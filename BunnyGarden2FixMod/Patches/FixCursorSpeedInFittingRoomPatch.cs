using System.Collections.Generic;
using System.Linq;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Extra;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// フィッティングルームでカーソルが高速移動する現象 (#39) を修正するパッチ。
///
/// <para>
/// FittingRoom.GBUpdate 内の GBInput.UpTriggeredR / DownTriggeredR / ScrollAxis の読み出しだけを
/// Transpiler でゲート付きラッパーへ差し替える。メソッド本体の他の処理 (A/B 決定・キャンセル、
/// Y スカート風、キャラ回転) は毎フレームそのまま実行されるため、旧実装 (GBUpdate 全体を
/// Prefix でスキップ) にあった「スクロール中に決定ボタンが落ちる」副作用がない。
/// </para>
///
/// <para>
/// ゲートは GBInput.isTriggeredR と同じタイミング (初回即時 → 0.5 秒待ち → 約 0.1333 秒間隔) を
/// 再現する。正規のリピート入力 (0.5s / 0.1333s 間隔で発火) はゲートを素通りし、毎フレーム発火する
/// 異常入力だけが制限されるため、他のメニューと同じ操作感になる。入力リリースで即 idle に戻すので
/// 連打 (タップ) も毎回即時に通る。
/// </para>
/// </summary>
[HarmonyPatch(typeof(FittingRoom), nameof(FittingRoom.GBUpdate))]
public static class FixCursorSpeedInFittingRoomPatch
{
    // GBInput.REPEAT_START_TIME と同値
    private const float RepeatStartTime = 0.5f;
    // GBInput.REPEAT_INTERVAL_TIME (0.13333s) より僅かに短くし、フレーム量子化で正規リピートを
    // 取りこぼさないようにする (60fps の異常入力でも 8 フレーム=0.1333s 間隔に揃う)
    private const float RepeatInterval = 0.12f;

    private static readonly RepeatGate s_cursorGate = new();
    private static readonly RepeatGate s_scrollGate = new();

    // GBUpdate は ScrollAxis を 1 フレームに 2 回読む (>0 判定と <0 判定)。
    // 1 回目の Accept 消費で 2 回目が 0 になるとスクロール下方向が常に死ぬため、
    // フレーム単位でゲート結果をキャッシュして両読み出しに同じ値を返す。
    private static int s_scrollFrame = -1;
    private static float s_scrollValue;

    private static bool Prepare()
    {
        PatchLogger.LogInfo(
            $"[{nameof(FixCursorSpeedInFittingRoomPatch)}] " +
            $"{nameof(FittingRoom)}.{nameof(FittingRoom.GBUpdate)} をパッチしました。");
        return true;
    }

    // 入力リリース検知。ブロックは一切せず、ゲートの idle 復帰だけを担う。
    private static void Prefix()
    {
        if (!GBInput.UpPressing && !GBInput.DownPressing) s_cursorGate.NotifyReleased();
        if (GBInput.ScrollAxis == 0f) s_scrollGate.NotifyReleased();
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var upGetter = AccessTools.PropertyGetter(typeof(GBInput), nameof(GBInput.UpTriggeredR));
        var downGetter = AccessTools.PropertyGetter(typeof(GBInput), nameof(GBInput.DownTriggeredR));
        var scrollGetter = AccessTools.PropertyGetter(typeof(GBInput), nameof(GBInput.ScrollAxis));

        var codes = instructions.ToList();
        int up = 0, down = 0, scroll = 0;

        foreach (var ins in codes)
        {
            // ラベル・例外ブロックを保持するため命令オブジェクトを書き換える
            if (ins.Calls(upGetter))
            {
                ins.operand = AccessTools.Method(typeof(FixCursorSpeedInFittingRoomPatch), nameof(GatedUpTriggered));
                up++;
            }
            else if (ins.Calls(downGetter))
            {
                ins.operand = AccessTools.Method(typeof(FixCursorSpeedInFittingRoomPatch), nameof(GatedDownTriggered));
                down++;
            }
            else if (ins.Calls(scrollGetter))
            {
                ins.operand = AccessTools.Method(typeof(FixCursorSpeedInFittingRoomPatch), nameof(GatedScrollAxis));
                scroll++;
            }
        }

        if (up == 0 || down == 0 || scroll == 0)
        {
            PatchLogger.LogError(
                $"[{nameof(FixCursorSpeedInFittingRoomPatch)}] 置換対象が見つかりません " +
                $"(Up={up}, Down={down}, Scroll={scroll})。ゲームのアップデートでパッチが機能していない可能性があります。");
        }

        return codes;
    }

    private static bool GatedUpTriggered() => GBInput.UpTriggeredR && s_cursorGate.Accept();

    private static bool GatedDownTriggered() => GBInput.DownTriggeredR && s_cursorGate.Accept();

    private static float GatedScrollAxis()
    {
        if (Time.frameCount != s_scrollFrame)
        {
            s_scrollFrame = Time.frameCount;
            float raw = GBInput.ScrollAxis;
            s_scrollValue = raw != 0f && s_scrollGate.Accept() ? raw : 0f;
        }
        return s_scrollValue;
    }

    /// <summary>
    /// キーリピートゲート。押し始めは即時に通し、押しっぱなし中は
    /// 0.5 秒経過後に約 0.1333 秒間隔でのみ通す (GBInput.isTriggeredR と同じスケジュール)。
    /// </summary>
    private sealed class RepeatGate
    {
        private bool m_held;
        private float m_burstStart;
        private float m_lastAccept;

        public void NotifyReleased() => m_held = false;

        public bool Accept()
        {
            float now = Time.unscaledTime;
            if (!m_held)
            {
                m_held = true;
                m_burstStart = now;
                m_lastAccept = now;
                return true; // 押し始めは即時
            }
            if (now - m_burstStart >= RepeatStartTime && now - m_lastAccept >= RepeatInterval)
            {
                m_lastAccept = now;
                return true;
            }
            return false;
        }
    }
}
