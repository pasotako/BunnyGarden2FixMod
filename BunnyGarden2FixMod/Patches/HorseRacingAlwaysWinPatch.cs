using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BunnyGarden2FixMod.Utils;
using GB.Scene;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 競馬で賭けた馬が必ず勝つチートパッチ（issue #54）。
///
/// ■ ゲーム側の仕組み
///   HorseRacing.Execute() はレース前に全て確定する:
///     - オッズ rate: 本命 1.4〜2.2 / 対抗 3.5〜6.3 / 大穴 55.0〜70.0 倍
///     - 勝ち馬 first = getHorseIndexByRank(0)（m_runPowers 由来。賭け入力より前に確定）
///     - 実況 m_events: (RaceEventParam, 馬index) の列。announce() が index の馬名を会話へ流す
///     - 当選判定: betHorse == first → 払い戻し betMoney * rate[betHorse] / 10
///
/// ■ 実装（Transpiler）
///   Execute は async ステートマシン（構造体）。構造体 MoveNext への Prefix は
///   MonoMod で Invalid IL になるため、Gamble 系と同様に Transpiler を使う。
///   MoveNext の先頭に「rig 呼び出し」を注入して毎 resume 実行する:
///     first = Rig(betHorse, first, this)
///   これにより await のどの再開時点で Config を ON にしても、次の進行で結果が
///   賭けた馬の勝利へ切り替わる（レース途中・結果確定後の ON でも当たる）。
///
///   Rig は「賭けが確定した本物の betHorse」だけを対象にし（初期化前の default 0 を誤検出しない）、
///   未再生の実況・着順発表の馬 index を trueWinner ↔ betHorse でスワップして表示を勝利に揃える。
///   スワップは 1 レースにつき 1 回（idempotent）。払い戻しは元コードの rate[betHorse] を使うため
///   大穴に賭ければ 55〜70 倍が当たる。フィールド解決に失敗したら no-op（LogError）で安全側に倒す。
/// </summary>
[HarmonyPatch]
public static class HorseRacingAlwaysWinPatch
{
    // レース単位の状態（同時に走る競馬は 1 つ）
    private static HorseRacing s_race;
    private static bool s_seenBetInit; // betHorse == -1 を観測したか（＝賭け入力フェーズに入った印）
    private static bool s_rigged;      // m_events を既にスワップ済みか

    private static MethodBase TargetMethod()
    {
        var sm = typeof(HorseRacing)
            .GetNestedTypes(AccessTools.all)
            .FirstOrDefault(t => t.Name.Contains("Execute") && t.GetMethod("MoveNext", AccessTools.all) != null);
        if (sm == null)
        {
            PatchLogger.LogError("[HorseRacingAlwaysWin] Execute ステートマシン型が見つかりません");
            return null;
        }
        return sm.GetMethod("MoveNext", AccessTools.all);
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var codes = instructions.ToList();
        var smType = __originalMethod.DeclaringType;
        var fields = smType.GetFields(AccessTools.all);

        var betHorseField = fields.FirstOrDefault(f => f.FieldType == typeof(int) && f.Name.Contains("betHorse"));
        var firstField = fields.FirstOrDefault(f => f.FieldType == typeof(int) && f.Name.Contains("first"));
        var thisField = fields.FirstOrDefault(f => f.FieldType == typeof(HorseRacing));
        var rig = AccessTools.Method(typeof(HorseRacingAlwaysWinPatch), nameof(Rig));

        if (betHorseField == null || firstField == null || thisField == null)
        {
            PatchLogger.LogError(
                "[HorseRacingAlwaysWin] hoisted フィールドを解決できませんでした " +
                $"(betHorse={betHorseField != null}, first={firstField != null}, this={thisField != null})。" +
                "ゲームのアップデートでパッチが機能していない可能性があります。");
            return codes; // 無改変で返す（機能はしないが安全）
        }

        // MoveNext 先頭に注入:
        //   this.first = Rig(this.betHorse, this.first, this.<>4__this);
        var inject = new List<CodeInstruction>
        {
            new CodeInstruction(OpCodes.Ldarg_0),                    // stfld 用の SM this
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, betHorseField),       // int betHorse
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, firstField),          // int first
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, thisField),           // HorseRacing this
            new CodeInstruction(OpCodes.Call, rig),                  // -> int
            new CodeInstruction(OpCodes.Stfld, firstField),          // this.first = ...
        };
        codes.InsertRange(0, inject);

        PatchLogger.LogInfo("[HorseRacingAlwaysWin] Execute.MoveNext に rig 注入を適用しました");
        return codes;
    }

    /// <summary>
    /// 賭けた馬を勝たせるよう first を返し、必要なら未再生の実況 index をスワップする。
    /// 途中で Config が OFF になった場合は、スワップを元に戻して勝ち馬も元へ復元する
    /// （スワップは対合なので同じペアで再スワップすれば復元できる）。
    /// ステートマシンから直接呼ばれるため public static。
    /// </summary>
    public static int Rig(int betHorse, int first, HorseRacing hr)
    {
        try
        {
            if (hr == null) return first;

            // 新しいレースに入ったら状態をリセット
            if (!ReferenceEquals(hr, s_race)) { s_race = hr; s_seenBetInit = false; s_rigged = false; }
            if (betHorse == -1) s_seenBetInit = true; // 賭け入力フェーズ(betHorse=-1)を通過した印

            // 賭け確定後の本物の betHorse のみ対象（初期化前の default 0 を誤検出しない）
            bool validBet = s_seenBetInit && betHorse >= 0 && betHorse <= 2;
            if (!validBet) return first; // 賭け未確定: 介入しない（first はゲーム設定のまま）

            int trueWinner = hr.getHorseIndexByRank(0); // m_runPowers 由来。スワップしても不変
            // リグしたい = ON かつ 賭けた馬が本来の勝ち馬でない
            bool wantRig = Configs.HorseRacingAlwaysWinEnabled.Value && betHorse != trueWinner;

            if (wantRig && !s_rigged)
            {
                SwapEvents(hr, trueWinner, betHorse);
                s_rigged = true;
                PatchLogger.LogInfo($"[HorseRacingAlwaysWin] レース結果をリグ: 勝ち馬 {trueWinner} → {betHorse}（賭けた馬）");
            }
            else if (!wantRig && s_rigged)
            {
                // OFF になった / 対象外になった → 実況スワップを戻し、勝ち馬も元へ復元
                SwapEvents(hr, trueWinner, betHorse);
                s_rigged = false;
                PatchLogger.LogInfo($"[HorseRacingAlwaysWin] リグを解除して元に戻しました（勝ち馬 {trueWinner}）");
            }

            // リグ中は betHorse を勝たせ、そうでなければ本来の勝ち馬(trueWinner)へ戻す
            return s_rigged ? betHorse : trueWinner;
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[HorseRacingAlwaysWin] リグ処理に失敗: {ex.Message}");
            return first;
        }
    }

    /// <summary>m_events 内の勝ち馬 index を a ↔ b でスワップする（対合）。</summary>
    private static void SwapEvents(HorseRacing hr, int a, int b)
    {
        if (hr.m_events == null) return;
        for (int i = 0; i < hr.m_events.Count; i++)
        {
            var (p, h) = hr.m_events[i];
            if (h == a) hr.m_events[i] = (p, b);
            else if (h == b) hr.m_events[i] = (p, a);
        }
    }
}
