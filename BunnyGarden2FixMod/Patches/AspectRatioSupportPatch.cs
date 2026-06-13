using BunnyGarden2FixMod.Utils;
using GB;
using GB.Scene;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ゲーム本体の 16:9 固定チェックを、バー入店中だけウルトラワイド比率へ差し替える。
/// </summary>
[HarmonyPatch]
public static class AspectRatioSupportPatch
{
    private const float VanillaAspect = 1.7777778f;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GBSystem), "Update");

        foreach (var nestedType in typeof(FirstScene).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (!typeof(IAsyncStateMachine).IsAssignableFrom(nestedType))
            {
                continue;
            }

            var moveNext = AccessTools.Method(nestedType, "MoveNext");
            if (moveNext != null)
            {
                yield return moveNext;
            }
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var getAspectMethod = AccessTools.Method(
            typeof(GameplayFullscreenUltrawideSupport),
            nameof(GameplayFullscreenUltrawideSupport.GetAspectRatioForGameChecks));

        var codes = instructions.ToList();
        int replaced = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_R4
                && codes[i].operand is float value
                && value == VanillaAspect)
            {
                // ラベル・例外ブロックを保持するため命令を書き換える (差し替えない)
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = getAspectMethod;
                replaced++;
            }
        }

        // GBSystem.Update は実行中の常時チェック経路で、ここが 0 件なら機能停止が確定。
        // FirstScene 側のステートマシンは定数を含まないメソッドも正常系なので per-method 0 件は許容する。
        if (replaced == 0 && original?.DeclaringType == typeof(GBSystem))
        {
            PatchLogger.LogError(
                "[AspectRatioSupport] GBSystem.Update に 16:9 定数 (1.7777778f) が見つかりませんでした。" +
                "ゲームのアップデートでパッチが機能していない可能性があります。");
        }
        else if (replaced > 0)
        {
            PatchLogger.LogInfo($"[AspectRatioSupport] {original?.DeclaringType?.Name}.{original?.Name}: 16:9 定数を {replaced} 箇所差し替えました。");
        }

        return codes;
    }
}
