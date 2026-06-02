using BunnyGarden2FixMod.Patches.CostumeChanger.Internal;
using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// <see cref="CharacterHandle.setup"/> の Postfix で <see cref="BreastFlattenApplier.ApplyOverlay"/>
/// を呼び、素キャラ (Tops/Bottoms 未移植) に flatten を当てる。
///
/// Tops/Bottoms override が同 setup() で適用される場合は、それらの Postfix → Apply 経路で
/// ApplySkinShrinkPhase 直後の hook が走り flatten を再注入するので、本 Postfix の出力は上書きされる (安全)。
/// 二重実行防止は <see cref="BreastFlattenApplier.ApplyOverlay"/> 内の <c>_breastflat</c> suffix 判定で行う。
///
/// donor preload host (Tops/BottomsLoader の s_loaderHostRoot 配下) には CharacterHandle.setup が
/// 走らないか走っても CharID 解決に失敗するので、特別な host mutex は不要 (ApplyOverlay 内で
/// 未対応 CharID は amount = 0 で skip される)。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.setup))]
internal static class BreastFlattenSetupPatch
{
    private static bool Prepare()
    {
        bool enabled = Configs.CostumeChangerEnabled.Value;
        if (enabled) PatchLogger.LogInfo("[BreastFlattenSetupPatch] 適用");
        return enabled;
    }

    private static void Postfix(CharacterHandle __instance)
    {
        if (__instance?.Chara == null) return;

        // donor 自身の preload host 配下の character を除外。Tops/BottomsLoader と同じ guard
        // (memory feedback_loader_preload_host_mutex)。
        if (DonorPreloadRegistry.IsAnyHostParent(__instance.Chara)) return;

        var charId = __instance.GetCharID();
        var chara = __instance.Chara;
        bool topsOverride = TopsOverrideStore.TryGet(charId, out _);
        bool bottomsOverride = BottomsOverrideStore.TryGet(charId, out _);
        bool pureNative = !topsOverride && !bottomsOverride;  // ApplyOverlay の push/swap が走る条件

        if (pureNative)
        {
            // cloth (mesh_costume) が Read/Write Disabled で頂点を読めず geometry flatten 不可な場合
            // (Bunnygirl 等)、skin だけ潰すと bust が不整合になるため flatten 全体を skip し native 維持。
            // 既に適用済みなら RestoreFor で戻す。jiggle 物理 (BreastClothTuner) は flatten 非依存なので維持。
            if (BreastFlattenApplier.HasUnflattenableBreastCloth(chara))
            {
                BreastFlattenApplier.RestoreFor(chara);
                BreastClothWeightShifter.RestoreFor(chara);
                BreastClothTuner.ApplyFor(chara, charId);
                PatchLogger.LogWarning($"[BreastFlatten] {chara.name} char={charId}: cloth mesh が Read/Write Disabled のため breast flatten skip (skin/cloth native 維持)");
                return;
            }

            // 肌 push (ApplyOverlay 内) が読む cloth を先に distance-preserve してから ApplyOverlay(swap+push+flatten)。
            // pureNative ⇒ !topsOverride なので cloth は flattenVerts:true（native tops cloth に距離保存）。
            BreastClothWeightShifter.ApplyFor(chara, charId, flattenVerts: true);
            BreastFlattenApplier.ApplyOverlay(chara, charId);
            BreastClothTuner.ApplyFor(chara, charId);
        }
        else
        {
            // Tops/Bottoms override。ApplyOverlay は push しない (gate OFF) ので cloth との順序非依存。
            // Tops/BottomsLoader 経路 (ApplySkinShrinkPhase→ApplyOverlay) で後から上書きされる。
            // flattenVerts は native tops cloth (= !topsOverride) のときのみ距離保存。
            // Tops override 時は false (TopsLoader が distance-preserve 済み)、Bottoms-only 時は true。
            BreastFlattenApplier.ApplyOverlay(chara, charId);
            BreastClothTuner.ApplyFor(chara, charId);
            BreastClothWeightShifter.ApplyFor(chara, charId, flattenVerts: !topsOverride);
        }
    }
}
