using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using HarmonyLib;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ゲーム内カレンダー日付の進行を止めるチートパッチ（issue #41「The World」）。
///
/// ■ 仕組み
///   一日の終わりは <see cref="GameData.ToNextDay"/> で処理され、その中の
///   <c>m_gameDate = m_gameDate.AddDays(num4);</c>（= 日付を進める唯一の代入）で日付が進む。
///   この <c>stfld m_gameDate</c> を、停止中は「進めない（進行前の日付のまま据え置く）」代入関数
///   <see cref="StoreGameDate"/> へトランスパイラで差し替える。
///
///   代入の時点で据え置くため、その後に走るショップ品揃え・SNS 更新・出勤順
///   （setShopLineup / updateSNS / UpdateTodaysCastOrder / updateTodaysPanties, L570 付近）や
///   遷移先シーン判定まで、すべて「当日の日付」で計算される。一日分の各種リセット
///   （会話履歴・購入品・好感度反映など）は通常どおり行われる。
///
/// ■ 注意（issue 調査メモ）
///   本作のルート進行（After イベント / 衣装デー / 終盤の日付依存イベント）は
///   カレンダー日付に依存する。日付を止めたままだとこれらの先のイベントへ進めないため、
///   進めたいときは Config を OFF に戻してから日を進める運用を想定している
///   （ON/OFF をプレイヤーが切り替えて使う）。実行時に Config を見るため F9 で即時 ON/OFF 可。
/// </summary>
[HarmonyPatch]
public static class StopTimeProgressionPatch
{
    private static MethodBase TargetMethod()
    {
        var smType = typeof(GameData)
            .GetNestedTypes(AccessTools.all)
            .FirstOrDefault(t => t.Name.Contains("ToNextDay"));
        if (smType == null)
        {
            PatchLogger.LogError("[StopTimeProgression] ToNextDay ステートマシン型が見つかりません");
            return null;
        }
        var method = smType.GetMethod("MoveNext", AccessTools.all);
        if (method == null)
            PatchLogger.LogError("[StopTimeProgression] MoveNext メソッドが見つかりません");
        return method;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var gameDateField = AccessTools.Field(typeof(GameData), "m_gameDate");
        var storeMethod = AccessTools.Method(typeof(StopTimeProgressionPatch), nameof(StoreGameDate));

        var codes = instructions.ToList();
        bool replaced = false;
        foreach (var c in codes)
        {
            // ToNextDay 内で唯一の m_gameDate への代入（日付を進める箇所）を差し替える。
            // stfld 直前のスタックは [GameData, DateTime] で StoreGameDate の引数と一致する。
            if (!replaced && c.opcode == OpCodes.Stfld && (FieldInfo)c.operand == gameDateField)
            {
                c.opcode = OpCodes.Call;
                c.operand = storeMethod;
                replaced = true;
            }
        }

        if (!replaced)
            PatchLogger.LogError("[StopTimeProgression] m_gameDate への代入が見つかりませんでした");
        else
            PatchLogger.LogInfo("[StopTimeProgression] GameData.ToNextDay の日付進行をパッチしました");

        return codes;
    }

    /// <summary>
    /// 進めた日付を <c>m_gameDate</c> へ格納する。ただし停止が有効なときは進行前の日付
    /// （<c>m_gamePreviousDate</c>、ToNextDay 内で進行直前の m_gameDate に設定済み）を据え置く。
    /// 元の <c>stfld m_gameDate</c> と同じスタック（GameData, DateTime）を消費する。
    /// </summary>
    public static void StoreGameDate(GameData gd, DateTime advanced)
    {
        gd.m_gameDate = Configs.StopTimeProgressionEnabled.Value ? gd.m_gamePreviousDate : advanced;
    }
}

/// <summary>
/// 日付進行停止が有効なときに「平日遭遇イベント」の発生を抑制する補助パッチ。
///
/// ■ なぜ必要か
///   <see cref="GameData.ToNextDay"/> は平日遭遇候補が見つかると遷移先シーンを
///   <c>WeekdayEncountScene</c> に決定する（候補発見 = flag3）。この判定は日付を進める代入より
///   前に行われるため、<see cref="StopTimeProgressionPatch"/> で日付を据え置いても、
///   未来日向けに用意された遭遇データとシーンが食い違い WeekdayEncountScene.Start() で
///   NullReferenceException になる。
///
///   そこで停止中は <c>queryWeekdayEncountCandidate</c> を常に空にして候補を発生させない。
///   これにより flag3 が立たず遷移先は HomeScene のままとなる。
///   （停止中は「同じ日を繰り返す」用途なので、先の平日遭遇へ飛ばさないのは意図どおり。）
/// </summary>
[HarmonyPatch(typeof(GameData), "queryWeekdayEncountCandidate")]
public static class SuppressWeekdayEncountWhenFrozenPatch
{
    private static bool Prefix(ref List<ValueTuple<CharID, int>> __result)
    {
        if (!Configs.StopTimeProgressionEnabled.Value) return true; // 通常どおり実行
        __result = new List<ValueTuple<CharID, int>>(); // 候補なし → flag3 立たず WeekdayEncountScene へ行かない
        return false;
    }
}
