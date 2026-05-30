using BunnyGarden2FixMod.Utils;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// Tops / Bottoms 双方の SkinShrink (skin SMR を cloth より内側へ push する補正) を統合し、
/// character 単位で素 skin Mesh と cloth contribution を保持して順次 push する。
///
/// 動機: 旧設計は <see cref="TopsLoader"/> と <see cref="BottomsLoader"/> がそれぞれ
/// `s_skinShrinkCache` / `s_skinShrinkOriginalMeshes` を持ち独立に skin SMR.sharedMesh を
/// 上書きしていた。両 override 同時適用すると以下の干渉が起きる:
///   - Bottoms apply が skin_upper を上書きして Tops 側の z-fighting 解消が消える
///   - 片方を Restore すると他方の登録済 originalMesh が stale となり Unity null 化で skin 描画消失
/// 本 Coordinator は素 mesh を 1 箇所で管理し、Refresh 時に両 skin SMR を素状態に rewind して
/// 全 contribution を順次 push し直すため上記干渉が起きない。
///
/// 呼び出し経路:
///   - <see cref="TopsLoader.Apply"/> 終端で <see cref="RegisterTops"/>
///   - <see cref="BottomsLoader.Apply"/> 終端で <see cref="RegisterBottoms"/>
///   - <see cref="TopsLoader.RestoreFor"/> 終端で <see cref="UnregisterTops"/>
///   - <see cref="BottomsLoader.RestoreFor"/> 終端で <see cref="UnregisterBottoms"/>
///   - 両 Loader の live tune handler から <see cref="InvalidateCache"/> + per-target
///     ApplyDirectly (これが内部で Register* を呼ぶ) で再同期
///   - <see cref="OnSceneUnloaded"/> 経路で <see cref="ClearScene"/>
///
/// 設計上の挙動 (素 mesh の意味):
///   - mesh_skin_upper の素は「上半身を Babydoll 基準で扱うか」で異なる:
///       Tops override 有: target Babydoll asset (Tops Apply (d) で swap 済み)
///       Tops 無 + BreastFlatten>0 (Bottoms-only 含む): target Babydoll asset
///         (BottomsLoader.ApplySkinUpperBabydollPhase で swap 済み。BreastFlatten の cloth が
///          flatten 済 Babydoll proxy 基準のため skin も Babydoll に揃える)
///       上記いずれでもない: target 元 costume asset
///   - mesh_skin_lower の素は常に target 元 costume asset (誰も swap しない)
///   - Bottoms 単独 → Tops 後追い override で skin_upper の素が target 元 → Babydoll に切替わる。
///     これは Tops 設計に内在する挙動で、Coordinator 化で初めて起きるわけではない。
///   - swap 主体 (Tops / BreastFlatten-Bottoms) は swap / un-swap と連動して
///     <see cref="RebaseOriginalSkinUpper"/> で OriginalSkinUpper を stable asset に同期すること。
///
/// API 契約:
///   - <see cref="UnregisterTops"/> 呼出前に呼出元は mesh_skin_upper.sharedMesh を
///     target 元 costume asset (= NativeSmrRegistry.TryGet(smr)) に戻し終えていること。
///     <see cref="TopsLoader.RestoreFor"/> 末尾で呼ぶのはこの前提を満たす。
///   - <see cref="RegisterTops"/> 呼出時に mesh_skin_upper.sharedMesh が Babydoll asset
///     になっていること (Tops Apply (d) 完了済)。Apply 末尾で呼ぶのはこの前提を満たす。
///   - すべての Register/Unregister は character GameObject の component が live な状態で呼ぶ
///     (<see cref="ClearScene"/> 後は呼ばない)。
///
/// skin_lower の素 (巻き戻し先) は Entry に保持せず <see cref="Internal.NativeSmrRegistry"/> の
/// native を単一権威とする (<see cref="RefreshOne"/> の rewind 参照)。skin_lower は誰も swap しない
/// ため「素 == native」で固定でき、registry 一本化で巻き戻し先が常に stable asset になる。
/// 本 Coordinator は skin_lower.sharedMesh の唯一の writer であり、push (<see cref="ApplyContribution"/>)
/// より前に必ず <see cref="RefreshOne"/> 冒頭で GetOrCapture するため、registry には初回ロード時の
/// 真 native (= _resolved 等の MOD clone でない) のみが焼かれる (汚染は構造的に到達不能)。
/// 旧実装は OriginalSkinLower を現 sharedMesh から生捕捉していたため、scene 遷移後に transient
/// pushed Mesh (_resolved clone) を誤捕捉 → InvalidateCache で破棄 → 巻き戻しスキップで
/// skin_lower が永久消失するバグがあった (registry 一本化で構造的に解消)。
/// 将来 skin_lower を swap する経路を追加する場合は、この native==素 前提が崩れるため
/// skin_upper 同様の Entry 保持 + Rebase 機構が必要になる。
/// </summary>
internal static class SkinShrinkCoordinator
{
    private struct Contribution
    {
        public List<SkinnedMeshRenderer> ClothSmrs;
        public float SkinPush;
        public float FalloffR;
        public float SampleR;
    }

    private struct Entry
    {
        // Live tune の handler が cache invalidate 後に全 entry を再 push し直すために必要。
        // s_entries の key (instanceId) からは GameObject を逆引きできないため Entry 側で保持する。
        // Unity-null 比較で破棄済 GameObject を検出して RefreshAllByConfig 内で skip。
        public GameObject Character;
        // skin_upper の素は swap 状態で native / Babydoll の 2 値を取るため Entry で保持する
        // (RebaseOriginalSkinUpper で stable asset に同期)。
        // skin_lower は誰も swap しないため「素 == native」で固定。巻き戻し先は NativeSmrRegistry
        // (RefreshOne 冒頭で GetOrCapture 済) を単一権威とし Entry には持たない (class doc 参照)。
        public Mesh OriginalSkinUpper;
        public Contribution Tops;
        public Contribution Bottoms;
        public bool HasTops;
        public bool HasBottoms;
    }

    private static readonly Dictionary<int, Entry> s_entries = new();

    // key: (prevSkinId=push 直前 skin mesh, donorClothId=cloth mesh, yQ/fQ/srQ=量子化 params,
    //       srcTag=0/1 Tops/Bottoms, kindTag=0/1 upper/lower push)
    // srcTag/kindTag は必須: Bottoms 経路は 1 skirt mesh を upper/lower 両方に push するため。
    // InstanceID 安全性: Unity は monotonic 採番、Original* は addressables 共有 asset で永続。
    private static readonly Dictionary<(int prevSkinId, int donorClothId, int yQ, int fQ, int srQ, int srcTag, int kindTag), Mesh> s_cache = new();

    // Anchor は非 skin 系 (face/eye) を優先する。upper/lower 互いを anchor から外すことで
    // waist 縫い目 (上下境界) で push が anchor 距離 0 → skinScale=0 にフェードする現象を解消。
    // upper/lower それぞれが同じ face/eye anchor を見るため、両 SMR で対応する境界頂点が
    // 同等の anchor 距離を得て自然に連続する。
    // face/eye SMR が不在のキャラ (Bunnygirl 等の可能性) では skin系 anchor (互いを除外) に
    // fallback して全頂点 full push 暴走を防ぐ。
    private static readonly string[] s_faceBoundarySmrNames = new[]
    {
        "mesh_face",
        "mesh_eye",
        "mesh_face_blush_high",
    };
    private static readonly HashSet<string> s_fallbackExcludeUpper = new() { "mesh_skin_upper" };
    private static readonly HashSet<string> s_fallbackExcludeLower = new() { "mesh_skin_lower" };

    public static void RegisterTops(GameObject character, IEnumerable<SkinnedMeshRenderer> topClothSmrs,
        float skinPush, float falloffR, float sampleR)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        var smrs = topClothSmrs?.Where(s => s != null).ToList() ?? new List<SkinnedMeshRenderer>();
        if (!s_entries.TryGetValue(id, out var e)) e = default;

        e.Character = character;
        e.HasTops = true;
        e.Tops = new Contribution
        {
            ClothSmrs = smrs,
            SkinPush = skinPush,
            FalloffR = falloffR,
            SampleR = sampleR,
        };

        // Originals: Tops Apply (d) で skin_upper を Babydoll に swap した直後に呼ばれる前提。
        // 既存 entry が Bottoms 単独経路で target 元 mesh を OriginalSkinUpper に格納していた可能性が
        // あるため、Tops Register は **強制的に** 現 sharedMesh (= Babydoll) で上書きする。
        // skin_lower は誰も swap しないため Entry に素を保持しない (RefreshOne が registry native へ rewind)。
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var su = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_upper");
        // MOD clone (_resolved/_breastflat) を素として捕捉しない (push 巻き戻し基準のドリフト防止)。
        if (su != null && su.sharedMesh != null) e.OriginalSkinUpper = StableSkinUpperBase(su);

        s_entries[id] = e;
        RefreshOne(id, character, renderers);
    }

    public static void RegisterBottoms(GameObject character, IEnumerable<SkinnedMeshRenderer> bottomsClothSmrs,
        float skinPush, float falloffR, float sampleR)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        var smrs = bottomsClothSmrs?.Where(s => s != null).ToList() ?? new List<SkinnedMeshRenderer>();
        if (!s_entries.TryGetValue(id, out var e)) e = default;

        e.Character = character;
        e.HasBottoms = true;
        e.Bottoms = new Contribution
        {
            ClothSmrs = smrs,
            SkinPush = skinPush,
            FalloffR = falloffR,
            SampleR = sampleR,
        };

        // Bottoms は skin SMR を swap しない。skin_upper の素が未捕捉なら現 sharedMesh を捕捉
        // (Tops 由来の Babydoll を尊重し既存値は据置)。skin_lower は Entry に保持せず registry native を使う。
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (e.OriginalSkinUpper == null)
        {
            var su = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_upper");
            // MOD clone を素として捕捉しない (push 巻き戻し基準のドリフト防止)。
            if (su != null && su.sharedMesh != null) e.OriginalSkinUpper = StableSkinUpperBase(su);
        }

        s_entries[id] = e;
        RefreshOne(id, character, renderers);
    }

    public static void UnregisterTops(GameObject character)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        if (!s_entries.TryGetValue(id, out var e)) return;
        e.HasTops = false;
        e.Tops = default;

        // Bottoms 残存: TopsLoader.RestoreFor が直前に skin_upper.sharedMesh を target 元 costume asset に
        // 戻した前提 (NativeSmrRegistry.TryGet 経由)。OriginalSkinUpper を null 化してから現 sharedMesh で
        // 再捕捉することで、「Tops 有時の素 = Babydoll」「Tops 無時の素 = target 元 asset」の遷移を吸収する。
        // 規約遵守前提: ApplySkinUpperPhase で GetOrCapture 済のため TryGet は真 native を返す。
        // Fail-safe: 規約破綻 (skin_upper の GetOrCapture 抜け) で TryGet が null を返した場合、RestoreFor で
        // smr.sharedMesh が Unity-null になる。`su.sharedMesh != null` チェックは Unity-null も False で弾くため、
        // destroyed Mesh を OriginalSkinUpper に再捕獲する事故は起きない (e.OriginalSkinUpper は null のまま
        // 温存)。RefreshOne 内の null check で rewind も skip。ただし mesh_skin_upper 描画は消失するため
        // 規約破綻は LogError (GetOrCapture suffix check) で fail-fast 検出する設計に依存する。
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        e.OriginalSkinUpper = null;
        var su = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_upper");
        // MOD clone (_resolved/_breastflat) を素として再捕捉しない (additive で skin_upper snapshot 不在のとき
        // 現 sharedMesh が push 済 _resolved のまま残り、ここで再捕捉すると _resolved が累積する主ドリフト点)。
        if (su != null && su.sharedMesh != null) e.OriginalSkinUpper = StableSkinUpperBase(su);
        s_entries[id] = e;
        // entry は s_entries に残す (Has* 両方 false でも OriginalSkin* は保持して、live tune
        // cycle 中の InvalidateCache → 再 Register で transient pushed Mesh を「素」と誤捕獲する事故を
        // 防ぐ)。両 Has=false の empty entry は RefreshOne で rewind だけ走り無害、scene unload の
        // ClearScene または character destroy 時の RefreshAllByConfig cleanup で除去される。
        RefreshOne(id, character, renderers);
    }

    public static void UnregisterBottoms(GameObject character)
    {
        if (character == null) return;
        int id = character.GetInstanceID();
        if (!s_entries.TryGetValue(id, out var e)) return;
        e.HasBottoms = false;
        e.Bottoms = default;
        s_entries[id] = e;
        // UnregisterTops と同方針で entry 保持。RefreshOne が rewind + 残存 contribution の push を行う
        // (HasTops も false なら no-op rewind のみ)。
        var renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        RefreshOne(id, character, renderers);
    }

    /// <summary>
    /// 指定 character の skin SMR を素 mesh に rewind し、登録された contribution を順次 push し直す。
    /// 順序: Tops contribution (skin_upper のみ) → Bottoms contribution (skin_lower → skin_upper)。
    /// </summary>
    private static void RefreshOne(int charId, GameObject character, SkinnedMeshRenderer[] renderers)
    {
        if (character == null) return;
        if (!s_entries.TryGetValue(charId, out var e)) return;
        var skinUpper = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_upper");
        var skinLower = renderers.FirstOrDefault(r => r != null && r.name == "mesh_skin_lower");

        // memory feedback_native_smr_registry_invariant: rewind / ApplyContribution で skin SMR の sharedMesh を
        // MOD 生成 push clone に書き換える前に Registry に真 native を確定。Tops/BottomsLoader 側の
        // ApplySkinUpperPhase / ApplySkinUpperBabydollPhase が gate (ShouldSwapSkinForFlatten 等) で skip された
        // 経路でも、本 Coordinator が独立に skin の native 確定責務を負う (Registry 未登録なら現 sharedMesh =
        // target 元 asset = 真 native を登録、既登録なら idempotent return)。
        if (skinUpper != null) Internal.NativeSmrRegistry.GetOrCapture(character, skinUpper);
        if (skinLower != null) Internal.NativeSmrRegistry.GetOrCapture(character, skinLower);

        // 1. Rewind to originals (素 asset 参照のみ。Destroy 不要、addressables 共有で永続)。
        //    skin_upper: Entry 保持の素 (native or Babydoll。RebaseOriginalSkinUpper で stable 同期)。
        //    skin_lower: NativeSmrRegistry の真 native を単一権威とする。直上 :236 の GetOrCapture で
        //      registry は必ず初回ロード時の native を保持済み (本 Coordinator が skin_lower の唯一の
        //      writer で push より前に GetOrCapture するため汚染不能。class doc 参照)。これにより
        //      scene 遷移後も TryGet が真 native を返し、旧 OriginalSkinLower 生捕捉で起きた
        //      「_resolved クローン誤捕捉 → InvalidateCache 破棄 → 巻き戻しスキップ → 永久消失」を防ぐ。
        if (skinUpper != null && e.OriginalSkinUpper != null) skinUpper.sharedMesh = e.OriginalSkinUpper;
        if (skinLower != null)
        {
            var nativeLower = Internal.NativeSmrRegistry.TryGet(skinLower);
            if (nativeLower != null) skinLower.sharedMesh = nativeLower;
        }

        // 2. Anchor 一括収集 (rewind 直後に固定化、push 中に anchor が変わらないように)。
        // face/eye 系を優先、不在なら skin系互いを除外する旧仕様に fallback (Bunnygirl 暴走防止)。
        // upper/lower 共通の anchor を使う: 互いに anchor から外すと waist 縫い目で push が連続化。
        var anchor = CollectFaceBoundaryAnchorVerts(renderers);
        Vector3[] anchorForUpper = anchor;
        Vector3[] anchorForLower = anchor;
        if (anchor.Length == 0)
        {
            // face/eye 不在 fallback: 旧 skin系 anchor (互いを除外)
            anchorForUpper = (skinUpper != null && skinUpper.sharedMesh != null)
                ? CollectSkinShrinkAnchorVerts(renderers, s_fallbackExcludeUpper) : null;
            anchorForLower = (skinLower != null && skinLower.sharedMesh != null)
                ? CollectSkinShrinkAnchorVerts(renderers, s_fallbackExcludeLower) : null;
            PatchLogger.LogDebug($"[SkinShrinkCoord] face/eye anchor 不在、skin系 fallback 経路 ({character.name})");
        }

        int totalSteps = 0;

        // 3. Tops contribution → skin_upper 先、続いて skin_lower push (waist / 長め Tops が
        //    腰下に達するワンピース型 donor 対策、Bottoms と対称化)。
        if (e.HasTops && e.Tops.SkinPush > 0f)
        {
            if (skinUpper != null && skinUpper.sharedMesh != null)
                totalSteps += ApplyContribution(skinUpper, "mesh_skin_upper", anchorForUpper, e.Tops, srcTag: 0, kindTag: 0);
            if (skinLower != null && skinLower.sharedMesh != null)
                totalSteps += ApplyContribution(skinLower, "mesh_skin_lower", anchorForLower, e.Tops, srcTag: 0, kindTag: 1);
        }

        // 4. Bottoms contribution → skin_lower 先、続いて skin_upper push (waist 部分)
        if (e.HasBottoms && e.Bottoms.SkinPush > 0f)
        {
            if (skinLower != null && skinLower.sharedMesh != null)
                totalSteps += ApplyContribution(skinLower, "mesh_skin_lower", anchorForLower, e.Bottoms, srcTag: 1, kindTag: 1);
            if (skinUpper != null && skinUpper.sharedMesh != null)
                totalSteps += ApplyContribution(skinUpper, "mesh_skin_upper", anchorForUpper, e.Bottoms, srcTag: 1, kindTag: 0);
        }

        if (totalSteps > 0)
            PatchLogger.LogDebug($"[SkinShrinkCoord] refresh: {totalSteps} step (hasTops={e.HasTops}, hasBottoms={e.HasBottoms}, anchorVerts={anchor.Length}, {character.name})");
    }

    /// <summary>
    /// shrink 対象 SMR (引数 <paramref name="excludeNames"/>) を除く skin 系 SMR の頂点を集める。
    /// MeshPenetrationResolver の skin push の falloff anchor として使う (隣接 skin との境界で
    /// push 量を 0 にフェードさせるため)。
    /// face/eye anchor 不在時の fallback 経路で使用。
    /// </summary>
    /// <param name="renderers">character 配下の全 SMR スナップショット。</param>
    /// <param name="excludeNames">anchor から除外する SMR 名集合 (shrink 対象自身)。例: "mesh_skin_upper" / "mesh_skin_lower"。</param>
    private static Vector3[] CollectSkinShrinkAnchorVerts(
        SkinnedMeshRenderer[] renderers, HashSet<string> excludeNames)
    {
        var list = new List<Vector3>();
        foreach (var r in renderers)
        {
            if (r == null || r.sharedMesh == null) continue;
            if (!r.name.StartsWith("mesh_skin_", System.StringComparison.Ordinal)) continue;
            if (excludeNames != null && excludeNames.Contains(r.name)) continue;
            list.AddRange(r.sharedMesh.vertices);
        }
        return list.ToArray();
    }

    /// <summary>
    /// face/eye 系 SMR の頂点を anchor として収集する。skin系 (mesh_skin_upper / lower) は意図的に
    /// 除外し、waist 縫い目で push が anchor 距離 0 → skinScale=0 にフェードする現象を解消する。
    /// 配列が空なら呼出側で skin系 anchor 経路に fallback する責務 (Bunnygirl 等 face SMR 不在向け)。
    /// </summary>
    private static Vector3[] CollectFaceBoundaryAnchorVerts(SkinnedMeshRenderer[] renderers)
    {
        var list = new List<Vector3>();
        foreach (var r in renderers)
        {
            if (r == null || r.sharedMesh == null) continue;
            bool match = false;
            for (int i = 0; i < s_faceBoundarySmrNames.Length; i++)
            {
                if (r.name == s_faceBoundarySmrNames[i]) { match = true; break; }
            }
            if (match) list.AddRange(r.sharedMesh.vertices);
        }
        return list.ToArray();
    }

    /// <summary>
    /// skin push の falloff anchor 頂点を収集する共有ヘルパ。face/eye 系を優先し、不在なら
    /// <paramref name="fallbackExcludeSelf"/> を除く skin 系で fallback する（waist 境界連続化 / 暴走防止）。
    /// pure-native BreastFlatten の肌 push（<see cref="BreastFlattenApplier"/>）と本 Coordinator で共有。
    /// </summary>
    internal static Vector3[] CollectSkinPushAnchor(
        SkinnedMeshRenderer[] renderers, HashSet<string> fallbackExcludeSelf)
    {
        var anchor = CollectFaceBoundaryAnchorVerts(renderers);
        if (anchor.Length > 0) return anchor;
        return CollectSkinShrinkAnchorVerts(renderers, fallbackExcludeSelf);
    }

    /// <summary>
    /// 1 つの contribution を 1 つの skin SMR に対して累積適用する。
    /// 各 cloth SMR ごとに <see cref="MeshPenetrationResolver.Resolve"/> を呼び、結果を skin SMR.sharedMesh に挿す。
    /// 同 cloth mesh の重複は呼出内 HashSet で 1 回に絞る。
    /// </summary>
    private static int ApplyContribution(SkinnedMeshRenderer skinSmr, string skinLabel, Vector3[] anchorVerts,
        Contribution c, int srcTag, int kindTag)
    {
        if (c.ClothSmrs == null || c.ClothSmrs.Count == 0) return 0;
        int yQ = Mathf.RoundToInt(c.SkinPush * 10_000f);
        int fQ = Mathf.RoundToInt(c.FalloffR * 10_000f);
        int srQ = Mathf.RoundToInt(c.SampleR * 1_000f);
        var seenInThisCall = new HashSet<int>();
        int stepsApplied = 0;

        // Invariant: c.ClothSmrs は Register 時に swappedTopsPairs / swappedBottomsPairs から構築されるため
        // 意図的 hide (case (c) "target のみ持つ → hide") の SMR は含まれない。よって activeInHierarchy
        // ガードは不要 (むしろ setup() Postfix 時点で character 親が SetActive(false) で hierarchy 上
        // inactive な場合に false 評価され、初回ロードで全 cloth が skip → push 0 step になる症状を踏んだ)。
        // push は Mesh data の static 計算なので cloth が今 visible でなくても pre-push しておけば
        // 後で active 化した時に正しく描画される。
        foreach (var clothSmr in c.ClothSmrs)
        {
            if (clothSmr == null || clothSmr.sharedMesh == null) continue;
            var donorMesh = clothSmr.sharedMesh;
            if (!seenInThisCall.Add(donorMesh.GetInstanceID())) continue;

            var currentSkin = skinSmr.sharedMesh;
            var key = (currentSkin.GetInstanceID(), donorMesh.GetInstanceID(), yQ, fQ, srQ, srcTag, kindTag);
            if (!s_cache.TryGetValue(key, out var pushedSkin) || pushedSkin == null)
            {
                var pair = MeshPenetrationResolver.Resolve(
                    donorMesh: donorMesh, referenceMesh: currentSkin, referenceShape: null,
                    minOffset: 0f, skinPushAmount: c.SkinPush,
                    skinAnchorVerts: anchorVerts, skinFalloffRadius: c.FalloffR,
                    logTag: $"SkinShrinkCoord({(srcTag == 0 ? "Tops" : "Bottoms")}/{skinLabel})",
                    useSkinNormalForPush: true,
                    clothSampleRadius: c.SampleR,
                    useScatterPush: true);
                pushedSkin = pair.skin;
                s_cache[key] = pushedSkin;
            }
            if (pushedSkin != null)
            {
                skinSmr.sharedMesh = pushedSkin;
                stepsApplied++;
            }
        }
        return stepsApplied;
    }

    /// <summary>
    /// 保持中の <c>OriginalSkinUpper</c> を新しい stable asset で同期する。
    /// skin_upper を Babydoll に swap する主体 (Tops / BreastFlatten-Bottoms) が swap 後に Babydoll、
    /// 解除後に native を渡して、<see cref="RefreshOne"/> の rewind 先を実状態と一致させる用途。
    ///
    /// <b>必ず addressables stable asset を渡すこと</b>。push 済 / flatten clone 等の transient を渡すと
    /// 後続 <see cref="InvalidateCache"/>→Destroy で RefreshOne が destroyed Mesh に rewind し描画破損する
    /// (本クラスが据置/再捕捉ロジックで一貫して回避している不変条件)。
    /// entry 不在 / mesh null は no-op。
    /// </summary>
    internal static void RebaseOriginalSkinUpper(GameObject character, Mesh stableMesh)
    {
        if (character == null || stableMesh == null) return;
        int id = character.GetInstanceID();
        if (!s_entries.TryGetValue(id, out var e)) return;
        e.OriginalSkinUpper = stableMesh;
        s_entries[id] = e;
    }

    /// <summary>
    /// skin_upper の push 巻き戻し基準 (素) として安全に捕捉できる Mesh を返す。
    /// 現 sharedMesh が MOD 生成 clone (_resolved/_breastflat 等) の場合、それを <c>OriginalSkinUpper</c> に
    /// 捕捉すると巻き戻し基準がドリフトし、サイクルごとに _resolved が累積する (additive base-SwimWear +
    /// Tops/Bottoms override で skin_upper がトゲ破綻)。clone なら registry の真 native を使う
    /// (skin_lower の registry 単一権威化と同型。<see cref="RebaseOriginalSkinUpper"/> doc の
    /// 「OriginalSkinUpper は stable asset のみ」不変条件を内部捕捉点で強制する)。
    /// stable asset (真 native / Babydoll, suffix 無) はそのまま返す。TryGet が null (未登録/fake-null) なら
    /// null を返し捕捉を見送る (RefreshOne の null-check で rewind skip = 既存 fail-safe と整合)。
    /// </summary>
    private static Mesh StableSkinUpperBase(SkinnedMeshRenderer su)
    {
        var shared = su != null ? su.sharedMesh : null;
        if (shared == null) return null;
        if (Internal.NativeSmrRegistry.IsModGeneratedMesh(shared))
            return Internal.NativeSmrRegistry.TryGet(su);
        return shared;
    }

    public static void ClearScene()
    {
        s_entries.Clear();
        // s_cache は scene 跨ぎで保持 (TopsLoader.OnSceneUnloaded と同方針 — 加算読込時の Unity null 化回避)。
        // GPU memory cleanup は InvalidateCache (param 変更経由) に集約。
    }

    /// <summary>
    /// 登録済み全 entry を Refresh する (live tune handler 用)。
    /// Tops/Bottoms 一方の handler が <see cref="InvalidateCache"/> 後に自分の override store だけ
    /// 再 register するだけだと、もう一方しか override されていない target が destroyed Mesh を
    /// 参照したまま孤児化してしまう。本メソッドが s_entries 全件を refresh して整合を取る。
    /// 破棄済み GameObject (Unity-null) は entry を削除する。
    ///
    /// <paramref name="skipIds"/> に含まれる instanceId の entry は skip する。
    /// 呼出側 (live-tune) が既に canonical な再 Apply (skin push + BreastFlatten overlay) を
    /// 終えた char を指定する用途。skip しないと <see cref="RefreshOne"/> が skin を素から push し直し、
    /// 直前に被せた BreastFlatten clone を上書きして flatten が消える (skin push のみ残る)。
    /// 到達済み char は呼出側で fresh な sharedMesh に再構築済みのため skip しても孤児化しない。
    /// </summary>
    public static void RefreshAllByConfig(HashSet<int> skipIds = null)
    {
        if (s_entries.Count == 0) return;
        var deadIds = new List<int>();
        // 列挙中の操作で例外を避けるため key を ToList でスナップショット。
        foreach (var id in s_entries.Keys.ToList())
        {
            if (skipIds != null && skipIds.Contains(id)) continue;
            if (!s_entries.TryGetValue(id, out var e)) continue;
            if (e.Character == null)
            {
                deadIds.Add(id);
                continue;
            }
            var renderers = e.Character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            RefreshOne(id, e.Character, renderers);
        }
        foreach (var id in deadIds) s_entries.Remove(id);
    }

    public static void InvalidateCache()
    {
        foreach (var m in s_cache.Values)
            if (m != null) Object.Destroy(m);
        s_cache.Clear();
    }
}
