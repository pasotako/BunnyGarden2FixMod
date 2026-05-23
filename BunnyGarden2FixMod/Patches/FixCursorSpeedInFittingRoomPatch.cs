using BunnyGarden2FixMod.Utils;
using GB.Extra;
using HarmonyLib;
using UnityEngine;
using GB;
using DG.Tweening;
using Cysharp.Threading.Tasks;
using System;

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
        if (__instance.m_tweener != null && __instance.m_tweener.IsActive() && !__instance.m_tweener.IsComplete())
			{
                __result = UniTask.CompletedTask;
				return false;
			}
			if (__instance.m_loading)
			{
                __result = UniTask.CompletedTask;
				return false;
			}
			if (GBInput.BTriggered || GBInput.RightClick)
			{
				Action onCanceled = __instance.m_onCanceled;
				if (onCanceled != null)
				{
					onCanceled();
				}
			}
			else if (GBInput.ATriggered)
			{
				Action onDecided = __instance.m_onDecided;
				if (onDecided != null)
				{
					onDecided();
				}
			}
			if (GBInput.YPressing)
			{
				__instance.m_skirtWind = Mathf.Lerp(__instance.m_skirtWind, 1f, 0.1f);
			}
			else
			{
				__instance.m_skirtWind = Mathf.Lerp(__instance.m_skirtWind, 0f, 0.1f);
			}
			if (GBInput.YTriggered)
			{
				GBSystem.Instance.PlaySE(SoundManager.SE.FittingRoomWind, false);
			}
			GBSystem.Instance.GetActiveEnvScene().SetCharacterSkirtWeight(__instance.m_charID, __instance.m_skirtWind);
			GBSystem.Instance.GetGameRoomScene().CharacterRoot[0].transform.Rotate(0f, -__instance.getTurnInput() * 3f, 0f);
			int select = __instance.m_select;
			int displayTop = __instance.m_displayTop;

            // -----------------------------------
            // 入力をスキップするディレイ処理を追加
            // -----------------------------------
            InputTimer += Time.deltaTime;
            if (InputTimer > InputDelay)
            {
                if (GBInput.UpTriggeredR)
                {
                    if (__instance.m_select >= __instance.m_displayTop && __instance.m_select < __instance.m_displayTop + 10)
                    {
                        __instance.m_select = (__instance.m_select - 1 + __instance.m_buttons.Count) % __instance.m_buttons.Count;
                    }
                    else
                    {
                        __instance.m_select = __instance.m_displayTop;
                    }
                    InputTimer = 0f;
                }
                else if (GBInput.DownTriggeredR)
                {
                    if (__instance.m_select >= __instance.m_displayTop && __instance.m_select < __instance.m_displayTop + 10)
                    {
                        __instance.m_select = (__instance.m_select + 1) % __instance.m_buttons.Count;
                    }
                    else
                    {
                        __instance.m_select = __instance.m_displayTop + 10 - 1;
                    }
                    InputTimer = 0f;
                }
                else if (GBInput.ScrollAxis > 0f)
                {
                    if (__instance.m_displayTop > 0)
                    {
                        __instance.m_displayTop--;
                    }
                    InputTimer = 0f;
                }
                else if (GBInput.ScrollAxis < 0f && __instance.m_displayTop < __instance.m_buttons.Count - 10)
                {
                    __instance.m_displayTop++;
                    InputTimer = 0f;
                }
            }
            // ----------------------
            // 入力スキップ処理おわり
            // ----------------------
            if (__instance.m_select != select)
            {
                __instance.onSelectChanged();
            }
            if (__instance.m_displayTop != displayTop)
            {
                __instance.updateScroll(false);
            }
            
            __result = UniTask.CompletedTask;
            return false;
    }

}
