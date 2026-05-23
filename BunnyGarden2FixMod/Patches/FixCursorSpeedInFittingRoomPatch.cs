using BunnyGarden2FixMod.Utils;
using GB.Extra;
using HarmonyLib;
using UnityEngine;
using GB;
using Cysharp.Threading.Tasks;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// フィッティングルームでカーソル速度が速くなる現象を修正するパッチ
// FittingRoom.GBUpdate に，前回の入力から InputDelay が経過するまで
// 入力を受け付けない処理を追加
/// </summary>

[HarmonyPatch(typeof(FittingRoom), nameof(FittingRoom.GBUpdate))]
public static class FixCursorSpeedInFittingRoomPatch
{
    private const float InputDelay = 0.12f;
    private static float InputTimer = 0f; 
    private static bool Prepare()
    {
        PatchLogger.LogInfo(
            $"[{nameof(FixCursorSpeedInFittingRoomPatch)}] " +
            $"{nameof(FittingRoom)}.{nameof(FittingRoom.GBUpdate)} " +
            $"をパッチしました。");
        return true;
    }
    public static bool Prefix(FittingRoom __instance, ref UniTask __result)
    {
        InputTimer += Time.deltaTime;

        bool isMovingInput = GBInput.UpTriggeredR ||
            GBInput.DownTriggeredR ||
            GBInput.ScrollAxis > 0f ||
            (GBInput.ScrollAxis < 0f && __instance.m_displayTop < __instance.m_buttons.Count - 10);

        if (!isMovingInput)
            return true;

        // -----------------------------------
        // 入力をスキップするディレイ処理を追加
        // -----------------------------------
        if (InputTimer > InputDelay)
        {
            InputTimer = 0f;
            return true;
        }

        // InputDelayに満たなければ何もしない
        __result = UniTask.CompletedTask;
        return false;
    }
}
