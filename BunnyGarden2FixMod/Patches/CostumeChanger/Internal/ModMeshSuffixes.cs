namespace BunnyGarden2FixMod.Patches.CostumeChanger.Internal;

/// <summary>
/// MOD が生成する clone Mesh の name suffix を一元管理する単一権威。
///
/// 各 suffix は 2 系統で参照される:
/// (1) 各生成クラスが clone Mesh を命名する際の接尾辞 (例: <c>baseMesh.name + BreastFlat</c>)。
/// (2) <see cref="NativeSmrRegistry.IsModGeneratedMesh"/> / <see cref="NativeSmrRegistry"/> の規約違反検出、
///     および <see cref="SkinShrinkCoordinator"/> の真 native fallback 判定。
///
/// 以前は (1) を各クラスの private const / インラインリテラル、(2) を NativeSmrRegistry のリテラル配列で
/// 二重管理していたため、新規 MOD Mesh 追加時に片方を忘れると規約違反検出が機能しなくなる懸念があった。
/// ここに集約することで単一の文字列リテラルを唯一の出所とする。
///
/// 新規 MOD 生成 Mesh を追加したら、ここに 1 定数追加し、生成クラスと
/// <see cref="NativeSmrRegistry"/> の suffix 配列の両方からこの定数を参照すること。
/// </summary>
internal static class ModMeshSuffixes
{
    /// <summary>BreastClothWeightShifter の cloth weight shift clone。</summary>
    internal const string BreastShift = "_breastshift";

    /// <summary>BreastFlattenApplier の胸 flatten clone。</summary>
    internal const string BreastFlat = "_breastflat";

    /// <summary>MeshDistancePreserver の距離保存補正 clone。</summary>
    internal const string DistPreserve = "_distpres";

    /// <summary>MeshBlendShapeTransplanter の blendShape 移植 clone。</summary>
    internal const string Transplanted = "_transplanted";

    /// <summary>MeshSurfaceOffsetAdjuster / MeshPenetrationResolver (donor 側) の表面オフセット clone。</summary>
    internal const string Offset = "_offset";

    /// <summary>MeshPenetrationResolver (skin 側) の貫通解決 clone。</summary>
    internal const string Resolved = "_resolved";
}
