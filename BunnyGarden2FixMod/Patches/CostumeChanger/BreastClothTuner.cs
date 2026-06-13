using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using BepInEx.Configuration;
using GB;
using GB.Game;
using MagicaCloth2;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 胸 (bust) MagicaCloth の物理パラメータ (damping.value / worldInertia + BoneSpring の springPower)
/// を per-character × 2 軸 (Jiggle / Inertia) の倍率で live tune するモジュール。Jiggle 軸は内部で
/// damping + springPower の両方を書き換える。
///
/// 識別: <see cref="ClothProcess.ClothType.BoneCloth"/> または <see cref="ClothProcess.ClothType.BoneSpring"/>
/// AND rootBones 配下に <c>R_breast1_skinJT</c> または <c>L_breast1_skinJT</c> を含む
/// <see cref="MagicaCloth"/> instance。実機 (2026-05-23) では `Magica Cloth_Breast` が `BoneSpring` 型で
/// hit するが、設計上 BoneCloth も識別対象に含めて将来拡張に備える。
///
/// baseline cache <c>s_baselines</c> は per-MagicaCloth instanceId。scene unload では明示 Clear せず
/// (preserved character を温存)、Unity-null 化した instance は次回 ApplyFor の IsValid() /
/// SerializeData null チェックで自然 skip される。
/// </summary>
internal static class BreastClothTuner
{
    /// <summary>damping baseline 捕獲時のフォールバック値。MagicaCloth2 の ClothSerializeData.cs で
    /// <c>damping = new CurveSerializeData(0.05f)</c> が default 宣言値。SerializeData.damping が null の
    /// ケース (通常起きないが防衛) のとき、この値で baseline 化する。</summary>
    private const float DefaultDampingValue = 0.05f;

    /// <summary>SpringConstraint.springPower の default 値 (SpringConstraint.SerializeData ctor で 0.04f)。
    /// springConstraint が null のときの baseline fallback。</summary>
    private const float DefaultSpringPower = 0.04f;

    /// <summary>SpringConstraint.springPower の MagicaCloth 内 clamp 上限 (DataValidate で Math.Clamp(0.001, 1))。
    /// Jiggle = 0 のとき spring 最大化 (振幅 0 = 完全停止表現) に使う。</summary>
    private const float SpringPowerStopValue = 1.0f;

    private sealed class BustClothBaseline
    {
        public float Damping;       // damping.value のスナップショット (両 type 共通)
        public float WorldInertia;
        public float SpringPower;   // springConstraint.springPower (BoneSpring のみ使用、BoneCloth では未参照)
    }

    private sealed class TuneEntry
    {
        public BustClothBaseline Baseline;
        /// <summary>直近 Apply で書き込んだ値 (damping, inertia, springPower)。一致なら
        /// SetParameterChange skip。BoneCloth で springPower は NaN のまま。</summary>
        public (float D, float I, float S) LastApplied = (float.NaN, float.NaN, float.NaN);
    }

    private static readonly Dictionary<int, TuneEntry> s_baselines = new();
    private static readonly HashSet<int> s_logSeenBustAbsent = new();

    /// <summary>sender (ConfigEntry) → CharID の逆引き。Initialize で構築。</summary>
    private static readonly Dictionary<object, CharID> s_configToChar = new();

    private static bool s_initialized;

    /// <summary>
    /// Plugin.Awake から呼ぶ。12 *Breast{Jiggle,Inertia} に SettingChanged subscribe、
    /// scene unload subscribe。冪等。
    /// </summary>
    /// <param name="parent">現状未使用。<see cref="BreastFlattenApplier.Initialize"/> との API 整合のため受け取る。</param>
    public static void Initialize(GameObject parent)
    {
        _ = parent;
        if (s_initialized) return;
        s_initialized = true;

        BuildConfigIndex();
        SubscribeAll(true);
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        PatchLogger.LogInfo("[BreastClothTuner] Initialized");
    }

    /// <summary>config entry → CharID マップを構築 (OnConfigChanged の sender 逆引き用)。</summary>
    private static void BuildConfigIndex()
    {
        TryIndex(Configs.ErisaBreastJiggle,  CharID.ERISA);
        TryIndex(Configs.ErisaBreastInertia, CharID.ERISA);
        TryIndex(Configs.KanaBreastJiggle,   CharID.KANA);
        TryIndex(Configs.KanaBreastInertia,  CharID.KANA);
        TryIndex(Configs.LunaBreastJiggle,   CharID.LUNA);
        TryIndex(Configs.LunaBreastInertia,  CharID.LUNA);
        TryIndex(Configs.RinBreastJiggle,    CharID.RIN);
        TryIndex(Configs.RinBreastInertia,   CharID.RIN);
        TryIndex(Configs.MiukaBreastJiggle,  CharID.MIUKA);
        TryIndex(Configs.MiukaBreastInertia, CharID.MIUKA);
        TryIndex(Configs.KuonBreastJiggle,   CharID.KUON);
        TryIndex(Configs.KuonBreastInertia,  CharID.KUON);
    }

    private static void TryIndex(ConfigEntry<float> cfg, CharID id)
    {
        if (cfg != null) s_configToChar[cfg] = id;
    }

    private static void SubscribeAll(bool subscribe)
    {
        foreach (var kv in s_configToChar)
        {
            if (kv.Key is ConfigEntry<float> cfg)
            {
                if (subscribe) cfg.SettingChanged += OnConfigChanged;
                else cfg.SettingChanged -= OnConfigChanged;
            }
        }
    }

    /// <summary>
    /// <paramref name="character"/> 配下の bust cloth (BoneCloth または BoneSpring) すべてに対し、
    /// CharID 別の倍率を適用する。baseline 未捕獲なら初回捕獲する。
    /// LastApplied 比較で no-op 時は SetParameterChange skip。
    /// </summary>
    public static void ApplyFor(GameObject character, CharID charId)
    {
        if (character == null) return;

        var clothList = FindBustClothComponents(character);
        if (clothList.Count == 0)
        {
            int key = character.GetInstanceID();
            if (s_logSeenBustAbsent.Add(key))
            {
                PatchLogger.LogDebug($"[BreastClothTuner] bust cloth 不在 ({character.name}, char={charId})");
            }
            return;
        }

        float jiggleMul = ResolveJiggle(charId);
        float inertiaMul = ResolveInertia(charId);

        foreach (var mc in clothList)
        {
            if (mc == null) continue;
            if (!mc.IsValid()) continue;  // runtime build 未完了は skip、次 cycle で retry
            if (mc.SerializeData == null) continue;

            int instanceId = mc.GetInstanceID();
            if (!s_baselines.TryGetValue(instanceId, out var entry))
            {
                entry = new TuneEntry
                {
                    Baseline = CaptureBaseline(mc),
                };
                s_baselines[instanceId] = entry;
            }

            bool isSpring = mc.SerializeData.clothType == ClothProcess.ClothType.BoneSpring;
            var (newD, newI, newS) = ComputeTargets(entry.Baseline, jiggleMul, inertiaMul, isSpring);

            // LastApplied と一致 → no-op。BoneCloth では newS / LastApplied.S が NaN になるため
            // SameOrNan で NaN==NaN を等しいと扱う (`Mathf.Approximately(NaN, NaN)` は false を返し、
            // 素直な比較だと no-op 最適化が壊れる)。
            if (SameOrNan(entry.LastApplied.D, newD) &&
                SameOrNan(entry.LastApplied.I, newI) &&
                SameOrNan(entry.LastApplied.S, newS))
            {
                continue;
            }

            if (mc.SerializeData.damping != null)
            {
                mc.SerializeData.damping.value = newD;      // value field のみ直接書換 (curve / useCurve 無触り)
            }
            if (mc.SerializeData.inertiaConstraint != null)
            {
                mc.SerializeData.inertiaConstraint.worldInertia = newI;
            }
            // BoneSpring では揺れを springConstraint.springPower が支配するため Jiggle 軸はこちらが主。
            // BoneCloth では newS = NaN で skip (SpringConstraintParams.Convert が
            // `(clothType == BoneSpring && useSpring) ? springPower : 0f` で effective 値を 0 化するため、
            // 書換しても物理に反映されない)。
            if (isSpring && !float.IsNaN(newS) && mc.SerializeData.springConstraint != null)
            {
                mc.SerializeData.springConstraint.springPower = newS;
            }
            entry.LastApplied = (newD, newI, newS);
            mc.SetParameterChange();

            PatchLogger.LogDebug($"[BreastClothTuner] Apply {character.name} char={charId} mc={mc.name} type={mc.SerializeData.clothType} D={newD:F4} I={newI:F3} S={newS:F4}");
        }
    }

    /// <summary>
    /// character 配下から bust 物理 cloth を全て返す。
    /// 判定: clothType が BoneCloth または BoneSpring AND rootBones 配下に
    /// <c>R_breast1_skinJT</c> または <c>L_breast1_skinJT</c> を含む。
    ///
    /// BoneSpring 固有の制約: <c>ClothSerializeData.Convert()</c> で
    /// <c>result.gravity = (clothType == BoneSpring) ? 0f : gravity;</c> となるため BoneSpring では
    /// gravity が常に 0 化されうる。damping (Jiggle 軸) と worldInertia (Inertia 軸) は両 type 共通で書換有効。
    /// </summary>
    private static List<MagicaCloth> FindBustClothComponents(GameObject character)
    {
        var result = new List<MagicaCloth>();
        if (character == null) return result;

        var all = character.GetComponentsInChildren<MagicaCloth>(includeInactive: true);
        foreach (var mc in all)
        {
            if (mc == null) continue;
            if (mc.SerializeData == null) continue;
            var ct = mc.SerializeData.clothType;
            if (ct != ClothProcess.ClothType.BoneCloth && ct != ClothProcess.ClothType.BoneSpring) continue;
            if (ContainsBreastBone(mc))
            {
                result.Add(mc);
            }
        }
        return result;
    }

    /// <summary>
    /// rootBones (駆動 bone リスト) の子孫を再帰 walk して R/L_breast1_skinJT を含むか判定。
    /// </summary>
    private static bool ContainsBreastBone(MagicaCloth mc)
    {
        if (mc.SerializeData?.rootBones == null) return false;
        foreach (var root in mc.SerializeData.rootBones)
        {
            if (root == null) continue;
            if (TransformContainsBreast(root)) return true;
        }
        return false;
    }

    private static bool TransformContainsBreast(Transform t)
    {
        if (t == null) return false;
        if (t.name == "R_breast1_skinJT" || t.name == "L_breast1_skinJT") return true;
        for (int i = 0; i < t.childCount; i++)
        {
            if (TransformContainsBreast(t.GetChild(i))) return true;
        }
        return false;
    }

    private static BustClothBaseline CaptureBaseline(MagicaCloth mc)
    {
        return new BustClothBaseline
        {
            Damping = mc.SerializeData.damping?.value ?? DefaultDampingValue,
            WorldInertia = mc.SerializeData.inertiaConstraint?.worldInertia ?? 0f,
            SpringPower = mc.SerializeData.springConstraint?.springPower ?? DefaultSpringPower,
        };
    }

    /// <summary>
    /// 倍率 → 適用値計算。
    /// - damping: jiggleMul = 0 なら 1.0f (= MagicaCloth Clamp(0,1) 上限)、それ以外は baseline / jiggleMul。
    ///            両 type で書換有効だが、BoneSpring では spring constraint が物理を支配するため Jiggle 軸の
    ///            主効果は spring 側、damping は補助。
    /// - inertia: baseline * inertiaMul (MagicaCloth 内 Clamp01 で頭打ち)
    /// - spring: BoneSpring のみ。jiggleMul = 0 なら <see cref="SpringPowerStopValue"/> (上限 clamp = 振幅 0 ≒ 完全停止)、
    ///           それ以外は baseline / jiggleMul (大きい multiplier = 弱い spring = 大振幅、damping と同じ意味論)。
    ///           BoneCloth 系では <c>NaN</c> を返し、Apply 側で書換 skip。
    /// </summary>
    private static (float D, float I, float S) ComputeTargets(
        BustClothBaseline baseline, float jiggleMul, float inertiaMul, bool isSpring)
    {
        float newD = (jiggleMul <= 0f) ? 1.0f : (baseline.Damping / jiggleMul);
        float newI = baseline.WorldInertia * inertiaMul;
        float newS = isSpring
            ? ((jiggleMul <= 0f) ? SpringPowerStopValue : (baseline.SpringPower / jiggleMul))
            : float.NaN;
        return (newD, newI, newS);
    }

    private static float ResolveJiggle(CharID id) => id switch
    {
        CharID.ERISA => Configs.ErisaBreastJiggle.Value,
        CharID.KANA  => Configs.KanaBreastJiggle.Value,
        CharID.LUNA  => Configs.LunaBreastJiggle.Value,
        CharID.RIN   => Configs.RinBreastJiggle.Value,
        CharID.MIUKA => Configs.MiukaBreastJiggle.Value,
        CharID.KUON  => Configs.KuonBreastJiggle.Value,
        _ => 1f,
    };

    private static float ResolveInertia(CharID id) => id switch
    {
        CharID.ERISA => Configs.ErisaBreastInertia.Value,
        CharID.KANA  => Configs.KanaBreastInertia.Value,
        CharID.LUNA  => Configs.LunaBreastInertia.Value,
        CharID.RIN   => Configs.RinBreastInertia.Value,
        CharID.MIUKA => Configs.MiukaBreastInertia.Value,
        CharID.KUON  => Configs.KuonBreastInertia.Value,
        _ => 1f,
    };

    /// <summary>
    /// 両側が NaN の場合に等しいと扱う近似比較。
    /// <see cref="Mathf.Approximately"/> は NaN 同士を false として返すため、BoneCloth 経路で
    /// LastApplied.S = NaN / newS = NaN を等しいと判定したい no-op 最適化が壊れる。これを補正する。
    /// </summary>
    private static bool SameOrNan(float a, float b)
    {
        if (float.IsNaN(a) && float.IsNaN(b)) return true;
        return Mathf.Approximately(a, b);
    }

    /// <summary>
    /// SettingChanged ハンドラ。sender (ConfigEntry) から CharID を逆引きし、
    /// その character のみ Refresh する (全 char ループしない)。
    /// </summary>
    private static void OnConfigChanged(object sender, System.EventArgs e)
    {
        if (!s_configToChar.TryGetValue(sender, out var charId))
        {
            // 既知の cfg 群以外から発火 (意図しない subscribe / mock テスト等)。
            PatchLogger.LogWarning($"[BreastClothTuner] OnConfigChanged: sender → CharID 解決失敗 ({sender?.GetType().Name})");
            return;
        }

        var sys = GBSystem.Instance;
        if (sys == null) return;
        var env = sys.GetActiveEnvScene();
        var holeScene = sys.GetHoleScene();

        var seen = new HashSet<int>();
        TryRefresh(env?.FindCharacter(charId), charId, seen);
        if (!ReferenceEquals(env, holeScene))
            TryRefresh(holeScene?.FindCharacter(charId), charId, seen);
    }

    private static void TryRefresh(GameObject character, CharID charId, HashSet<int> seen)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        if (!seen.Add(id)) return;
        ApplyFor(character, charId);
    }

    /// <summary>
    /// scene unload 時のハンドラ。
    ///
    /// `s_baselines` は明示 Clear しない (m_holeScene preserved character の baseline を保護、
    /// Unity-null 化した entry は次回 ApplyFor の IsValid() / SerializeData null チェックで
    /// 自然 skip される)。
    ///
    /// `s_logSeenBustAbsent` は scene cycle ごとに Clear し、新 scene で character GameObject が
    /// destroy → recreate されて InstanceID が新規発番されても "bust 不在" log が 1 回出るように戻す。
    /// 同 scene 内での重複出力は引き続き抑制される (scene 跨ぎでのみリセット)。
    /// </summary>
    private static void OnSceneUnloaded(Scene scene)
    {
        s_logSeenBustAbsent.Clear();
    }
}
