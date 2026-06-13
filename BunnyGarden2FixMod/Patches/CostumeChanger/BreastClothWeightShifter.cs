using System.Collections.Generic;
using BepInEx.Configuration;
using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB;
using GB.Game;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// cloth (<c>mesh_costume*</c>) の breast 領域 vertex の boneWeight を <c>R/L_breast1_skinJT</c> から
/// <c>Spine3_skinJT</c> へ flatten amount 比例で再配分し、cloth の breast bone 由来 motion を抑制する機構。
///
/// 目的: <see cref="BreastFlattenApplier"/> で skin が flatten され radius=0 で動きを失った状態でも、
/// cloth (mesh_costume) は元 cup 位置で大きく揺れる非対称 ([[flatten-bone-rotation-asymmetry]]) を解消する。
/// 本機構は cloth 側の bone 影響を Spine3 寄せにすることで、motion 量を skin に揃える。
///
/// 算法:
///   per Tops 候補 SMR (<see cref="TopsLoader.IsTopsCandidate"/>):
///     1. R/L_breast1_skinJT idx 解決 (bones[] 内、両方欠如なら SMR skip)
///     2. parent idx 解決 (Spine3 → Spine2 → Spine1 の順 fallback、全欠如なら SMR skip)
///     3. amount=0 / clone suffix 既存 → 適切に no-op or recover
///     4. sharedMesh clone → per-vert で breast slot を (1 - amount) 倍に減衰、削減分を parent slot に集約
///     5. clone.boneWeights 書込 + smr.sharedMesh 差替 + Entry に登録 (前 clone は Destroy)
///
/// Hook 経路:
///   1. <see cref="BreastFlattenSetupPatch"/> (CharacterHandle.setup Postfix) — 素キャラ向け
///   2. <see cref="TopsLoader"/>.Apply の ApplySkinShrinkPhase 完了直後 — Tops 移植 cycle
///   3. <see cref="TopsLoader"/>.RestoreFor の <see cref="BreastFlattenApplier.RestoreFor"/> 隣 — clone 破棄
///
/// 設計参照: <see cref="BreastFlattenApplier"/> (entry lifecycle / SettingChanged / scene unload)
/// と <see cref="BreastClothTuner"/> (sender→CharID 逆引き) の構造を踏襲。
///
/// 数学的性質: Linear skinning では weight 平行移動 (breast → parent) が bind pose 頂点位置を変えないため、
/// 静止 frame では本機構による視覚差は無い。runtime motion のみ変化する。
/// </summary>
internal static class BreastClothWeightShifter
{
    // suffix リテラルは Internal/ModMeshSuffixes に一元管理（規約違反検出との二重管理回避）。
    private const string ShiftCloneSuffix = Internal.ModMeshSuffixes.BreastShift;

    // Babydoll skin donor preload の二重起動防止。
    private static readonly HashSet<CharID> s_skinDonorPreloadInFlight = new();

    private sealed class SmrSnapshot
    {
        public SkinnedMeshRenderer Smr;
        public Mesh BaseMesh;          // Apply 直前の素 sharedMesh (Tops 移植直後の素)
        public Mesh PrevClone;         // SMR.sharedMesh に乗っている _breastshift clone
        public float LastAmount;       // flatten 量 (形状 cache key)
        public float LastShiftAmount;  // weight 移送量 = flatten 量 × 揺れ抑制倍率 (倍率だけ変えても再 apply させる cache key)
        public bool LastFlattenVerts;  // この snapshot が頂点 flatten 込みで適用されたか (cache-hit / live tune 引き継ぎ用)
    }

    private sealed class Entry
    {
        public GameObject Character;
        public Dictionary<int, SmrSnapshot> PerSmr = new();   // key: smr.GetInstanceID()
    }

    private static readonly Dictionary<int, Entry> s_entries = new();

    private static bool s_initialized;
    private static bool s_sceneUnloadSubscribed;

    /// <summary>
    /// Plugin.Awake から呼ぶ。冪等: 二重呼出は no-op。
    /// config 変更の live tune は <see cref="CostumeReflectionCoordinator"/> が一元処理する。
    /// </summary>
    public static void Initialize(GameObject parent)
    {
        _ = parent;     // 将来 coroutine host 用に予約 (BreastClothTuner.Initialize と API 整合)
        if (s_initialized) return;
        s_initialized = true;

        PatchLogger.LogInfo("[BreastClothWeightShifter] Initialized");
    }

    /// <summary>
    /// native distance-preserve に必要な (charId, Babydoll) skin donor の preload を保証する。
    /// 既に ready なら何もしない。未完なら fire-and-forget で preload し、完了後に <see cref="Refresh"/> して
    /// distance-preserve を反映する（preload 完了までは <see cref="ApplyToSmr"/> が旧 FlattenClonedMesh に fallback）。
    /// </summary>
    private static void EnsureSkinDonorPreloaded(GameObject character, CharID charId)
    {
        if (character == null || charId >= CharID.NUM) return;
        if (TopsLoader.TryGetSkinDonorSmrs(charId, out _, out _)) return;   // 既に ready
        if (!s_skinDonorPreloadInFlight.Add(charId)) return;                 // 起動済
        PreloadSkinDonorAndReapply(charId).Forget();
    }

    private static async UniTaskVoid PreloadSkinDonorAndReapply(CharID charId)
    {
        try
        {
            await TopsLoader.PreloadDonorAsync(charId, TopsLoader.SkinDonorCostume);
        }
        finally
        {
            s_skinDonorPreloadInFlight.Remove(charId);
        }
        // 起動元 1 体に限定すると、同 CharID 2 体（同伴: env 側 + HoleScene preserved）の片方が
        // fallback flatten に取り残されるため CharID 単位で全 scene を走査する。
        // pure-native は ReapplyForCharId→Refresh が cloth→push→flatten を一括 orchestrate するため
        // RefreshCharAllScenes は不要（呼ぶと cloth を二重 refresh する）。Tops/Bottoms char は flatten のみ
        // ReapplyForCharId で再適用し、cloth は RefreshCharAllScenes で別途 refresh する。
        BreastFlattenApplier.ReapplyForCharId(charId);
        if (!BreastFlattenApplier.IsPureNativeFlatten(charId))
            RefreshCharAllScenes(charId);
    }

    /// <summary>
    /// <paramref name="character"/> 配下の Tops 候補 SMR すべてに weight shift を適用する。
    /// amount=0 のときは既存 entry あれば restore、なければ allocation 0 で完全 no-op。
    /// </summary>
    /// <param name="flattenVerts">
    /// true のとき clone の頂点も breast bone 原点へ flatten する (native costume 用)。
    /// MOD Tops 移植 cloth は <see cref="MeshDistancePreserver"/> で flatten 済 skin に距離保存済のため
    /// false (頂点 flatten すると二重収縮で破綻する)。weight shift 自体は両モードで実行される。
    /// </param>
    /// <param name="distancePreservedSmrIds">
    /// 距離保存済 (= 頂点 flatten 不要) の SMR instanceID 集合。<paramref name="flattenVerts"/>=true でも
    /// この集合に含まれる SMR は flat=false に固定する (additive Tops で injected donor は距離保存済・温存 costume は
    /// 距離保存対象外なので per-SMR で分岐するため)。null なら全 SMR が <paramref name="flattenVerts"/> 一律 (既存挙動)。
    /// </param>
    public static void ApplyFor(GameObject character, CharID charId, bool flattenVerts,
                                HashSet<int> distancePreservedSmrIds = null)
    {
        if (character == null) return;

        float amount = ResolveAmount(charId);
        if (amount <= 0f)
        {
            // amount=0: 既存 shift 状態を巻き戻すのみ、新規 clone 生成しない
            if (s_entries.ContainsKey(character.GetInstanceID()))
            {
                RestoreFor(character);
            }
            return;
        }

        // native flatten 経路は Babydoll skin を distance-preserve の reference に使うため preload を保証。
        if (flattenVerts)
            EnsureSkinDonorPreloaded(character, charId);

        var smrs = character.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
        bool processedAny = false;
        foreach (var smr in smrs)
        {
            if (smr == null) continue;
            if (!TopsLoader.IsTopsCandidate(smr)) continue;
            // per-SMR flatten 判定:
            //  - 距離保存済 cloth(injected donor) は二重収縮回避で flatten しない。
            //  - non-readable cloth は頂点を読めず flatten 無音失敗するので weight-shift only に落とす
            //    (isReadable ガードは distancePreservedSmrIds=null の SetupPatch/Refresh 経路にも効く)。
            bool flat = flattenVerts
                        && (distancePreservedSmrIds == null || !distancePreservedSmrIds.Contains(smr.GetInstanceID()))
                        && smr.sharedMesh != null && smr.sharedMesh.isReadable;
            if (ApplyToSmr(character, charId, smr, amount, flat)) processedAny = true;
        }

        if (processedAny) EnsureSceneUnloadSubscribed();

        // cloth は distance-preserve で flatten skin に追従。肌の貫通対策 (SkinShrink) は
        // BreastFlattenApplier.ApplyOverlay 内の PushSkinUnderCloth が pure-native で実施する
        // （setup patch / live-tune で cloth → ApplyOverlay の順を保証）。
    }

    /// <summary>
    /// 1 SMR への適用本体。touched=0 (breast vert 不在) なら clone Destroy + entry 不登録の早期 return。
    /// 戻り値: 実際に weight 書込まで進んだ場合 true。
    /// </summary>
    private static bool ApplyToSmr(GameObject character, CharID charId, SkinnedMeshRenderer smr, float amount, bool flattenVerts)
    {
        // memory feedback_native_smr_registry_invariant: sharedMesh を _breastshift clone に
        // 差し替える前に Registry に native (= addressables stable mesh) を確定させる。
        Internal.NativeSmrRegistry.GetOrCapture(character, smr);
        var baseMesh = smr.sharedMesh;
        if (baseMesh == null) return false;

        // weight 移送量は flatten 量 × 揺れ抑制倍率。flatten 形状(amount)とは別軸で扱う。
        float shiftAmount = BreastFlattenApplier.ResolveWeightShiftAmount(charId);

        int charKey = character.GetInstanceID();
        if (!s_entries.TryGetValue(charKey, out var entry))
        {
            entry = new Entry { Character = character };
            s_entries[charKey] = entry;
        }

        int smrKey = smr.GetInstanceID();
        entry.PerSmr.TryGetValue(smrKey, out var snap);

        // 二重 Apply 防止 + リカバリ
        // smr.sharedMesh が _breastshift suffix を持つ = 前 cycle clone が乗ったまま
        if (baseMesh.name.EndsWith(ShiftCloneSuffix))
        {
            if (snap != null && snap.PrevClone == baseMesh && Mathf.Approximately(snap.LastAmount, amount)
                && Mathf.Approximately(snap.LastShiftAmount, shiftAmount)
                && snap.LastFlattenVerts == flattenVerts)
            {
                return false;   // 同 amount + 同 shiftAmount + 同 flatten モードで既に適用済
            }
            PatchLogger.LogWarning($"[BreastClothWeightShifter] _breastshift suffix 残存 (smr={smr.name}) → 強制 restore & 再 apply");
            if (snap != null) RestoreSmr(smr, snap);
            entry.PerSmr.Remove(smrKey);
            snap = null;
            baseMesh = smr.sharedMesh;
            if (baseMesh == null || baseMesh.name.EndsWith(ShiftCloneSuffix))
            {
                // restore 後も clone のまま (smr.sharedMesh が別経路で差替済) → 諦め
                return false;
            }
        }

        // cache hit: 同 baseMesh + 同 amount + 同 shiftAmount + 同 flatten モードで snapshot 既存 → no-op
        if (snap != null && snap.BaseMesh == baseMesh && Mathf.Approximately(snap.LastAmount, amount)
            && Mathf.Approximately(snap.LastShiftAmount, shiftAmount)
            && snap.LastFlattenVerts == flattenVerts)
        {
            return false;
        }

        var bones = smr.bones ?? System.Array.Empty<Transform>();
        if (bones.Length == 0) return false;

        var (rIdx, lIdx) = ResolveBreastBoneIndices(bones);
        if (rIdx < 0 && lIdx < 0) return false;

        int parentIdx = BreastWeightShiftMath.ResolveParentBoneIndex(bones);
        if (parentIdx < 0)
        {
            PatchLogger.LogWarning($"[BreastClothWeightShifter] parent bone (Spine3/2/1_skinJT) 不在 ({character.name}/{smr.name})");
            return false;
        }

        var clone = Object.Instantiate(baseMesh);
        clone.name = baseMesh.name + ShiftCloneSuffix;

        var weights = clone.boneWeights;
        if (weights == null || weights.Length == 0)
        {
            Object.Destroy(clone);
            return false;
        }

        // native costume: clone を round Babydoll → flat Babydoll の skin 差分で distance-preserve し、
        // cloth を flatten 済 skin に追従させる (TopsOverride 同等)。視覚 skin は swap せず、Babydoll は
        // hidden proxy として計算 reference にのみ使う。preload 未完 / カバー外 / Preserve null のときは
        // 旧 FlattenClonedMesh に fallback して flatten を最低限保証する (preload 未完は完了後 Refresh で再補正)。
        if (flattenVerts)
        {
            if (!TryApplyDistancePreserve(charId, smr, clone))
            {
                BreastFlattenApplier.FlattenClonedMesh(clone, smr, amount);
            }
            // distance-preserve が clone.boneWeights を焼いた可能性があるので weights を再読込し、
            // 後続 weight shift ループの入力に反映する（boneWeight blend と weight shift を併用、Tops 経路と同一）。
            weights = clone.boneWeights;
        }

        int touched = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            float wR = (rIdx >= 0) ? BreastWeightShiftMath.GetWeightForBone(weights[i], rIdx) : 0f;
            float wL = (lIdx >= 0) ? BreastWeightShiftMath.GetWeightForBone(weights[i], lIdx) : 0f;
            float wBreast = wR + wL;
            if (wBreast < BreastWeightShiftMath.BreastWeightThreshold) continue;
            weights[i] = BreastWeightShiftMath.RedistributeWeight(weights[i], rIdx, lIdx, parentIdx, shiftAmount);
            touched++;
        }

        if (touched == 0)
        {
            // breast vert 不在 (e.g. mesh_costume_sleeve)。clone 廃棄 + entry 不登録
            Object.Destroy(clone);
            return false;
        }

        clone.boneWeights = weights;

        // 前 clone を Destroy してから差替
        if (snap != null && snap.PrevClone != null)
        {
            Object.Destroy(snap.PrevClone);
        }

        smr.sharedMesh = clone;

        // snapshot 登録 / 更新 (BaseMesh は常に最新で上書き = stale 問題対策)
        if (snap == null) snap = new SmrSnapshot { Smr = smr };
        snap.BaseMesh = baseMesh;
        snap.PrevClone = clone;
        snap.LastAmount = amount;
        snap.LastShiftAmount = shiftAmount;
        snap.LastFlattenVerts = flattenVerts;
        entry.PerSmr[smrKey] = snap;

        var parentBone = (parentIdx >= 0 && parentIdx < bones.Length) ? bones[parentIdx] : null;
        PatchLogger.LogDebug($"[BreastClothWeightShifter] Apply {character.name}/{smr.name} amount={amount:F2} shift={shiftAmount:F2} flat={flattenVerts} touched={touched}/{weights.Length} parent={(parentBone != null ? parentBone.name : "<null>")}");
        return true;
    }

    /// <summary>
    /// native 衣装 <paramref name="clone"/> を round Babydoll → flat Babydoll の skin 差分で distance-preserve し、
    /// clone の頂点を補正後の値で上書きする。補正後 boneWeight (TopsSkinWeightFalloff による肌追従 blend) も
    /// clone に焼く（後続 weight shift ループの入力になる = Tops 経路と同じく blend と weight shift を併用）。
    ///
    /// 戻り値 true  = distance-preserve を clone に適用した。
    /// 戻り値 false = preload 未完 / proxy 生成失敗 / Preserve null（カバー外等）/ 頂点数不一致。
    ///                呼出側は旧 FlattenClonedMesh に fallback して flatten を最低限保証する。
    /// </summary>
    private static bool TryApplyDistancePreserve(CharID charId, SkinnedMeshRenderer smr, Mesh clone)
    {
        if (!TopsLoader.TryGetSkinDonorSmrs(charId, out var donorSkinUpper, out var donorSkinLower))
            return false;   // Babydoll 未 preload → fallback（preload 完了後に Refresh で再来）

        // target upper = flatten 済 Babydoll proxy（hidden）。amount=0 / 生成失敗で null。
        var targetSkinUpper = BreastFlattenApplier.GetFlattenedReferenceSmr(donorSkinUpper, charId);
        if (targetSkinUpper == null) return false;   // amount=0 / proxy 失敗 → fallback

        // lower は flatten 対象外。donor/target で同一 SMR を渡すと Preserve 内で lower 領域の
        // d_donor==d_target → push=0 となり補正されない（breast 専用補正、意図通り）。
        var donorSkinSmrs  = new[] { donorSkinUpper, donorSkinLower };
        var targetSkinSmrs = new[] { targetSkinUpper, donorSkinLower };

        // Tops 経路と同挙動: boneWeight blend (Pass4) を常時 TopsSkinWeightFalloff で有効化。
        // weight shift は後続ループで常に適用される（Pass4 と併用、Tops 経路と同一）。
        float weightFalloff = Configs.TopsSkinWeightFalloff.Value;

        // フェード基準: clone はこの時点で base mesh の Instantiate 直後 = 補正前 (native) 状態。
        // Preserve 出力を腰ラインより下で base へ引き戻すため、補正前の頂点 / boneWeight を退避する。
        var basePv = clone.vertices;
        var baseBw = clone.boneWeights;

        // smr.sharedMesh はこの時点で baseMesh（round native）。Preserve はそれを donor cloth として補正する。
        var preserved = MeshDistancePreserver.Preserve(
            smr, donorSkinSmrs, targetSkinSmrs,
            maxNeighborDist: Configs.TopsDistancePreserveRange.Value,
            minOffset: Configs.TopsSkinMinOffset.Value,
            skinSampleRadius: Configs.TopsSkinSampleRadius.Value,
            weightFalloffOuter: weightFalloff,
            smoothIterations: Configs.DistancePreserveSmoothIterations.Value,
            smoothStrength: Configs.DistancePreserveSmoothStrength.Value,
            // native costume flatten (温存 swimsuit 等の内層) は突き抜け対策の対象外なので push-out 無し。
            breastPushOut: 0f,
            // 谷間 (cleavage) で衣装が平らな body 上に浮くのを縮小（pure-native / Bottoms-only native fit のみ）。
            // 強度はキャラ個別 flat 量に線形比例。手前の GetFlattenedReferenceSmr で amount>0 保証済。
            cleavageShrink: Configs.BreastFlattenCleavageShrink.Value * BreastFlattenApplier.ResolveAmount(charId),
            cleavageWidth: Configs.BreastFlattenCleavageWidth.Value,
            logTag: "BreastFlattenNative");

        if (preserved == null)
            return false;   // カバー外/失敗の可能性 → flatten 退行を防ぐため fallback flatten へ

        bool applied = false;
        var pv = preserved.vertices;
        var pbw = preserved.boneWeights;
        if (pv.Length == clone.vertexCount)
        {
            // full-body 衣装 (1 枚 mesh_costume) では skin_lower 域 (腰ラインより下) の衣装頂点へ胸 flatten の
            // 距離保存補正が漏れる。Babydoll lower の max Y を腰ラインに、pv/pbw を base へ Y フェードで引き戻す。
            // OFF / lower 非 readable / NaN / 長さ不一致 のときは何もしない (= 全域焼き、bit-identical)。
            if (Configs.BreastFlattenLowerFade.Value
                && donorSkinLower != null && donorSkinLower.sharedMesh != null
                && donorSkinLower.sharedMesh.isReadable)
            {
                float yTop = BreastClothLowerFadeMath.ComputeFadeTopY(donorSkinLower.sharedMesh.vertices);
                int fadedCount = BreastClothLowerFadeMath.ApplyLowerFade(
                    basePv, pv, baseBw, pbw, yTop, Configs.BreastFlattenLowerFadeWidth.Value);
                if (fadedCount > 0)
                    PatchLogger.LogDebug($"[BreastFlattenNative] lower fade smr={smr.name} yTop={yTop:F4} faded(f<1)={fadedCount}/{pv.Length}");
            }

            // 補正成分 disp の平滑化 (ガタつき除去) は Preserve 内 (MeshDisplacementSmoother) で実施済み。
            // pv = base + smoothed_disp なので clone へそのまま焼く（lower fade 適用後）。
            clone.vertices = pv;
            clone.RecalculateNormals();
            clone.RecalculateBounds();
            // blend 済 boneWeight を clone に焼く。後続 weight shift が weights 再読込で取り込み、
            // breast→Spine3 shift を上書き適用する（Tops 経路と同一）。TopsSkinWeightFalloff=0 のときは
            // preserved.boneWeights == baseMesh weights のため焼いても実質 no-op。
            clone.boneWeights = pbw;
            applied = true;
        }
        else
        {
            PatchLogger.LogWarning($"[BreastFlattenNative] 頂点数不一致 (smr={smr.name}, preserved={pv.Length}/{clone.vertexCount}) — 補正破棄");
        }

        Object.Destroy(preserved);
        return applied;   // 頂点数不一致なら false → fallback flatten
    }

    /// <summary>
    /// <paramref name="character"/> に登録された全 SMR snapshot を巻き戻し + clone 破棄。
    /// </summary>
    public static void RestoreFor(GameObject character)
    {
        if (character == null) return;
        int charKey = character.GetInstanceID();
        if (!s_entries.TryGetValue(charKey, out var entry)) return;

        foreach (var kv in entry.PerSmr)
        {
            RestoreSmr(kv.Value.Smr, kv.Value);
        }
        s_entries.Remove(charKey);
    }

    private static void RestoreSmr(SkinnedMeshRenderer smr, SmrSnapshot snap)
    {
        if (snap == null) return;
        // memory feedback_native_smr_registry_invariant: Registry が native の単一権威。
        // Registry が fake-null/未登録なら null 代入で SMR 非描画 (元から null と同等の意図動作)。
        // 直後 Destroy(PrevClone) で destroyed Mesh が sharedMesh に残るのを防ぐ。
        if (smr != null && smr.sharedMesh == snap.PrevClone)
        {
            smr.sharedMesh = Internal.NativeSmrRegistry.TryGet(smr);
        }
        if (snap.PrevClone != null) Object.Destroy(snap.PrevClone);
    }

    /// <summary>scene unload で全 entry を破棄 (m_holeScene preserved character を除く)。</summary>
    internal static void ClearScene()
    {
        var deadKeys = new List<int>();
        foreach (var kv in s_entries)
        {
            // Unity-null 化した character は dead。それ以外 (m_holeScene preserved 含む) は温存
            // ([[feedback_scene_unload_snapshot_clear]] 同方針)
            if (kv.Value.Character == null)
            {
                foreach (var sk in kv.Value.PerSmr.Values)
                {
                    if (sk.PrevClone != null) Object.Destroy(sk.PrevClone);
                }
                deadKeys.Add(kv.Key);
            }
        }
        foreach (var k in deadKeys) s_entries.Remove(k);
    }

    private static void EnsureSceneUnloadSubscribed()
    {
        if (s_sceneUnloadSubscribed) return;
        s_sceneUnloadSubscribed = true;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        ClearScene();
    }

    private static (int rIdx, int lIdx) ResolveBreastBoneIndices(Transform[] bones)
    {
        int rIdx = -1, lIdx = -1;
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            if (b == null) continue;
            if (b.name == "R_breast1_skinJT") rIdx = i;
            else if (b.name == "L_breast1_skinJT") lIdx = i;
        }
        return (rIdx, lIdx);
    }

    private static float ResolveAmount(CharID id) => id switch
    {
        CharID.ERISA => Mathf.Clamp(Configs.ErisaBreastFlatten.Value, 0f, 1.0f),
        CharID.KANA  => Mathf.Clamp(Configs.KanaBreastFlatten.Value,  0f, 1.0f),
        CharID.LUNA  => Mathf.Clamp(Configs.LunaBreastFlatten.Value,  0f, 1.0f),
        CharID.RIN   => Mathf.Clamp(Configs.RinBreastFlatten.Value,   0f, 1.0f),
        CharID.MIUKA => Mathf.Clamp(Configs.MiukaBreastFlatten.Value, 0f, 1.0f),
        CharID.KUON  => Mathf.Clamp(Configs.KuonBreastFlatten.Value,  0f, 1.0f),
        _ => 0f,
    };

    /// <summary>
    /// 指定 CharID の live character を env + HoleScene の両方から解決し、各 character を <see cref="Refresh"/> する。
    /// env == HoleScene (Bar 滞在中) は二重 Apply を seen set で防ぐ。
    /// skin donor preload 完了経路 (<see cref="PreloadSkinDonorAndReapply"/>) から呼ぶ。
    /// </summary>
    private static void RefreshCharAllScenes(CharID charId)
    {
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
        Refresh(character, charId);
    }

    /// <summary>
    /// amount 変更時の live tune エントリポイント。RestoreFor → ApplyFor の順で再生成し、
    /// stale cache 残留を防ぐ。amount=0 の判定は <see cref="ApplyFor"/> 側で行うため、ここでは
    /// 二重 ResolveAmount を避けるため直接呼び込む。
    /// </summary>
    private static void Refresh(GameObject character, CharID charId)
    {
        // RestoreFor で snapshot が消える前に per-SMR flatten モードを退避し、再 Apply で引き継ぐ。
        // (native↔移植 の文脈は呼出元から渡されないため snapshot に記憶した値を使う)
        // additive Tops の「温存 costume=flat / injected donor=no-flat」混在を単一 bool に潰すと injected が
        // 二重収縮するため、flat=false だった SMR を distancePreservedSmrIds として再適用し per-SMR で維持する。
        var (anyFlatten, noFlatIds) = SnapshotFlattenModes(character);
        RestoreFor(character);
        ApplyFor(character, charId, flattenVerts: anyFlatten, distancePreservedSmrIds: noFlatIds);
    }

    /// <summary>
    /// 当該 character の既存 snapshot から per-SMR flatten モードを 1 パスで集約する (live-tune Refresh 用)。
    /// anyFlatten: PerSmr のいずれかが LastFlattenVerts=true (= 頂点 flatten 経路が要る)。snapshot 不在は native 想定で true。
    /// noFlatIds: LastFlattenVerts==false の SMR (= 距離保存済 = 二重収縮回避で flatten しない) の instanceID 集合
    ///            (PerSmr のキー smrKey = smr.GetInstanceID())。additive の kept=flat / injected=no-flat 混在を維持する。
    /// </summary>
    private static (bool anyFlatten, HashSet<int> noFlatIds) SnapshotFlattenModes(GameObject character)
    {
        var noFlatIds = new HashSet<int>();
        if (character == null) return (true, noFlatIds);
        if (!s_entries.TryGetValue(character.GetInstanceID(), out var entry)) return (true, noFlatIds);
        bool anySnap = false;
        bool anyFlatten = false;
        foreach (var kv in entry.PerSmr)
        {
            anySnap = true;
            if (kv.Value.LastFlattenVerts) anyFlatten = true;
            else noFlatIds.Add(kv.Key);
        }
        if (!anySnap) return (true, noFlatIds);   // snapshot 不在 (初回 live tune): native 想定 true / set 空
        return (anyFlatten, noFlatIds);
    }
}
