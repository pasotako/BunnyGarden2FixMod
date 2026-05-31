using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Game;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 衣装系 Config (<c>*BreastFlatten</c> / <c>Tops*</c> / <c>Bottoms*</c> / 共有 smooth) の
/// <see cref="ConfigEntry{T}.SettingChanged"/> を一元購読し、影響 CharID を per-CharID dirty set に積んで
/// <see cref="LateUpdate"/> で次フレーム 1 回に coalesce して反映する単一窓口。同一 config を複数ハンドラが
/// 購読する購読順依存を避けるため、購読を本クラスに集約する。
///
/// 反映は <see cref="ReflectChar"/> が canonical 順 (<c>Bottoms.ApplyDirectly → Tops.ApplyDirectly</c>、
/// pure-native は <see cref="BreastFlattenApplier.Refresh"/>) で実行し、各 Apply 末尾の overlay/cloth 再適用
/// (既存) に委ねる。Bottoms-only override のみ <see cref="BottomsLoader"/>.Apply が cloth weight-shift を
/// 呼ばないため、本クラスが <see cref="BreastClothWeightShifter.ApplyFor"/> で補完する。
///
/// ライフタイム前提: host GameObject = Plugin の gameObject (process 生存・破棄されない)。dirty state は
/// static、flush は instance <see cref="LateUpdate"/>。host を破棄する経路を追加する場合は OnDestroy での
/// 再購読 / 再 flush を要検討 (現状は host 不変のため不要)。
/// </summary>
internal sealed class CostumeReflectionCoordinator : MonoBehaviour
{
    /// <summary>flush 時に invalidate するキャッシュ種別。config ごとに OR 集約する。</summary>
    [Flags]
    private enum CacheFlags
    {
        None = 0,
        Proxy = 1,             // BreastFlattenApplier.InvalidateProxyCache
        DistancePreserve = 2,  // TopsLoader.InvalidateDistancePreserveCache
        SkinShrink = 4,        // SkinShrinkCoordinator.InvalidateCache (+ RefreshAllByConfig)
    }

    /// <summary>各 config が影響する char とキャッシュ種別。<c>SingleChar==null</c> は全 target char。</summary>
    private readonly struct Descriptor
    {
        public readonly CharID? SingleChar;
        public readonly CacheFlags Flags;
        public Descriptor(CharID? singleChar, CacheFlags flags)
        {
            SingleChar = singleChar;
            Flags = flags;
        }
    }

    private static readonly Dictionary<object, Descriptor> s_configMap = new();
    private static readonly HashSet<CharID> s_dirtyChars = new();
    private static CacheFlags s_cacheFlags = CacheFlags.None;
    private static bool s_initialized;

    /// <summary>Plugin.Awake から呼ぶ。冪等。host GameObject に component を付け、config を購読する。</summary>
    public static void Initialize(GameObject parent)
    {
        if (s_initialized) return;
        s_initialized = true;

        if (parent != null) parent.AddComponent<CostumeReflectionCoordinator>();

        // *BreastFlatten ×6: 単一 char、proxy cache。
        Subscribe(Configs.ErisaBreastFlatten, CharID.ERISA, CacheFlags.Proxy);
        Subscribe(Configs.KanaBreastFlatten,  CharID.KANA,  CacheFlags.Proxy);
        Subscribe(Configs.LunaBreastFlatten,  CharID.LUNA,  CacheFlags.Proxy);
        Subscribe(Configs.RinBreastFlatten,   CharID.RIN,   CacheFlags.Proxy);
        Subscribe(Configs.MiukaBreastFlatten, CharID.MIUKA, CacheFlags.Proxy);
        Subscribe(Configs.KuonBreastFlatten,  CharID.KUON,  CacheFlags.Proxy);

        // *BreastWeightShift ×6: 単一 char、proxy cache（*BreastFlatten と同型。揺れ抑制倍率の live 反映）。
        Subscribe(Configs.ErisaBreastWeightShift, CharID.ERISA, CacheFlags.Proxy);
        Subscribe(Configs.KanaBreastWeightShift,  CharID.KANA,  CacheFlags.Proxy);
        Subscribe(Configs.LunaBreastWeightShift,  CharID.LUNA,  CacheFlags.Proxy);
        Subscribe(Configs.RinBreastWeightShift,   CharID.RIN,   CacheFlags.Proxy);
        Subscribe(Configs.MiukaBreastWeightShift, CharID.MIUKA, CacheFlags.Proxy);
        Subscribe(Configs.KuonBreastWeightShift,  CharID.KUON,  CacheFlags.Proxy);

        // BreastFlatten smooth (global): 全 char、proxy。
        Subscribe(Configs.BreastFlattenSmoothRadius,     null, CacheFlags.Proxy);
        Subscribe(Configs.BreastFlattenSmoothIterations, null, CacheFlags.Proxy);
        Subscribe(Configs.BreastFlattenSmoothStrength,   null, CacheFlags.Proxy);

        // 上半身肌 腰継ぎ目 weight conform (global): 全 char、proxy。conform は ApplyOverlay 内で適用されるため
        // Proxy refresh で再 apply される（toggle ON/OFF と近接距離とも live-tune 反映）。
        Subscribe(Configs.SkinUpperSeamConform,     null, CacheFlags.Proxy);
        Subscribe(Configs.SkinUpperSeamConformDist, null, CacheFlags.Proxy);

        // distance-preserve smooth (共有): 全 char、proxy + distancePreserve。
        Subscribe(Configs.DistancePreserveSmoothIterations, null, CacheFlags.Proxy | CacheFlags.DistancePreserve);
        Subscribe(Configs.DistancePreserveSmoothStrength,   null, CacheFlags.Proxy | CacheFlags.DistancePreserve);

        // 谷間 delta 縮小 (global): 実体は distance-preserve push のみだが、pure-native の refit は
        // BreastFlattenApplier.Refresh (proxy 経由で cloth→push→flatten を orchestrate) でトリガされるため
        // Proxy も付けて refit を確実に発火させる (DistancePreserveSmooth* と同型)。
        Subscribe(Configs.BreastFlattenCleavageShrink, null, CacheFlags.Proxy | CacheFlags.DistancePreserve);
        Subscribe(Configs.BreastFlattenCleavageWidth,  null, CacheFlags.Proxy | CacheFlags.DistancePreserve);

        // 下半身衣装の保護 (lower fade, global): 全 char。native fit (TryApplyDistancePreserve) の
        // 焼き込みに効くため distance-preserve cache を無効化 + Cleavage* と同型で Proxy も付けて refit を発火。
        Subscribe(Configs.BreastFlattenLowerFade,      null, CacheFlags.Proxy | CacheFlags.DistancePreserve);
        Subscribe(Configs.BreastFlattenLowerFadeWidth, null, CacheFlags.Proxy | CacheFlags.DistancePreserve);

        // Tops distance-preserve / additive push: 全 char、distancePreserve。
        Subscribe(Configs.TopsDistancePreserveRange, null, CacheFlags.DistancePreserve);
        Subscribe(Configs.TopsSkinMinOffset,         null, CacheFlags.DistancePreserve);
        Subscribe(Configs.TopsSkinSampleRadius,      null, CacheFlags.DistancePreserve);
        Subscribe(Configs.TopsSkinWeightFalloff,     null, CacheFlags.DistancePreserve);
        Subscribe(Configs.TopsAdditiveBreastPushOut, null, CacheFlags.DistancePreserve);

        // Tops skin shrink: 全 char、skinShrink (pure-native flatten の PushSkinUnderCloth も読むため全 char)。
        Subscribe(Configs.TopsSkinShrink,              null, CacheFlags.SkinShrink);
        Subscribe(Configs.TopsSkinShrinkFalloffRadius, null, CacheFlags.SkinShrink);
        Subscribe(Configs.TopsSkinShrinkSampleRadius,  null, CacheFlags.SkinShrink);

        // Bottoms skin shrink: 全 char、skinShrink。
        Subscribe(Configs.BottomsSkinShrink,              null, CacheFlags.SkinShrink);
        Subscribe(Configs.BottomsSkinShrinkFalloffRadius, null, CacheFlags.SkinShrink);
        Subscribe(Configs.BottomsSkinShrinkSampleRadius,  null, CacheFlags.SkinShrink);

        PatchLogger.LogInfo("[CostumeReflection] Initialized");
    }

    private static void Subscribe<T>(ConfigEntry<T> cfg, CharID? singleChar, CacheFlags flags)
    {
        if (cfg == null) return;
        s_configMap[cfg] = new Descriptor(singleChar, flags);
        cfg.SettingChanged += OnAnyConfigChanged;
    }

    private static void OnAnyConfigChanged(object sender, EventArgs e)
    {
        // sender は購読済 config (= s_configMap に存在) のはず。map 構築漏れ等の防御として no-op return する。
        if (sender == null || !s_configMap.TryGetValue(sender, out var desc)) return;
        if (desc.SingleChar.HasValue)
            s_dirtyChars.Add(desc.SingleChar.Value);
        else
            foreach (var c in BreastFlattenApplier.TargetCharIds) s_dirtyChars.Add(c);
        s_cacheFlags |= desc.Flags;
    }

    private void LateUpdate() => Flush();

    /// <summary>
    /// dirty char をまとめて反映する。同フレームの複数 SettingChanged が 1 回に集約される。
    /// <see cref="GBSystem.Instance"/> 不在 (ロード中) は dirty/flags を保持して次フレーム再試行。
    /// </summary>
    private static void Flush()
    {
        if (s_dirtyChars.Count == 0) return;

        var sys = GBSystem.Instance;
        if (sys == null) return;   // dirty/flags 保持 → 次フレーム再試行 (破棄済 Mesh を放置しない)
        var env = sys.GetActiveEnvScene();
        var holeScene = sys.GetHoleScene();
        if (env == null && holeScene == null) return;   // 同上、保持して再試行

        if ((s_cacheFlags & CacheFlags.Proxy) != 0)
            BreastFlattenApplier.InvalidateProxyCache();
        if ((s_cacheFlags & CacheFlags.DistancePreserve) != 0)
            TopsLoader.InvalidateDistancePreserveCache();
        if ((s_cacheFlags & CacheFlags.SkinShrink) != 0)
            SkinShrinkCoordinator.InvalidateCache();

        // env == HoleScene (Bar 滞在中) の二重 Apply を seen で防止。env != HoleScene (companion) は両 scene 走査。
        var seen = new HashSet<int>();
        foreach (var charId in s_dirtyChars)
        {
            ReflectChar(env != null ? env.FindCharacter(charId) : null, charId, seen);
            if (!ReferenceEquals(env, holeScene))
                ReflectChar(holeScene != null ? holeScene.FindCharacter(charId) : null, charId, seen);
        }

        // SkinShrink cache を消したまま ReflectChar で到達できなかった entry を孤児化させない安全網。
        // 残るのは FindCharacter で GameObject を取れなかった scene 取りこぼし entry のみ (通常起きない保険)。
        // seen の char を skip する理由: RefreshAllByConfig が skin を素から push し直すと、ReflectChar が
        // phase (g) で被せた BreastFlatten clone を上書きして flatten が消える (skin shrink だけ残る不具合)。
        // seen の char は ReflectChar で fresh な sharedMesh に再構築済みなので skip しても孤児化しない。
        // 例外で reflect 失敗した char も seen に積まれる (seen.Add は try 外) ため push 再適用対象外になるが、
        // 次回 config 変更で再 reflect される。
        if ((s_cacheFlags & CacheFlags.SkinShrink) != 0)
            SkinShrinkCoordinator.RefreshAllByConfig(seen);

        s_dirtyChars.Clear();
        s_cacheFlags = CacheFlags.None;
    }

    /// <summary>
    /// 単一 character を canonical 順で全反映する共通プリミティブ。
    /// Bottoms→Tops の固定順で <see cref="BottomsLoader.ApplyDirectly"/> / <see cref="TopsLoader.ApplyDirectly"/>
    /// を呼ぶ (= 内部 RestoreFor + Apply)。両 override 持ちでも setup() load と同順になり、既存 per-Kind snapshot の
    /// 不変条件 (Bottoms が native skin_upper を capture、Tops が Babydoll を capture) を保つ。
    ///
    /// 反映カバレッジ:
    ///   - pure-native: <see cref="BreastFlattenApplier.Refresh"/> が cloth→push→flatten を orchestrate。
    ///   - Tops override (Bottoms 併用含む): TopsLoader.Apply 末尾の cloth ApplyFor + ApplyOverlay (既存)。
    ///   - Bottoms-only: BottomsLoader.Apply 末尾の ApplyOverlay (skin) は既存だが cloth weight-shift は呼ばないため
    ///     ここで <see cref="BreastClothWeightShifter.ApplyFor"/>(flattenVerts:true) を補完
    ///     (BreastFlattenSetupPatch else 分岐 flattenVerts:!topsOverride=true と同型)。amount=0 は ApplyFor 内で no-op/restore。
    /// </summary>
    internal static void ReflectChar(GameObject character, CharID charId, HashSet<int> seen)
    {
        if (character == null) return;
        if (!seen.Add(character.GetInstanceID())) return;   // env==hole の二重 Apply 防止
        try
        {
            bool hasBottoms = BottomsOverrideStore.TryGet(charId, out var b);
            bool hasTops    = TopsOverrideStore.TryGet(charId, out var t);
            if (!hasBottoms && !hasTops)
            {
                BreastFlattenApplier.Refresh(character, charId);
                return;
            }
            if (hasBottoms) BottomsLoader.ApplyDirectly(character, b.DonorChar, b.DonorCostume);
            if (hasTops)    TopsLoader.ApplyDirectly(character, t.DonorChar, t.DonorCostume);
            // Bottoms-only のみ cloth weight-shift を補完する。Tops override 持ち (Bottoms 併用含む) は
            // TopsLoader.Apply 末尾の BreastClothWeightShifter.ApplyFor が cloth を担うため、二重適用回避で skip。
            // Bottoms-only は injected donor / 距離保存対象 SMR を持たないため flattenVerts:true 一律で正しい。
            // Tops 併用経路へ流用する場合は per-SMR モード保存が必要になるため要再設計。
            if (hasBottoms && !hasTops)
                BreastClothWeightShifter.ApplyFor(character, charId, flattenVerts: true);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[CostumeReflection] ReflectChar 失敗: char={charId}: {ex}");
        }
    }

    /// <summary>
    /// 衣装トグル解除 (Tops/Bottoms override の片方を外す) 用の全反映。
    /// <b>対象の <see cref="TopsOverrideStore"/> / <see cref="BottomsOverrideStore"/>.Clear を先に呼んでから</b>
    /// 実行すること (下記 skin_upper 所有権の収束がこの順序に依存する)。
    ///
    /// 当該 CharID の全 live character (env + HoleScene) で全 garment を native へ戻し
    /// (<see cref="TopsLoader.RestoreFor"/> → <see cref="BottomsLoader.RestoreFor"/>)、現在の store 状態から
    /// <see cref="ReflectChar"/> が canonical 順 (Bottoms→Tops) で再適用する。外した garment は RestoreFor で
    /// 視覚的に外れ、残存 override は native baseline から再構築されるため、cloth flatten / skin_upper swap /
    /// companion 同期が順序非依存で揃う。config は変えないため cache invalidate は不要 (ReflectChar は cache hit で再利用)。
    /// </summary>
    internal static void ReflectToggleOff(CharID charId)
    {
        var sys = GBSystem.Instance;
        if (sys == null) return;
        var env = sys.GetActiveEnvScene();
        var holeScene = sys.GetHoleScene();

        var seen = new HashSet<int>();
        FullResetAndReflect(env != null ? env.FindCharacter(charId) : null, charId, seen);
        if (!ReferenceEquals(env, holeScene))
            FullResetAndReflect(holeScene != null ? holeScene.FindCharacter(charId) : null, charId, seen);
    }

    private static void FullResetAndReflect(GameObject character, CharID charId, HashSet<int> seen)
    {
        if (character == null) return;
        // 全 garment を native へ戻す (apply は Bottoms→Tops 順なので reset は逆順 Tops→Bottoms)。
        // skin_upper 所有権の収束は「呼び順」ではなく "呼出元が store を先に Clear している" 前提で保証される:
        //   BottomsLoader.RestoreFor の OriginalSkinUpper→native rebase は !TopsOverrideStore.TryGet ガード下。
        //   - Tops 解除: TopsStore 空 → rebase 通過 → skin_upper を native(stable) baseline に戻す
        //     → 直後の Bottoms.Apply(ApplySkinUpperBabydollPhase) が Tops 不在で Babydoll swap し直す。
        //   - Bottoms 解除: Tops が store に残る → rebase skip → skin_upper は Tops 所有 Babydoll のまま (正しい)。
        // 残存 override は直後の ReflectChar が ApplyDirectly(=RestoreFor+Apply) で baseline から再構築する
        // (残存側 Loader.RestoreFor の二重呼出は snapshot 不在で restoredAny=false の no-op)。
        try
        {
            // RestoreFor 自体は try/catch を持たないため、1 scene の reset 失敗をここで握りつぶし、
            // ReflectChar・他 scene(companion)・UI Render の継続を保証する (ベストエフォート方針)。
            TopsLoader.RestoreFor(character);
            BottomsLoader.RestoreFor(character);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[CostumeReflection] FullResetAndReflect reset 失敗: char={charId}: {ex}");
        }
        // reset が部分的でも store 状態から再構築を試みる (ReflectChar 自身は try/catch 済み)。
        ReflectChar(character, charId, seen);
    }
}
