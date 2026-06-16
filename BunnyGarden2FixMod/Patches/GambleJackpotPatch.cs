using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Home;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// ギャンブルのジャックポット（ペカリボーナス）を常時確定させるチートパッチ。
///
/// ■ 仕組み
///   Gamble.executeGamble() のペカリ抽選は <c>if (Random.Range(0f, 100f) &lt;= num3)</c> で行われる
///   （num3 = BonusProbability）。この <c>Random.Range(0f, 100f)</c> を、有効時は常に 0 を返す
///   ラッパーへトランスパイラで差し替えることで、<c>0 &lt;= num3</c> が必ず成立しペカリが確定する。
///   ペカリ成立後は元コードが winAmount をジャックポット額に再計算する。
///
///   当初は <c>GambleParam.BonusProbability</c> getter の Postfix で巨大値を返す実装にしていたが、
///   getter が JIT にインライン化されて Postfix が当たらず機能しなかったため、確実に効く
///   call サイト差し替え方式へ変更した。
///
///   <c>Random.Range(0f, 100f)</c> は executeGamble 内で唯一の float 版 Random.Range なので、
///   他の int 版（賞金抽選など）には影響しない。実行時に Config を見るため F9 での即時 ON/OFF に対応。
/// </summary>
[HarmonyPatch]
public static class GambleJackpotPatch
{
    private static MethodBase TargetMethod()
    {
        var smType = typeof(Gamble)
            .GetNestedTypes(AccessTools.all)
            .FirstOrDefault(t => t.Name.Contains("executeGamble"));
        if (smType == null)
        {
            PatchLogger.LogError("[GambleJackpot] executeGamble ステートマシン型が見つかりません");
            return null;
        }
        var method = smType.GetMethod("MoveNext", AccessTools.all);
        if (method == null)
            PatchLogger.LogError("[GambleJackpot] MoveNext メソッドが見つかりません");
        return method;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var realRandom = AccessTools.Method(typeof(Random), nameof(Random.Range),
            new[] { typeof(float), typeof(float) });
        var replacement = AccessTools.Method(typeof(GambleJackpotPatch), nameof(PekariProbabilityRoll));

        var codes = instructions.ToList();
        bool replaced = false;
        foreach (var c in codes)
        {
            // executeGamble 内で唯一の Random.Range(float,float) = ペカリゲート。先頭一致で差し替える。
            if (!replaced && c.Calls(realRandom))
            {
                c.operand = replacement;
                replaced = true;
            }
        }

        if (!replaced)
            PatchLogger.LogError("[GambleJackpot] ペカリ抽選の Random.Range(float,float) が見つかりませんでした");
        else
            PatchLogger.LogInfo("[GambleJackpot] ペカリ確定トランスパイラを適用しました");

        return codes;
    }

    /// <summary>
    /// ペカリ抽選用 Random.Range(0f,100f) の差し替え先。
    /// ジャックポット確定が有効なら 0 を返し（0 &lt;= num3 が必ず成立）ペカリを確定させる。
    /// 無効時は本来どおり Random.Range(min,max) を返す。ステートマシンから直接呼ばれるため public static。
    /// </summary>
    public static float PekariProbabilityRoll(float min, float max)
    {
        if (Configs.GambleAlwaysJackpotEnabled.Value) return min; // = 0f
        return Random.Range(min, max);
    }
}

/// <summary>
/// ギャンブルの当落演出（GambleUI.GambleDirection）を 20 倍速で再生するチートパッチ。
///
/// ■ 仕組み
///   旧実装は GambleDirection を丸ごとスキップしていたが、演出内で行われる UI 状態の更新
///   （rate-select / description の非表示化、コイン生成 setupCoins など）まで飛ばすため
///   結果画面の UI が崩れた。そこで演出は通常どおり全て実行し、<see cref="Time.timeScale"/> を
///   20 に上げて時間ベースの待機・Tween を一気に進める方式へ変更した。
///
///   GambleDirection は <c>using (new ScopedFastForward())</c> で囲まれており、ScopedFastForward は
///   毎フレーム timeScale を 1（A/Y 長押しで 2）に戻す。これに勝つため、専用ドライバの LateUpdate
///   （Update より後）で timeScale を 20 に上書きする。スコープ終了時の ScopedFastForward.Dispose が
///   timeScale を 1 に戻すので、演出後は通常速度に復帰する（Dispose は安全網も兼ねる）。
///
///   ScopedFastForward は Work（労働）でも使われるため、ギャンブル中だけ作用するよう
///   <see cref="GambleFastForwardDriver"/> のフラグで制御する。実行時に Config を見る。
/// </summary>
[HarmonyPatch(typeof(GambleUI), nameof(GambleUI.GambleDirection))]
public static class GambleFastDirectionPatch
{
    private static bool Prepare()
    {
        PatchLogger.LogInfo("[GambleFastDirection] GambleUI.GambleDirection をパッチしました（演出 20 倍速）");
        return true;
    }

    private static void Prefix()
    {
        if (Configs.GambleFastDirectionEnabled.Value)
            GambleFastForwardDriver.Begin();
    }
}

/// <summary>
/// ギャンブル演出のスコープ終了（ScopedFastForward.Dispose）で高速再生を解除する。
/// Work 側の Dispose でも呼ばれるが、ギャンブル中でなければ <see cref="GambleFastForwardDriver.End"/> は no-op。
/// </summary>
[HarmonyPatch(typeof(ScopedFastForward), nameof(ScopedFastForward.Dispose))]
public static class GambleFastForwardEndPatch
{
    private static void Postfix() => GambleFastForwardDriver.End();
}

/// <summary>
/// ギャンブル演出中だけ <see cref="Time.timeScale"/> を高速側へ上書きするドライバ。
/// LateUpdate（Update 後）で設定することで、ScopedFastForward が Update で戻す timeScale に勝つ。
/// </summary>
internal sealed class GambleFastForwardDriver : MonoBehaviour
{
    private const float FastScale = 20f;

    private static GambleFastForwardDriver s_instance;
    private static bool s_active;

    public static void Begin()
    {
        EnsureInstance();
        s_active = true;
    }

    public static void End()
    {
        if (!s_active) return;
        s_active = false;
        // ScopedFastForward.Dispose も timeScale=1 にするが、二重の安全網として明示的に戻す。
        Time.timeScale = 1f;
    }

    private static void EnsureInstance()
    {
        if (s_instance != null) return;
        var go = new GameObject("GambleFastForwardDriver");
        DontDestroyOnLoad(go);
        s_instance = go.AddComponent<GambleFastForwardDriver>();
    }

    private void LateUpdate()
    {
        if (s_active && Configs.GambleFastDirectionEnabled.Value)
            Time.timeScale = FastScale;
    }
}
