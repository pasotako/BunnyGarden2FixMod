using System.Collections.Generic;
using BunnyGarden2FixMod.Patches.CostumeChanger.Internal;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Game;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// <c>mesh_skin_upper</c> の胸領域頂点を <c>R/L_breast1_skinJT</c> bone 原点へ収縮させる overlay 機構。
///
/// <see cref="SkinShrinkCoordinator"/> の "素 mesh 焼き込み" の後段に被せる形で sharedMesh を flatten clone
/// に差し替え、<c>OriginalSkinUpper</c> には addressables stable asset (= 素 mesh_skin_upper / 素 Babydoll)
/// が残る設計。
///
/// Hook 経路:
///   1. <see cref="BreastFlattenSetupPatch"/> (CharacterHandle.setup Postfix) — 素キャラ向け
///   2. <see cref="TopsLoader"/>.Apply の ApplySkinShrinkPhase 完了直後 — Tops 移植 cycle
///   3. <see cref="BottomsLoader"/>.Apply の ApplySkinShrinkPhase 完了直後 — Bottoms 単独移植 cycle
///   4. Tops/BottomsLoader.RestoreFor の Unregister* 直前 — clone を破棄して
///      OriginalSkinUpper 再捕獲が flatten clone を「素」と誤捕獲しないようにする
///
/// 寿命管理: per-character の <see cref="Entry"/> に直近の flatten clone と base mesh を保持し、
/// 新規 clone 生成時に <see cref="Object.Destroy"/> で前回 clone を破棄。
/// scene unload (<c>m_holeScene</c> preserved character は除く) と character destroy で entry を除去。
/// </summary>
internal static class BreastFlattenApplier
{
    private const float BreastWeightThreshold = 0.05f;
    private const float AmountUpperClamp = 1.0f;
    private const string FlattenCloneSuffix = "_breastflat";

    // native_upper への NN マッチ上界（m）。継ぎ目ゲート(SkinUpperSeamConformDist)とは独立軸で、
    // ゲート通過頂点の補正先 native weight の誤マッチのみ除外する。構造的値なので Config 化しない。
    private const float SeamConformNativeMatchDist = 0.03f;
    // 腕除外 X 半幅（torso |x|<0.20、腕 |x|>0.3。0.25 で torso のみ conform）。構造的値なので Config 化しない。
    private const float SeamConformXHalf = 0.25f;

    private sealed class Entry
    {
        public GameObject Character;
        public Mesh PrevFlattenClone;
        /// <summary>
        /// flatten 適用直前の sharedMesh (= swap 後の Babydoll / SkinShrinkCoordinator が焼いた push 済 mesh)。
        /// RestoreFor で clone を Destroy する前に smr.sharedMesh をこれに書き戻すために保持する。
        /// </summary>
        public Mesh BaseMesh;
    }

    private static readonly Dictionary<int, Entry> s_entries = new();
    private static bool s_sceneUnloadedSubscribed;

    private static GameObject s_proxyHostRoot;

    private sealed class ProxyEntry
    {
        public SkinnedMeshRenderer ProxySmr;
        public Mesh CurrentFlattenedMesh;
        public float LastAmount = float.NaN;
        public Mesh SourceSharedMesh;  // 直前生成時の preload sharedMesh (差替検出)
    }

    private static readonly Dictionary<CharID, ProxyEntry> s_proxyByChar = new();

    private static readonly CharID[] s_targetCharIds =
    {
        CharID.ERISA,
        CharID.KANA,
        CharID.LUNA,
        CharID.RIN,
        CharID.MIUKA,
        CharID.KUON,
    };

    /// <summary>
    /// 衣装系 Config 反映の対象 CharID（全6）。<see cref="CostumeReflectionCoordinator"/> が
    /// global param 変更時に全 char を dirty にするため参照する。定義の二重化を避ける。
    /// </summary>
    internal static IReadOnlyList<CharID> TargetCharIds => s_targetCharIds;

    /// <summary>
    /// Plugin.Awake から呼ぶ。proxy SMR ホスト用の hidden GameObject を <paramref name="parent"/> 配下に作り、
    /// scene unload 購読を張る。config 変更の live tune は <see cref="CostumeReflectionCoordinator"/> が一元処理する。
    /// 冪等: 二重呼出は no-op。
    /// </summary>
    public static void Initialize(GameObject parent)
    {
        EnsureSceneUnloadSubscribed();
        EnsureProxyHost(parent);
        PatchLogger.LogInfo("[BreastFlatten] Initialized");
    }

    /// <summary>
    /// flatten を live <c>mesh_skin_upper.sharedMesh</c> に overlay する。clone を生成して差し替える。
    /// amount = 0 / breast bone 不在 / SMR 不在のいずれかで早期 return (allocation 0)。
    /// </summary>
    /// <param name="character">対象 character GameObject (null 不可)</param>
    /// <param name="charId">amount 解決のための CharID</param>
    public static void ApplyOverlay(GameObject character, CharID charId)
    {
        if (character == null) return;

        float amount = ResolveAmount(charId);

        var smr = FindSkinUpper(character);
        if (smr == null)
        {
            if (amount > 0f)
                PatchLogger.LogDebug($"[BreastFlatten] mesh_skin_upper SMR 不在 ({character.name}, char={charId})");
            return;
        }

        // ApplyOverlay は HarmonyX patch 順 alphabetical で BreastFlattenSetupPatch < TopsSetupPatch /
        // BottomsSetupPatch のため、setup() Postfix chain で最初に skin_upper.sharedMesh を mutate する MOD
        // 経路。ここで真 native を確定して後段呼出を idempotent にする。
        Internal.NativeSmrRegistry.GetOrCapture(character, smr);

        // pure-native: flatten 駆動時のみ視覚 skin_upper を Babydoll に swap（cloth と同じ flat surface に揃える）。
        // amount=0 (Tops override 等) では swap は ApplySkinUpperPhase 側が既に済ませている。
        if (amount > 0f)
            EnsureNativeSkinSwap(character, charId, smr);

        var baseMesh = smr.sharedMesh;
        if (baseMesh == null)
        {
            if (amount > 0f)
                PatchLogger.LogDebug($"[BreastFlatten] sharedMesh null ({character.name})");
            return;
        }

        // 二重 ApplyOverlay 防止: 既に mod clone (suffix 付き) ならスキップ。
        if (baseMesh.name.EndsWith(FlattenCloneSuffix))
            return;

        // swap 検出: 現 sharedMesh が Registry の真 native と異なれば Babydoll に swap 済み。
        // 腰境界 conform は swap 済みのときのみ適用する（native upper を参照に下端リムを寄せる）。
        var nativeRef = Internal.NativeSmrRegistry.TryGet(smr);
        bool swapped = nativeRef != null && !ReferenceEquals(baseMesh, nativeRef);
        bool conformEnabled = Configs.SkinUpperSeamConform.Value;
        bool needConform = swapped && conformEnabled;
        bool needFlatten = amount > 0f;
        if (!needFlatten && !needConform) return;

        // breast bone は flatten のみ要する。conform-only では bone 解決失敗でも続行。
        int rIdx = -1, lIdx = -1;
        if (needFlatten)
        {
            (rIdx, lIdx) = ResolveBreastBoneIndices(smr);
            if (rIdx < 0)
            {
                PatchLogger.LogWarning($"[BreastFlatten] breast bone (R/L_breast1_skinJT) 不在 ({character.name}, char={charId})");
                needFlatten = false;
                if (!needConform) return;
            }
        }

        // pure-native flatten のみ、distance-preserve 済み cloth の内側へ肌を push してから flatten。
        // Tops/Bottoms は SkinShrinkCoordinator が push 済みのため skip（swap snapshot 不在で gate OFF）。
        int instanceId = character.GetInstanceID();
        bool nativeSwapped = SmrSnapshotStore.TryGet(
            SnapshotKind.BreastFlatten, instanceId, SwapSnapshotKindName, false, out _);
        Mesh flattenSource = baseMesh;
        if (needFlatten && nativeSwapped)
        {
            var pushRenderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            flattenSource = PushSkinUnderCloth(character, pushRenderers, baseMesh);
        }

        // clone を作って頂点 deform。Entry.BaseMesh は baseMesh(=Babydoll) を保持し、restore は Babydoll→native。
        var clone = Object.Instantiate(flattenSource);
        clone.name = baseMesh.name + FlattenCloneSuffix;
        if (!ReferenceEquals(flattenSource, baseMesh)) Object.Destroy(flattenSource);

        var verts = clone.vertices;
        var weights = clone.boneWeights;

        // ① 胸 flatten deform（高 Y 域）。
        int touched = 0;
        if (needFlatten)
        {
            var (smoothRadius, smoothIterations, smoothStrength) = ResolveSmoothParams();
            touched = BuildDeformedVertsInPlace(
                verts, weights, rIdx, lIdx, amount, smoothRadius, smoothIterations, smoothStrength);
        }

        // ② 腰継ぎ目 weight conform（胴 Y 帯）。swap で Babydoll 順になった seam の boneWeight を native upper へ戻す。
        // 非 readable native / native bone 名未取得は skip + ログ。breast 域は falloff で不変。
        int conformed = 0;
        if (needConform)
        {
            var nativeBoneNames = Internal.NativeSmrRegistry.TryGetBoneNames(smr);
            // 継ぎ目 localization 基準 = target 自身の Babydoll lower（swap 元と同一 asset、masking 不変）。
            Vector3[] lowerVerts = null;
            if (TopsLoader.TryGetSkinDonorSmrs(charId, out _, out var babyLowerSmr)
                && babyLowerSmr != null && babyLowerSmr.sharedMesh != null && babyLowerSmr.sharedMesh.isReadable)
            {
                lowerVerts = babyLowerSmr.sharedMesh.vertices;
            }
            if (nativeRef.isReadable && nativeBoneNames != null && lowerVerts != null)
            {
                float seamDist = Configs.SkinUpperSeamConformDist.Value;
                var babyBoneNames = new string[smr.bones.Length];
                for (int bi = 0; bi < smr.bones.Length; bi++)
                    babyBoneNames[bi] = smr.bones[bi] != null ? smr.bones[bi].name : null;
                conformed = SkinUpperWeightConformer.ConformInPlace(
                    verts, weights, babyBoneNames,
                    nativeRef.vertices, nativeRef.boneWeights, nativeBoneNames,
                    lowerVerts, seamDist, SeamConformNativeMatchDist, SeamConformXHalf);
            }
            else
            {
                PatchLogger.LogDebug($"[BreastFlatten] seam weight conform skip: readable={nativeRef.isReadable} boneNames={(nativeBoneNames != null)} babyLower={(lowerVerts != null)} ({character.name}, char={charId})");
            }
        }

        // conform-only（flatten 無し）で 1 頂点も動かなかった場合（非 readable native / 全 NN 外れ値）は
        // clone が baseMesh と完全同一になる。entry/clone を増やさず sharedMesh は baseMesh のまま据え置く
        // （baseMesh は suffix 無しなので次回 ApplyOverlay で再 conform を試行できる）。
        // needFlatten 経路は touched==0 でも weight-shift を要するため対象外。
        if (!needFlatten && conformed == 0)
        {
            Object.Destroy(clone);
            return;
        }

        clone.vertices = verts;
        if (conformed > 0) clone.boneWeights = weights;
        clone.RecalculateNormals();
        clone.RecalculateBounds();

        // 縮退 triangle 由来の zero / NaN 法線をフォールバック (元 baseMesh の法線で上書き)
        var newNormals = clone.normals;
        var origNormals = baseMesh.normals;
        if (newNormals != null && origNormals != null && newNormals.Length == origNormals.Length)
        {
            for (int i = 0; i < newNormals.Length; i++)
            {
                Vector3 n = newNormals[i];
                if (float.IsNaN(n.x) || float.IsNaN(n.y) || float.IsNaN(n.z) || n.sqrMagnitude < 1e-8f)
                {
                    newNormals[i] = origNormals[i];
                }
            }
            clone.normals = newNormals;
        }

        // ── breast1 weight を Spine3 へ amount 比例で移送（flatten 専用。conform-only では skip）。
        // 順序厳守: BuildDeformedVertsInPlace が weights[] の breast weight を読んだ "後" に書き換えること。
        int shifted = 0;
        if (needFlatten)
        {
            float shiftAmount = ResolveWeightShiftAmount(charId);
            int parentIdx = BreastWeightShiftMath.ResolveParentBoneIndex(smr.bones);
            if (parentIdx < 0)
            {
                // parentIdx<0 時は boneWeights を一切改変せず clone をそのまま登録 (= flatten のみ適用の既存挙動)。
                PatchLogger.LogWarning($"[BreastFlatten] parent bone (Spine3/2/1_skinJT) 不在 — weight shift skip ({character.name}, char={charId})");
            }
            else
            {
                for (int i = 0; i < weights.Length; i++)
                {
                    float wR = BreastWeightShiftMath.GetWeightForBone(weights[i], rIdx);
                    float wL = BreastWeightShiftMath.GetWeightForBone(weights[i], lIdx);
                    if (wR + wL < BreastWeightShiftMath.BreastWeightThreshold) continue;
                    weights[i] = BreastWeightShiftMath.RedistributeWeight(weights[i], rIdx, lIdx, parentIdx, shiftAmount);
                    shifted++;
                }
                clone.boneWeights = weights;
            }
        }

        // entry に登録 + 前回 clone を破棄 + BaseMesh を保持
        EnsureSceneUnloadSubscribed();
        int charKey = character.GetInstanceID();
        if (s_entries.TryGetValue(charKey, out var entry))
        {
            if (entry.PrevFlattenClone != null) Object.Destroy(entry.PrevFlattenClone);
            entry.Character = character;
            entry.PrevFlattenClone = clone;
            entry.BaseMesh = baseMesh;
        }
        else
        {
            s_entries[charKey] = new Entry
            {
                Character = character,
                PrevFlattenClone = clone,
                BaseMesh = baseMesh,
            };
        }

        smr.sharedMesh = clone;

        PatchLogger.LogDebug($"[BreastFlatten] ApplyOverlay {character.name} char={charId} amount={amount:F2} flatten={touched} conform={conformed} shifted={shifted}");
    }

    private static (float radius, int iterations, float strength) ResolveSmoothParams()
    {
        float radius = Configs.BreastFlattenSmoothRadius.Value;
        int iterations = Configs.BreastFlattenSmoothIterations.Value;
        float strength = Configs.BreastFlattenSmoothStrength.Value;
        return (radius, iterations, strength);
    }

    /// <summary>
    /// CharID から該当する Configs.*BreastFlatten の amount を返す。clamp 済 [0, AmountUpperClamp(=1.0)]。
    /// 未対応 CharID は 0.0 を返す (= 完全 skip)。
    /// </summary>
    internal static float ResolveAmount(CharID charId)
    {
        float raw = charId switch
        {
            CharID.ERISA => Configs.ErisaBreastFlatten.Value,
            CharID.KANA  => Configs.KanaBreastFlatten.Value,
            CharID.LUNA  => Configs.LunaBreastFlatten.Value,
            CharID.RIN   => Configs.RinBreastFlatten.Value,
            CharID.MIUKA => Configs.MiukaBreastFlatten.Value,
            CharID.KUON  => Configs.KuonBreastFlatten.Value,
            _ => 0f,
        };
        return Mathf.Clamp(raw, 0f, AmountUpperClamp);
    }

    /// <summary>
    /// CharID から該当する Configs.*BreastWeightShift の倍率を返す。未対応 CharID は 1.0 (= 抑制を弱めない)。
    /// </summary>
    internal static float ResolveShiftMultiplier(CharID charId)
    {
        return charId switch
        {
            CharID.ERISA => Configs.ErisaBreastWeightShift.Value,
            CharID.KANA  => Configs.KanaBreastWeightShift.Value,
            CharID.LUNA  => Configs.LunaBreastWeightShift.Value,
            CharID.RIN   => Configs.RinBreastWeightShift.Value,
            CharID.MIUKA => Configs.MiukaBreastWeightShift.Value,
            CharID.KUON  => Configs.KuonBreastWeightShift.Value,
            _ => 1f,
        };
    }

    /// <summary>
    /// weight 移送量 = flatten 量 × 倍率（[0,1] clamp）。flatten=0 で 0（= 移送なし）。
    /// skin (<see cref="ApplyOverlay"/>) と cloth (<see cref="BreastClothWeightShifter"/>) の
    /// RedistributeWeight 呼び出しで共有する単一ソース。
    /// </summary>
    internal static float ResolveWeightShiftAmount(CharID charId)
        => BreastWeightShiftMath.ComputeWeightShiftAmount(ResolveAmount(charId), ResolveShiftMultiplier(charId));

    /// <summary>
    /// SMR の bones[] から R_breast1_skinJT / L_breast1_skinJT の index を解決。
    /// 戻り値: (rightIndex, leftIndex)。どちらか欠けていれば両方 -1 を返す。
    /// </summary>
    private static (int rIdx, int lIdx) ResolveBreastBoneIndices(SkinnedMeshRenderer smr)
    {
        if (smr == null || smr.bones == null) return (-1, -1);
        int rIdx = -1, lIdx = -1;
        for (int i = 0; i < smr.bones.Length; i++)
        {
            var b = smr.bones[i];
            if (b == null) continue;
            if (rIdx < 0 && b.name == "R_breast1_skinJT") rIdx = i;
            else if (lIdx < 0 && b.name == "L_breast1_skinJT") lIdx = i;
            if (rIdx >= 0 && lIdx >= 0) break;
        }
        if (rIdx < 0 || lIdx < 0) return (-1, -1);
        return (rIdx, lIdx);
    }

    /// <summary>
    /// character 配下に「flatten したいが <c>sharedMesh.isReadable=false</c> で頂点を読めない」cloth
    /// (<c>mesh_costume*</c>) が 1 つでもあるか（OR 集約）。true のとき pure-native の BreastFlatten は
    /// skin/cloth とも skip する（cloth geometry を潰せないのに skin だけ潰すと bust が不整合になるため）。
    /// 判定に使う <c>isReadable</c> / <c>bones</c> / <see cref="TopsLoader.IsTopsCandidate"/> は非readable
    /// mesh でも読めるので vertices に依存しない。
    /// 例: Bunnygirl の <c>mesh_costume</c> は Read/Write Disabled でビルドされている（実機確認 2026-05-27、
    /// docs/costume-system.md「Bunnygirl mesh_costume は Read/Write Disabled」参照）。
    /// </summary>
    internal static bool HasUnflattenableBreastCloth(GameObject character)
    {
        if (character == null) return false;
        var smrs = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var smr in smrs)
        {
            if (smr == null || !TopsLoader.IsTopsCandidate(smr)) continue;
            var m = smr.sharedMesh;
            if (m == null || m.isReadable) continue;          // readable は通常経路で flatten 可能
            var (rIdx, lIdx) = ResolveBreastBoneIndices(smr);  // bones は非readable mesh でも読める
            if (rIdx >= 0 && lIdx >= 0) return true;           // breast を覆う cloth が潰せない
        }
        return false;
    }

    /// <summary>
    /// character GameObject 配下の "mesh_skin_upper" 名の SMR を 1 つ返す (無ければ null)。
    /// </summary>
    private static SkinnedMeshRenderer FindSkinUpper(GameObject character)
    {
        if (character == null) return null;
        var smrs = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var s in smrs)
        {
            if (s != null && s.name == "mesh_skin_upper") return s;
        }
        return null;
    }

    // 視覚 skin swap 用 snapshot の SMR 名キー（SmrSnapshotStore の smrKind）。
    private const string SwapSnapshotKindName = "mesh_skin_upper";

    /// <summary>
    /// pure-native（Tops / Bottoms override 皆無）+ BreastFlatten amount>0 のとき、視覚 mesh_skin_upper を
    /// <see cref="CostumeMeshSwapper.SwapSmr"/> で Babydoll に swap し、cloth(distance-preserve) と同じ
    /// flat Babydoll surface に視覚 skin を揃える。swap 前 native 状態を SmrSnapshotStore(BreastFlatten) に
    /// capture（<see cref="RestoreNativeSkinSwap"/> で復元）。
    ///
    /// SwapSmr は bones を Babydoll 順にマップするため、後続 <see cref="ApplyOverlay"/> の flatten
    /// （rIdx は smr.bones、bindposes は Babydoll mesh）が同一 index 空間で整合する。
    ///
    /// gate（いずれかで skip）:
    ///   - Tops override: TopsLoader.ApplySkinUpperPhase が skin swap 担当。
    ///   - Bottoms override: BottomsLoader.ApplySkinUpperBabydollPhase が RegisterBottoms より前に
    ///     skin_upper を Babydoll に swap (BreastFlatten>0 時) + OriginalSkinUpper を同期するため、
    ///     本クラスは pure-native のみ swap を担当する。full-body 衣装 target でも readable cloth (SwimWear 等)
    ///     なら同 Phase が swap する (non-readable=Bunnygirl は native 維持)。ここで swap すると ApplyOverlay は
    ///     phase(g) = RegisterBottoms の後で走るため push 済 native clone を snapshot に焼き込む。
    ///   - Babydoll skin 未 preload: native flatten に fallback（preload 完了後 Refresh で再来）。
    ///   - 既に swap 済（snapshot 存在）: idempotent skip。
    ///
    /// 既知の脆さ（実害なし・要 revisit）: pure-native で swap 済の状態から runtime で Tops/Bottoms override を
    /// 適用すると、gate は以後の新規 swap のみ抑止し、既存 swap は巻き戻さない。Tops/BottomsLoader の
    /// ApplySkinUpperPhase は当該時点の skin (= Babydoll-swap + flatten clone) を OriginalMesh として捕捉するため、
    /// snapshot が一時 clone を「原本」として記録する。最終復元は <see cref="RestoreFor"/> → <see cref="RestoreNativeSkinSwap"/>
    /// が native まで戻すため net 回帰は無いが、復元順への暗黙依存に支えられている。
    /// </summary>
    private static void EnsureNativeSkinSwap(GameObject character, CharID charId, SkinnedMeshRenderer smr)
    {
        if (smr == null || charId >= CharID.NUM) return;
        if (TopsOverrideStore.TryGet(charId, out _)) return;
        if (BottomsOverrideStore.TryGet(charId, out _)) return;
        int instanceId = character.GetInstanceID();
        if (SmrSnapshotStore.TryGet(SnapshotKind.BreastFlatten, instanceId, SwapSnapshotKindName, false, out _))
            return;
        if (!TopsLoader.TryGetSkinDonorSmrs(charId, out var babydollSkinUpper, out _)
            || babydollSkinUpper == null || babydollSkinUpper.sharedMesh == null)
            return;

        SmrSnapshotStore.Capture(SnapshotKind.BreastFlatten, instanceId, SwapSnapshotKindName, false, smr, null);
        // SwapSmr で sharedMesh を Babydoll に書き替えても Registry は真 native を session 不変で保持する。
        CostumeMeshSwapper.SwapSmr(smr, babydollSkinUpper, character, "BreastFlatten", skipActivateForTransparentLayer: false);
        PatchLogger.LogDebug($"[BreastFlatten] native skin_upper を Babydoll に swap ({character.name} char={charId})");
    }

    /// <summary>
    /// <see cref="EnsureNativeSkinSwap"/> の逆。Babydoll→native に戻し snapshot を除去する。
    /// <see cref="RestoreFor"/> 末尾（flatten 復元後）で呼ぶ。snapshot 不在（Tops/Bottoms 経路 / 未 swap）は no-op。
    /// </summary>
    private static void RestoreNativeSkinSwap(GameObject character)
    {
        if (character == null) return;
        int instanceId = character.GetInstanceID();
        if (!SmrSnapshotStore.TryGet(SnapshotKind.BreastFlatten, instanceId, SwapSnapshotKindName, false, out var snap))
            return;
        var smr = FindSkinUpper(character);
        if (smr != null)
        {
            smr.gameObject.SetActive(snap.OriginalActive);
            smr.enabled = snap.OriginalEnabled;
            // Registry が fake-null/未登録の場合は null 代入で SMR 非描画になる (元から null と同等の意図動作)。
            smr.sharedMesh = Internal.NativeSmrRegistry.TryGet(smr);
            if (snap.OriginalBones != null) smr.bones = snap.OriginalBones;
            if (snap.OriginalMaterials != null) smr.sharedMaterials = snap.OriginalMaterials;
        }
        SmrSnapshotStore.Remove(SnapshotKind.BreastFlatten, instanceId, SwapSnapshotKindName, false);
        // SwapSmr で graft した bone を回収（skin は通常 graft 無いが per-owner tag で対称に呼ぶ）。
        BoneGrafter.DestroyGrafted(character, "BreastFlatten");
    }

    /// <summary>
    /// 指定 CharID の BreastFlatten amount が有効 (&gt; 0) か。
    /// <see cref="BottomsLoader"/> が skin_upper の Babydoll swap 要否を判定する用途
    /// （Bottoms-only でも BreastFlatten>0 なら上半身を Babydoll 基準に揃える）。
    /// </summary>
    internal static bool IsFlattenActive(CharID charId) => ResolveAmount(charId) > 0f;

    /// <summary>
    /// BreastFlatten 整合のため mesh_skin_upper を Babydoll(<see cref="TopsLoader.SkinDonorCostume"/>) に
    /// 揃えるべきか。= flatten 有効 かつ breast を覆う cloth が flatten 可能(readable)。
    /// pure-native / Bottoms override / additive Tops override で共通の「flatten 駆動 swap」判定で、
    /// <see cref="BottomsLoader"/>.ApplySkinUpperBabydollPhase と <see cref="TopsLoader"/>.ApplySkinUpperPhase
    /// の additive 分岐から呼ぶ。charId 無効 / flatten=0 / non-readable cloth(Bunnygirl 等) のいずれかで false。
    ///
    /// 注: Tops 非 additive override の「donor garment topology のため flatten 非依存で常に swap」は本判定の
    /// 対象外（別理由。TopsLoader 側が非 additive 経路でそのまま swap する）。pure-native の swap 判定は
    /// 上流（SetupPatch / Refresh の <see cref="HasUnflattenableBreastCloth"/> + ApplyOverlay の amount gate）が
    /// 担うため <see cref="EnsureNativeSkinSwap"/> は本 helper を呼ばない。
    /// </summary>
    internal static bool ShouldSwapSkinForFlatten(GameObject character, CharID charId)
        => charId < CharID.NUM
           && IsFlattenActive(charId)
           && !HasUnflattenableBreastCloth(character);

    /// <summary>
    /// pure-native（Tops/Bottoms override 皆無）= BreastFlatten が肌 swap/push/flatten を所有する状態。
    /// この状態でのみ <see cref="ApplyOverlay"/> の swap + 肌 push が走る（Tops/Bottoms 時は Loader / Coordinator 管理）。
    /// </summary>
    internal static bool IsPureNativeFlatten(CharID charId) =>
        charId < CharID.NUM
        && !TopsOverrideStore.TryGet(charId, out _)
        && !BottomsOverrideStore.TryGet(charId, out _);

    /// <summary>
    /// pure-native の Babydoll skin <paramref name="baseSkinMesh"/> を、distance-preserve 済みの
    /// native cloth (IsTopsCandidate) の内側へ push する（Tops SkinShrink と同じ push engine / 設定）。
    /// 各 cloth SMR を donor に <see cref="MeshPenetrationResolver.Resolve"/> を chain 適用し、push 済み
    /// 肌を返す。push 不要 / <c>TopsSkinShrink</c>&lt;=0 のときは <paramref name="baseSkinMesh"/> をそのまま返す。
    /// 返り値が baseSkinMesh と異なる場合、呼出側が flatten 後に中間 mesh を Destroy する責務を負う。
    /// resolver の <c>Instantiate(referenceMesh)</c> で bindposes/boneWeights/topology は保持されるため、
    /// 後続 flatten（breast weight 由来の eff）と weight-shift（boneWeights 読取）が整合する。
    /// </summary>
    private static Mesh PushSkinUnderCloth(GameObject character, SkinnedMeshRenderer[] renderers, Mesh baseSkinMesh)
    {
        float skinPush = Configs.TopsSkinShrink.Value;
        if (skinPush <= 0f || baseSkinMesh == null) return baseSkinMesh;

        var anchor = SkinShrinkCoordinator.CollectSkinPushAnchor(
            renderers, new HashSet<string> { "mesh_skin_upper" });
        float falloffR = Configs.TopsSkinShrinkFalloffRadius.Value;
        float sampleR = Configs.TopsSkinShrinkSampleRadius.Value;

        Mesh current = baseSkinMesh;
        Mesh produced = null;
        int pushedParts = 0;
        foreach (var smr in renderers)
        {
            if (smr == null || !TopsLoader.IsTopsCandidate(smr)) continue;
            var donor = smr.sharedMesh;
            if (donor == null) continue;
            var pair = MeshPenetrationResolver.Resolve(
                donorMesh: donor, referenceMesh: current, referenceShape: null,
                minOffset: 0f, skinPushAmount: skinPush,
                skinAnchorVerts: anchor, skinFalloffRadius: falloffR,
                logTag: "BreastFlattenSkinShrink",
                useSkinNormalForPush: true,
                clothSampleRadius: sampleR,
                useScatterPush: true);
            if (pair.skin == null) continue;          // この donor では push 不要
            if (produced != null) Object.Destroy(produced);   // 前段中間を破棄
            produced = pair.skin;
            current = pair.skin;                      // 次 donor は push 後を ref に（カバレッジ累積）
            pushedParts++;
        }
        if (produced == null) return baseSkinMesh;    // 全 donor で push 不要
        PatchLogger.LogDebug($"[BreastFlatten] SkinShrink push {character.name} skinPush={skinPush:F4} parts={pushedParts}");
        return produced;
    }

    /// <summary>
    /// <paramref name="verts"/> を in-place で deform する共通 helper（<see cref="ApplyOverlay"/> と
    /// <see cref="BuildFlattenedClone"/> が共有）。
    ///
    /// 3 pass:
    ///   Pass 1: 頂点 i ごとに raw amountEff = clamp01(wT) * amount (wT &lt; threshold は 0)。
    ///   grid 構築 (smoothRadius &gt; 0 のみ): SpatialGridIndex を元頂点位置で 1 度構築し Pass 2 / Pass 3 で
    ///     共有する。build cost は O(N) (N = vertex 数, ~2-15k 想定)。ApplyOverlay は costume reload と
    ///     live tune 時のみ呼ばれるため per-frame budget には影響しない。
    ///   Pass 2 (optional, smoothRadius &gt; 0): 半径 smoothRadius 内の頂点を集め、raw を距離重み
    ///     (w = 1/(dsq + 1e-4)) 平均して eff にする。weightEps = 1e-4 はクエリ点自身 (dsq=0) の
    ///     self-weight が支配的にならないよう調整した値。近傍は List を使い回し保持しない
    ///     (全 N 頂点ぶんの近傍 index を retain するとメモリ肥大するため)。
    ///   Pass 3 (relaxation, 主機構): eff 重み Laplacian で胸を近傍平均へ寄せて潰す。明示 target を
    ///     置かず近傍平均へ寄せることで「縮小」と「凸凹除去」を同一操作にし、等方平均ゆえ fold / shear /
    ///     center-split が原理的に出ない。高 eff の胸中心が周囲 (低 eff anchor) へ沈み滑らかに deflate し、
    ///     境界 (wT 小 → eff≈0) と領域外 (wT==0 → eff=0) は不動で膜面の固定境界になる。
    ///     wT &gt; 0 && eff &gt; 0 の頂点 (= smoothable) だけを query 点にし、近傍 index リストを smoothable
    ///     頂点ぶんだけ構築 (全 N retain は radius 0.1 等で肥大するため不可)。近傍平均は自己点 (dsq=0) を
    ///     除外した単純平均で、非 smoothable=固定境界も平均に含む (境界 anchor)。各反復で
    ///     verts[i] = Lerp(verts[i], 近傍平均, eff[i] * smoothStrength) を K 回 Jacobi (double-buffer) 反復し
    ///     走査順非依存。eff∈[0,0.95] × smoothStrength∈[0,1] なので Lerp 係数 ≤ 0.95 で overshoot しない。
    ///     eff=0 (領域外) は query 点に含めず完全不動 ＝ 領域外影響遮断 invariant を維持。
    ///     発火条件: grid!=null (smoothRadius&gt;0) && smoothIterations&gt;0 && smoothStrength&gt;0。
    ///     いずれか 0 で flatten 無効 (amount&gt;0 でも変形しない)。
    /// </summary>
    /// <param name="smoothRadius">relaxation 近傍半径 (mesh-local m)。0 以下で flatten 無効。</param>
    /// <param name="smoothIterations">relaxation 反復回数 K。0 以下で flatten 無効。</param>
    /// <param name="smoothStrength">relaxation の 1 反復あたり寄せ率 [0,1]。0 以下で flatten 無効。</param>
    /// <returns>relaxation 対象頂点数 (デバッグログ用)。</returns>
    private static int BuildDeformedVertsInPlace(
        Vector3[] verts, BoneWeight[] weights,
        int rIdx, int lIdx,
        float amount, float smoothRadius, int smoothIterations, float smoothStrength)
    {
        int N = verts.Length;

        // Pass 1: raw amountEff
        var raw = new float[N];
        for (int i = 0; i < N; i++)
        {
            float wR = GetWeightForBone(weights[i], rIdx);
            float wL = GetWeightForBone(weights[i], lIdx);
            float wT = wR + wL;
            raw[i] = (wT < BreastWeightThreshold) ? 0f : Mathf.Clamp01(wT) * amount;
        }

        // grid を smoothRadius>0 のとき元頂点位置で 1 度だけ構築し Pass 2 / Pass 3 で共有する。
        // 近傍 index リストは全 N 頂点ぶん retain するとメモリ肥大するため、Pass 2 は List 使い回しのまま
        // (retain しない)。Pass 3 (relaxation) は smoothable 頂点ぶんだけ後段で構築する。
        SpatialGridIndex grid = smoothRadius > 0f ? new SpatialGridIndex(verts) : null;

        // Pass 2: neighbor-radius smoothing of eff (optional)
        float[] eff;
        if (grid != null)
        {
            eff = new float[N];
            var neighbors = new List<int>(32);
            const float weightEps = 1e-4f;
            for (int i = 0; i < N; i++)
            {
                neighbors.Clear();
                grid.FindWithinRadius(verts[i], smoothRadius, neighbors);
                if (neighbors.Count == 0) { eff[i] = raw[i]; continue; }
                float sum = 0f, wSum = 0f;
                for (int k = 0; k < neighbors.Count; k++)
                {
                    int j = neighbors[k];
                    float dsq = (verts[j] - verts[i]).sqrMagnitude;
                    float w = 1f / (dsq + weightEps);
                    sum += raw[j] * w;
                    wSum += w;
                }
                eff[i] = wSum > 0f ? sum / wSum : raw[i];
            }
        }
        else
        {
            eff = raw;
        }

        // Pass 3 (relaxation): eff 重み Laplacian で胸を近傍平均へ寄せて潰す (flatten 主機構)。
        // 明示 target を置かず近傍平均へ寄せることで「縮小」と「凸凹除去」を同一操作にし、等方平均ゆえ
        // fold / shear / center-split が原理的に出ない。高 eff の胸中心が周囲 (低 eff anchor) へ沈み滑らかに
        // deflate。境界 (wT 小→eff≈0) と領域外 (wT==0→eff=0) は不動で膜面の固定境界。
        // 近傍リストは smoothable ぶんだけ構築 (全 N retain は radius 0.1 等で肥大)。Jacobi double-buffer。
        // 発火条件: grid!=null && K>0 && strength>0 (いずれか 0 で flatten 無効)。
        int touched = 0;
        if (grid != null && smoothIterations > 0 && smoothStrength > 0f)
        {
            var smoothIdx = new List<int>(256);
            for (int i = 0; i < N; i++)
            {
                if (eff[i] <= 0f) continue;
                float wT = GetWeightForBone(weights[i], rIdx) + GetWeightForBone(weights[i], lIdx);
                if (wT > 0f) smoothIdx.Add(i);
            }
            touched = smoothIdx.Count;

            if (smoothIdx.Count > 0)
            {
                // smoothable 頂点の近傍 index リストを 1 度だけ構築 (自己点 dsq=0 は除外 = 標準 umbrella)。
                var nbLists = new int[smoothIdx.Count][];
                var tmp = new List<int>(64);
                for (int s = 0; s < smoothIdx.Count; s++)
                {
                    int i = smoothIdx[s];
                    tmp.Clear();
                    grid.FindWithinRadius(verts[i], smoothRadius, tmp);
                    tmp.Remove(i);   // 自己点を除外 (FindWithinRadius は dsq=0 の自身を含む)
                    nbLists[s] = tmp.ToArray();
                }

                // double-buffer。非 smoothable 頂点は両 buffer とも初期コピーした原値を保持し続ける
                // (本ループは smoothable index しか書かないため、非 smoothable は不変＝領域外不動が保証される)。
                var src = verts;
                var dst = new Vector3[N];
                System.Array.Copy(src, dst, N);
                for (int iter = 0; iter < smoothIterations; iter++)
                {
                    for (int s = 0; s < smoothIdx.Count; s++)
                    {
                        int i = smoothIdx[s];
                        var nb = nbLists[s];
                        // 近傍ゼロ (粗 mesh で自己除外後 0 件): dst が前反復の stale 値を持たないよう自値を写す。
                        if (nb.Length == 0) { dst[i] = src[i]; continue; }
                        Vector3 acc = Vector3.zero;
                        for (int k = 0; k < nb.Length; k++) acc += src[nb[k]];
                        Vector3 avg = acc / nb.Length;
                        dst[i] = Vector3.Lerp(src[i], avg, eff[i] * smoothStrength);
                    }
                    var swap = src; src = dst; dst = swap;
                }
                // 反復後 src が最新。verts と別配列なら書き戻す (K 奇数回のとき)。
                if (!ReferenceEquals(src, verts))
                {
                    System.Array.Copy(src, verts, N);
                }
            }
        }

        return touched;
    }

    /// <summary>
    /// BoneWeight (Unity の 4 ボーンまでの skin weight) から指定 bone index の weight を抽出。
    /// </summary>
    private static float GetWeightForBone(BoneWeight bw, int boneIndex)
    {
        if (bw.boneIndex0 == boneIndex) return bw.weight0;
        if (bw.boneIndex1 == boneIndex) return bw.weight1;
        if (bw.boneIndex2 == boneIndex) return bw.weight2;
        if (bw.boneIndex3 == boneIndex) return bw.weight3;
        return 0f;
    }

    /// <summary>
    /// 既に <see cref="Object.Instantiate(Object)"/> 済みの <paramref name="clone"/> の頂点を
    /// breast flatten で in-place 変形する。breast bone idx は <paramref name="smr"/> から解決し、その
    /// breast weight を eff として Laplacian relaxation で胸頂点を近傍平均へ寄せる。normal / bounds を
    /// 再計算し、縮退三角形由来の zero/NaN 法線は clone の元法線で fallback する。
    ///
    /// 呼出側が同 clone の boneWeights も書き換える場合 (cloth weight shift) は、本メソッドが
    /// <c>clone.boneWeights</c> を読むため <b>weight 書換より前</b>に呼ぶこと。
    /// </summary>
    /// <returns>relaxation 対象頂点数。breast bone 不在等で変形不能なら -1 (呼出側は続行可)。</returns>
    public static int FlattenClonedMesh(Mesh clone, SkinnedMeshRenderer smr, float amount)
    {
        if (clone == null || smr == null || amount <= 0f) return -1;

        var (rIdx, lIdx) = ResolveBreastBoneIndices(smr);
        if (rIdx < 0) return -1;

        var verts = clone.vertices;
        var weights = clone.boneWeights;

        var (smoothRadius, smoothIterations, smoothStrength) = ResolveSmoothParams();

        var origNormals = clone.normals;   // deform 後の縮退 NaN 法線 fallback 用
        int touched = BuildDeformedVertsInPlace(
            verts, weights, rIdx, lIdx, amount, smoothRadius, smoothIterations, smoothStrength);

        clone.vertices = verts;
        clone.RecalculateNormals();
        clone.RecalculateBounds();

        var newNormals = clone.normals;
        if (newNormals != null && origNormals != null && newNormals.Length == origNormals.Length)
        {
            for (int i = 0; i < newNormals.Length; i++)
            {
                Vector3 n = newNormals[i];
                if (float.IsNaN(n.x) || float.IsNaN(n.y) || float.IsNaN(n.z) || n.sqrMagnitude < 1e-8f)
                {
                    newNormals[i] = origNormals[i];
                }
            }
            clone.normals = newNormals;
        }

        return touched;
    }

    /// <summary>
    /// <paramref name="srcSmr"/> の sharedMesh を Instantiate し、breast bone weight ベースで flatten 適用した
    /// 新 Mesh を返す。生成失敗 (breast bone 不在 等) で <c>null</c>。
    /// 頂点 deform は <see cref="FlattenClonedMesh"/> に委譲しており、アルゴリズム変更時は
    /// そちらを更新すれば <see cref="ApplyOverlay"/> 系と cloth 側 <see cref="BreastClothWeightShifter"/>
    /// 両方に同時反映される。
    /// </summary>
    private static Mesh BuildFlattenedClone(SkinnedMeshRenderer srcSmr, float amount)
    {
        if (srcSmr == null || srcSmr.sharedMesh == null) return null;
        if (amount <= 0f) return null;

        var src = srcSmr.sharedMesh;
        var clone = Object.Instantiate(src);
        clone.name = src.name + FlattenCloneSuffix;

        int touched = FlattenClonedMesh(clone, srcSmr, amount);
        if (touched < 0)
        {
            Object.Destroy(clone);
            return null;
        }
        return clone;
    }

    /// <summary>
    /// 当該 character の flatten clone を破棄し、必要に応じて <c>mesh_skin_upper.sharedMesh</c> を
    /// 直近 base mesh に戻す。用途: <see cref="SkinShrinkCoordinator"/>.UnregisterTops/Bottoms の直前で呼び、
    /// 再捕獲される sharedMesh が addressables stable asset であることを担保する。
    /// </summary>
    public static void RestoreFor(GameObject character)
    {
        if (character == null) return;
        int charKey = character.GetInstanceID();

        // flatten clone の復元 + 破棄（entry がある場合のみ）。
        if (s_entries.TryGetValue(charKey, out var entry))
        {
            // sharedMesh の取り扱いは状況依存:
            //
            //   ケース A) TopsLoader/BottomsLoader.RestoreFor 経由
            //     先行する SMR snapshot 復元ループが既に sharedMesh = addressables stable 原本
            //     に戻している。我々がここで触ると正しい原本を上書きしてしまうので、何もしない。
            //
            //   ケース B) sharedMesh がまだ flatten clone のまま到達したケースのフェイルセーフ
            //     Destroy 先にすると Unity-null 化して UnregisterTops の OriginalSkinUpper 再捕獲が
            //     destroyed mesh を焼いてしまうので、sharedMesh を Entry.BaseMesh に巻き戻してから Destroy する。
            //
            // 判定: smr.sharedMesh が "_breastflat" suffix の clone のままならケース B、それ以外はケース A。
            var smr = FindSkinUpper(character);
            if (smr != null && smr.sharedMesh != null
                && smr.sharedMesh.name.EndsWith(FlattenCloneSuffix)
                && entry.BaseMesh != null)
            {
                smr.sharedMesh = entry.BaseMesh;
            }

            if (entry.PrevFlattenClone != null)
            {
                Object.Destroy(entry.PrevFlattenClone);
                entry.PrevFlattenClone = null;
            }
            entry.BaseMesh = null;
            s_entries.Remove(charKey);
        }

        // 視覚 swap の復元: flatten 復元（sharedMesh → Entry.BaseMesh=Babydoll）の後に Babydoll→native へ戻す。
        // snapshot 不在（Tops/Bottoms 経路 / 未 swap / pure-native でない）は no-op。
        // entry が無い経路（swap 後 flatten 前に early return した等のエッジ）でも un-swap を保証するため
        // entry ブロック外に置く。
        RestoreNativeSkinSwap(character);

        PatchLogger.LogDebug($"[BreastFlatten] RestoreFor {character.name}");
    }

    /// <summary>scene unload で全 entry を破棄 (m_holeScene preserved character を除く)。</summary>
    public static void ClearScene()
    {
        if (s_entries.Count == 0) return;
        var preservedKeys = new List<int>();
        foreach (var kv in s_entries)
        {
            // m_holeScene preserved character は GameObject が生き残る。
            // Unity-null でない entry はそのまま温存する。Unity-null は dead 扱いで除去。
            if (kv.Value.Character != null)
            {
                preservedKeys.Add(kv.Key);
                continue;
            }
            if (kv.Value.PrevFlattenClone != null) Object.Destroy(kv.Value.PrevFlattenClone);
        }

        if (preservedKeys.Count == 0)
        {
            s_entries.Clear();
        }
        else
        {
            // alive な entry だけ残す
            var alive = new Dictionary<int, Entry>(preservedKeys.Count);
            foreach (var k in preservedKeys) alive[k] = s_entries[k];
            s_entries.Clear();
            foreach (var kv in alive) s_entries[kv.Key] = kv.Value;
        }
        PatchLogger.LogDebug($"[BreastFlatten] ClearScene (alive={preservedKeys.Count})");
    }

    private static void EnsureProxyHost(GameObject parent)
    {
        if (s_proxyHostRoot != null) return;
        s_proxyHostRoot = new GameObject("BunnyGarden2FixMod_BreastFlattenProxyHost");
        if (parent != null)
        {
            s_proxyHostRoot.transform.SetParent(parent.transform, worldPositionStays: false);
        }
        s_proxyHostRoot.SetActive(false);  // 描画されない hidden host
    }

    internal static void EnsureSceneUnloadSubscribed()
    {
        if (s_sceneUnloadedSubscribed) return;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        s_sceneUnloadedSubscribed = true;
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        ClearScene();
    }

    /// <summary>
    /// preload Babydoll donor の <paramref name="preloadSkinUpper"/> SMR について、<paramref name="charId"/> の
    /// 現 BreastFlatten amount で flatten した proxy SMR を返す。<see cref="MeshDistancePreserver.Preserve"/>
    /// の <c>targetSkinSmrs</c> に流して、cloth が flatten された skin 表面を基準に距離保存されるようにする。
    ///
    /// amount = 0 / preload SMR が null / breast bone 不在 のいずれかで <c>null</c> を返す
    /// (呼出側は preload SMR をそのまま使う想定)。
    ///
    /// proxy は per-CharID で永続化する hidden SMR。amount 変更時は <see cref="InvalidateProxyCache"/> 経由で
    /// 全 proxy の sharedMesh を作り直す。
    /// </summary>
    public static SkinnedMeshRenderer GetFlattenedReferenceSmr(SkinnedMeshRenderer preloadSkinUpper, CharID charId)
    {
        if (preloadSkinUpper == null || preloadSkinUpper.sharedMesh == null) return null;

        float amount = ResolveAmount(charId);
        if (amount <= 0f) return null;

        if (s_proxyHostRoot == null)
        {
            PatchLogger.LogWarning($"[BreastFlatten] proxy host 未初期化 (Initialize 未呼出) — preload SMR をそのまま使う");
            return null;
        }

        if (!s_proxyByChar.TryGetValue(charId, out var entry))
        {
            entry = new ProxyEntry();
            s_proxyByChar[charId] = entry;
        }

        // 必要なら proxy SMR を生成
        if (entry.ProxySmr == null)
        {
            // preload mesh_skin_upper SMR GameObject 単体をクローンする (bones / bindposes / structure を継承)。
            // CharacterHandle は親階層にあるため複製されない (= preload host guard 相当の問題は起きない)。
            // preload host mutex 違反にならないよう、誤って `transform.root.gameObject` 等で SMR の親
            // (CharacterHandle 持ち) を複製しないこと。
            var go = Object.Instantiate(preloadSkinUpper.gameObject, s_proxyHostRoot.transform, worldPositionStays: false);
            go.name = $"BreastFlattenProxy_{charId}_skinUpper";
            go.SetActive(false);
            entry.ProxySmr = go.GetComponent<SkinnedMeshRenderer>();
            if (entry.ProxySmr == null)
            {
                PatchLogger.LogWarning($"[BreastFlatten] proxy SMR 生成失敗: {charId}");
                Object.Destroy(go);
                s_proxyByChar.Remove(charId);
                return null;
            }
            entry.ProxySmr.name = "mesh_skin_upper"; // CombineSkinSmrs の summary 表示用

            // 万一 mesh_skin_upper GameObject に MagicaCloth 系 component が付いていた場合、BuildAndRun が
            // proxy SMR の sharedMesh を referenceInitSetupData.originalMesh で巻き戻す可能性があるため、
            // proxy 上ではすべて Destroy する。通常 mesh_skin_upper に MagicaCloth は attach されないが、
            // preload donor の prefab 構成変更で将来 attach される可能性を考慮した保険。
            DestroyMagicaClothComponentsRecursive(go);
        }

        // amount 変更 / source 差替 → flatten clone を再生成
        bool needsRefresh = entry.CurrentFlattenedMesh == null
                            || !Mathf.Approximately(entry.LastAmount, amount)
                            || !ReferenceEquals(entry.SourceSharedMesh, preloadSkinUpper.sharedMesh);
        if (needsRefresh)
        {
            if (entry.CurrentFlattenedMesh != null) Object.Destroy(entry.CurrentFlattenedMesh);
            entry.CurrentFlattenedMesh = BuildFlattenedClone(preloadSkinUpper, amount);
            entry.LastAmount = amount;
            entry.SourceSharedMesh = preloadSkinUpper.sharedMesh;
            if (entry.CurrentFlattenedMesh == null)
            {
                // 生成失敗 (breast bone 不在 等) — fallback null
                return null;
            }
            entry.ProxySmr.sharedMesh = entry.CurrentFlattenedMesh;
        }

        return entry.ProxySmr;
    }

    /// <summary>
    /// 全 proxy entry の flatten clone を破棄する (proxy SMR GameObject 自体は保持)。
    /// 次回 <see cref="GetFlattenedReferenceSmr"/> で各 entry の sharedMesh が再生成される。
    /// 用途: BreastFlatten amount / smooth 変更時に CostumeReflectionCoordinator.Flush から呼ぶ。
    /// </summary>
    public static void InvalidateProxyCache()
    {
        foreach (var kv in s_proxyByChar)
        {
            var e = kv.Value;
            if (e.CurrentFlattenedMesh != null)
            {
                Object.Destroy(e.CurrentFlattenedMesh);
                e.CurrentFlattenedMesh = null;
            }
            e.LastAmount = float.NaN;
            e.SourceSharedMesh = null;
            if (e.ProxySmr != null) e.ProxySmr.sharedMesh = null;
        }
        PatchLogger.LogDebug($"[BreastFlatten] InvalidateProxyCache (entries={s_proxyByChar.Count})");
    }

    /// <summary>
    /// <paramref name="root"/> 配下の MagicaCloth 系 component を Destroy する。proxy SMR の
    /// sharedMesh 巻き戻し防衛用。MagicaCloth2 の DLL に依存しないよう、型名文字列で動的判定する。
    /// </summary>
    private static void DestroyMagicaClothComponentsRecursive(GameObject root)
    {
        if (root == null) return;
        int destroyed = 0;
        foreach (var c in root.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
        {
            if (c == null) continue;
            var typeName = c.GetType().FullName ?? string.Empty;
            if (typeName.StartsWith("MagicaCloth2") || typeName.Contains(".MagicaCloth"))
            {
                Object.Destroy(c);
                destroyed++;
            }
        }
        if (destroyed > 0)
            PatchLogger.LogDebug($"[BreastFlatten] proxy 配下の MagicaCloth component {destroyed} 個 Destroy ({root.name})");
    }

    private static void TryRefresh(GameObject character, CharID charId, HashSet<int> seen)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        if (!seen.Add(id)) return;
        Refresh(character, charId);
    }

    /// <summary>
    /// amount / SmoothRadius 変更時の live tune エントリポイント。pure-native では cloth→push→flatten の
    /// 順序を保証するため本メソッドに集約する（肌 push が distance-preserve 済み cloth を読むため）。
    /// BreastClothWeightShifter 側の config ハンドラは pure-native を skip し、ここから cloth を直接呼ぶ。
    ///   - amount = 0: flatten clone 破棄 (+ pure-native は cloth も RestoreFor)。
    ///   - amount > 0 / pure-native: RestoreFor → cloth RestoreFor → cloth ApplyFor → ApplyOverlay。
    ///   - amount > 0 / Tops・Bottoms: 肌は Loader 管理なので再 flatten のみ (cloth は別ハンドラが処理)。
    /// </summary>
    internal static void Refresh(GameObject character, CharID charId)
    {
        if (character == null) return;
        float amount = ResolveAmount(charId);
        bool pureNative = IsPureNativeFlatten(charId);

        if (amount <= 0f)
        {
            RestoreFor(character);
            if (pureNative) BreastClothWeightShifter.RestoreFor(character);  // cloth clone も戻す
            return;
        }

        if (pureNative)
        {
            // cloth (mesh_costume) が Read/Write Disabled で頂点を読めず geometry flatten 不可なら、
            // skin だけ潰すと bust が不整合になるため flatten 全体を skip し native へ戻す（Bunnygirl 等）。
            if (HasUnflattenableBreastCloth(character))
            {
                RestoreFor(character);
                BreastClothWeightShifter.RestoreFor(character);
                PatchLogger.LogWarning($"[BreastFlatten] {character.name} char={charId}: cloth mesh が Read/Write Disabled のため breast flatten skip (skin/cloth native 維持)");
                return;
            }

            // cloth 再 fit → 肌 push → flatten の順を保証（push が cloth を読むため）。
            RestoreFor(character);
            BreastClothWeightShifter.RestoreFor(character);
            BreastClothWeightShifter.ApplyFor(character, charId, flattenVerts: true);
            ApplyOverlay(character, charId);
        }
        else
        {
            // Tops/Bottoms: 肌は Loader 管理。ここは再 flatten のみ（cloth は BreastClothWeightShifter の
            // ハンドラが独立処理。push しないので順序非依存）。
            RestoreFor(character);
            ApplyOverlay(character, charId);
        }
    }

    /// <summary>
    /// 指定 CharID の live character (env + HoleScene) に flatten overlay を再適用する。
    /// Babydoll skin donor の遅延 preload 完了時に <see cref="BreastClothWeightShifter"/> から呼び、
    /// 初回 cold load で未 preload により skip された視覚 swap (<see cref="EnsureNativeSkinSwap"/>) を retry する。
    /// cloth 側 (BreastClothWeightShifter) だけが完了後に Babydoll-flat へ追従し視覚 skin が native-flat に
    /// 取り残される非対称を解消する。env == HoleScene (Bar 滞在中) は二重 Apply を seen set で防ぐ。
    /// proxy cache は不変 (amount/source 不変) なので InvalidateProxyCache は呼ばない。
    /// </summary>
    internal static void ReapplyForCharId(CharID charId)
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
}
