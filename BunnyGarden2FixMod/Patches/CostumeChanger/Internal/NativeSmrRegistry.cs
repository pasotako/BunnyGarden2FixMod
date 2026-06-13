using System.Collections.Generic;
using BunnyGarden2FixMod.Utils;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger.Internal;

/// <summary>
/// SMR の native sharedMesh を集中管理する単一権威。
///
/// **規約 (Invariant)**: 全 MOD cloner / Capture サイトは、SMR.sharedMesh を MOD 由来 Mesh
/// (clone / resolved / push / swap) に差し替える前に必ず <see cref="GetOrCapture"/> を呼ぶ。
/// 最初に呼ぶ MOD 経路が native を確定する (= true native = addressables stable な setup() ロード時 sharedMesh)。
/// 後続の呼出は同一 Mesh 参照を返す。
///
/// Restore は <see cref="TryGet"/> で native へ戻す。
///
/// memory feedback_native_smr_registry_invariant.md 参照。
///
/// 規約違反 (= MOD 生成 Mesh suffix を持つ Mesh の native 登録試行) は <see cref="GetOrCapture"/> 内で
/// <see cref="PatchLogger.LogError"/> + stack trace で検出する。
/// </summary>
internal static class NativeSmrRegistry
{
    /// <summary>
    /// MOD 生成 Mesh の name suffix (規約違反検出用)。
    /// suffix リテラルは <see cref="ModMeshSuffixes"/> に一元管理し、生成クラスと本配列の双方が
    /// 同一定数を参照する (二重管理を排除し、新規 Mesh 追加時の片側忘れによる検出漏れを防ぐ)。
    /// 新規 MOD 生成 Mesh が追加されたら <see cref="ModMeshSuffixes"/> に定数を足し、ここにも 1 行追加する。
    /// </summary>
    private static readonly string[] s_knownModSuffixes =
    {
        ModMeshSuffixes.BreastShift,   // BreastClothWeightShifter
        ModMeshSuffixes.BreastFlat,    // BreastFlattenApplier
        ModMeshSuffixes.DistPreserve,  // MeshDistancePreserver
        ModMeshSuffixes.Transplanted,  // MeshBlendShapeTransplanter
        ModMeshSuffixes.Offset,        // MeshSurfaceOffsetAdjuster, MeshPenetrationResolver (donor 側)
        ModMeshSuffixes.Resolved,      // MeshPenetrationResolver (skin 側)
    };

    private readonly struct Key
    {
        public readonly int CharInstanceId;
        public readonly int SmrInstanceId;
        public Key(int charId, int smrId) { CharInstanceId = charId; SmrInstanceId = smrId; }
    }

    private sealed class Entry
    {
        public GameObject Character;
        public Mesh NativeMesh;
        // GetOrCapture が NativeMesh を確定した瞬間の smr.bones[].name（= native costume の bone 順）。
        // swap 後は smr.bones が Babydoll 順に置換されるため、weight conform の bone名 remap 用に保存する。
        public string[] NativeBoneNames;
    }

    private static readonly Dictionary<Key, Entry> s_entries = new();

    // smrInstanceId → Entry の逆引き index。Restore 経路 (TryGet / TryGetBoneNames) を O(1) 化する。
    // 不変条件: s_entries に entry が存在 ⟺ s_bySmrId にも同一 Entry 参照が存在。
    // これは「新規挿入時に必ず両 dict へ set」「ClearScene 削除時に ref 一致なら両方除去」で維持する。
    // smr から charInstanceId を引けないため s_entries (Key=(char,smr)) は smr 単独 query に使えない。
    private static readonly Dictionary<int, Entry> s_bySmrId = new();

    /// <summary>
    /// 指定 SMR の native sharedMesh を返す。未登録なら現 sharedMesh を native として登録して返す。
    ///
    /// **規約**: SMR の sharedMesh を MOD 由来 Mesh に差し替える前に必ず呼ぶ。
    /// 最初に呼ぶ MOD 経路が native を確定する。後続は同一参照を返す。
    /// </summary>
    /// <param name="character">SMR を所有する character (charInstanceId 用)</param>
    /// <param name="smr">対象 SMR (smrInstanceId / sharedMesh 用)</param>
    /// <returns>native Mesh ref。Unity-null は許容 (元から sharedMesh = null の SMR の場合)。</returns>
    internal static Mesh GetOrCapture(GameObject character, SkinnedMeshRenderer smr)
    {
        if (character == null || smr == null) return null;
        var key = new Key(character.GetInstanceID(), smr.GetInstanceID());
        if (s_entries.TryGetValue(key, out var existing))
        {
            // fake-null check: 元 native Mesh が外部経路 (MagicaCloth originalMesh 上書き等) で
            // destroyed された場合は null を返す (memory reference_magicacloth_originalmesh_override 参照)。
            return existing.NativeMesh == null ? null : existing.NativeMesh;
        }

        var current = smr.sharedMesh;
        // 規約違反検出: MOD 生成 Mesh suffix を持つ Mesh を native 登録しようとした
        if (current != null && IsModGeneratedMesh(current))
        {
            PatchLogger.LogError(
                $"[NativeSmrRegistry] 規約違反: MOD 生成 Mesh を native 登録しようとした " +
                $"(char={character.name}, smr={smr.name}, mesh={current.name})\n" +
                $"{System.Environment.StackTrace}");
            // 違反検出時も登録は行う (= 既存挙動を壊さない fail-open)。LogError で開発時に発見・修正する。
        }

        var boneNames = CaptureBoneNames(smr);
        var entry = new Entry
        {
            Character = character,
            NativeMesh = current,
            NativeBoneNames = boneNames,
        };
        s_entries[key] = entry;
        // 逆引き index も同時に set (last-writer-wins)。recycled smrId が別 char で再登録された場合は
        // 最新 entry で上書きされ、古い entry の除去は ClearScene の ref 一致 guard が担う。
        s_bySmrId[key.SmrInstanceId] = entry;
        return current;
    }

    /// <summary>
    /// 登録済 native のみ取得 (未登録なら null)。Restore 経路で消費。
    /// Unity-null 化した Mesh (= destroyed) を保持していた場合も null を返す。
    /// </summary>
    internal static Mesh TryGet(SkinnedMeshRenderer smr)
    {
        if (smr == null) return null;
        // 逆引き index で O(1) 取得。smrInstanceId は SMR ごとに一意なため char を跨いだ衝突は
        // (recycled smrId の過渡期を除き) 起きず、s_entries の (char,smr) Key 走査と同結果になる。
        if (s_bySmrId.TryGetValue(smr.GetInstanceID(), out var entry))
            return entry.NativeMesh == null ? null : entry.NativeMesh;
        return null;
    }

    /// <summary>
    /// 登録済 native bone 名配列を取得（未登録なら null）。weight conform の bone名 remap 用。
    /// 返る配列の index は登録時 native mesh の boneWeights が指す index 空間に対応する。
    /// </summary>
    internal static string[] TryGetBoneNames(SkinnedMeshRenderer smr)
    {
        if (smr == null) return null;
        if (s_bySmrId.TryGetValue(smr.GetInstanceID(), out var entry))
            return entry.NativeBoneNames;
        return null;
    }

    /// <summary>
    /// scene unload で Unity-null 化した character の entry を回収。
    /// m_holeScene preserved character は entry.Character ≠ null で温存される
    /// (memory feedback_scene_unload_snapshot_clear と同方針)。
    /// </summary>
    internal static void ClearScene()
    {
        if (s_entries.Count == 0) return;
        var deadKeys = new List<Key>();
        foreach (var kv in s_entries)
        {
            // Character が Unity-null = この scene unload で destroy された entry
            if (kv.Value.Character == null)
            {
                deadKeys.Add(kv.Key);
            }
        }
        foreach (var k in deadKeys)
        {
            // 逆引き index も除去するが、recycled smrId が別 char で再登録され s_bySmrId[smrId] が
            // 別 Entry を指している場合は消さない (= ref 一致 guard。最新 entry を温存)。
            if (s_entries.TryGetValue(k, out var deadEntry)
                && s_bySmrId.TryGetValue(k.SmrInstanceId, out var indexed)
                && ReferenceEquals(indexed, deadEntry))
            {
                s_bySmrId.Remove(k.SmrInstanceId);
            }
            s_entries.Remove(k);
        }
        PatchLogger.LogDebug($"[NativeSmrRegistry] ClearScene: {deadKeys.Count} entry 回収 (残 {s_entries.Count})");
    }

    private static string[] CaptureBoneNames(SkinnedMeshRenderer smr)
    {
        var bones = smr.bones;
        if (bones == null) return System.Array.Empty<string>();
        var names = new string[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            names[i] = bones[i] != null ? bones[i].name : null;
        return names;
    }

    /// <summary>
    /// Mesh 名が MOD 生成 clone の suffix (<see cref="s_knownModSuffixes"/>) を持つか。
    /// 用途は 2 つ: (1) GetOrCapture の規約違反早期検出、(2) <see cref="SkinShrinkCoordinator"/> の
    /// skin_upper 巻き戻し基準選択で transient clone を排除し真 native へ fallback する判定。
    /// suffix を編集する際は両用途への影響に注意。
    /// </summary>
    internal static bool IsModGeneratedMesh(Mesh mesh)
    {
        var name = mesh.name;
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var suffix in s_knownModSuffixes)
        {
            if (name.EndsWith(suffix, System.StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
