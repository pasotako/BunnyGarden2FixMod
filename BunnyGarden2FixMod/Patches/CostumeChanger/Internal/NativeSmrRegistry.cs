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
    /// MOD 生成 Mesh の name suffix (規約違反検出用)。2026-05-28 plan 着手時に実コード grep で確定。
    /// 新規 MOD 生成 Mesh が追加されたらここにも suffix 追加すること
    /// (spec §規約違反の早期検出 参照)。
    /// </summary>
    private static readonly string[] s_knownModSuffixes =
    {
        "_breastshift",   // BreastClothWeightShifter
        "_breastflat",    // BreastFlattenApplier
        "_distpres",      // MeshDistancePreserver
        "_transplanted",  // MeshBlendShapeTransplanter
        "_offset",        // MeshSurfaceOffsetAdjuster, MeshPenetrationResolver (donor 側)
        "_resolved",      // MeshPenetrationResolver (skin 側)
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
        s_entries[key] = new Entry
        {
            Character = character,
            NativeMesh = current,
            NativeBoneNames = boneNames,
        };
        return current;
    }

    /// <summary>
    /// 登録済 native のみ取得 (未登録なら null)。Restore 経路で消費。
    /// Unity-null 化した Mesh (= destroyed) を保持していた場合も null を返す。
    /// </summary>
    internal static Mesh TryGet(SkinnedMeshRenderer smr)
    {
        if (smr == null) return null;
        // SMR から char InstanceID を引けないので全 entry 走査になるが、規模は数十 SMR / char × 12 char 以下で許容範囲。
        // TryGet は discrete RestoreFor / RestoreSmr 経路のみで呼出され毎フレーム hot path 無し。
        // パフォーマンス問題が出たら smr InstanceID → char InstanceID の逆引き index を追加する。
        foreach (var kv in s_entries)
        {
            if (kv.Key.SmrInstanceId == smr.GetInstanceID())
            {
                return kv.Value.NativeMesh == null ? null : kv.Value.NativeMesh;
            }
        }
        return null;
    }

    /// <summary>
    /// 登録済 native bone 名配列を取得（未登録なら null）。weight conform の bone名 remap 用。
    /// 返る配列の index は登録時 native mesh の boneWeights が指す index 空間に対応する。
    /// </summary>
    internal static string[] TryGetBoneNames(SkinnedMeshRenderer smr)
    {
        if (smr == null) return null;
        foreach (var kv in s_entries)
        {
            if (kv.Key.SmrInstanceId == smr.GetInstanceID())
                return kv.Value.NativeBoneNames;
        }
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
        foreach (var k in deadKeys) s_entries.Remove(k);
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
