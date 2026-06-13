using System;
using System.Collections.Generic;
using GB;

namespace BunnyGarden2FixMod.Utils;

/// <summary>
/// <see cref="MSGID_SPLIT_2"/> のメンバを「実行時に名前で」解決するヘルパー。
///
/// <para>
/// <c>(int)MSGID_SPLIT_2.SOME_NAME</c> のような enum 参照は C# コンパイル時に
/// **整数定数として焼き込まれる**。MOD は特定バージョンの Assembly-CSharp.dll に対して
/// ビルドされるため、ゲーム更新でメッセージテーブルにエントリが挿入されると、
/// 焼き込まれた定数と実行中ゲームの enum 値がずれる。
/// </para>
///
/// <para>
/// 実害の例 (ゲーム v1.0.5): <c>ASMR_HOLIDAY_AFTER_1/2</c> の 2 件が FITTING_ROOM 系より
/// 前に挿入され、以降の値が一律 +2 ずれた。結果:
/// <list type="bullet">
///   <item>フィッティングの衣装/パンツ/ストッキングのラベルが 2 つ前を表示</item>
///   <item><see cref="Patches.ExtraResolutionPatch"/> の DISPLAY 行 ID 判定が常に不一致になり拡張解像度が機能停止</item>
/// </list>
/// </para>
///
/// <para>
/// enum の「名前」はバージョン間で安定しており、ずれるのは数値だけなので、
/// 実行時に <see cref="Enum.Parse(Type, string)"/> で名前→値を引けば起動中ゲームの値に追従できる。
/// MOD は実行時にゲーム本体の <see cref="MSGID_SPLIT_2"/> 型をロードするため、
/// この解決はそのバージョンの正しい値を返す。
/// </para>
///
/// 呼び出しは <c>nameof</c> で名前を渡し、コンパイル時定数をフォールバックとして併用する
/// (rename 追従 + 解決失敗時の保険)。
/// </summary>
internal static class MsgIdResolver
{
    private static readonly Dictionary<string, int> s_cache = new();

    /// <summary>名前から実行時 ID を引く。解決失敗時は <paramref name="compileFallback"/> を返す。</summary>
    public static int Id(string name, int compileFallback)
    {
        if (s_cache.TryGetValue(name, out var cached)) return cached;

        int value;
        try
        {
            value = (int)Enum.Parse(typeof(MSGID_SPLIT_2), name);
        }
        catch
        {
            // 名前が存在しない (将来バージョンで削除/改名) 場合は焼き込み値で代替する。
            value = compileFallback;
            PatchLogger.LogWarning(
                $"[MsgIdResolver] '{name}' を実行時解決できませんでした。焼き込み値 {compileFallback} を使用します。");
        }
        s_cache[name] = value;
        return value;
    }

    /// <summary>名前から実行時 <see cref="MSGID"/> を生成する。フォールバックは enum メンバで渡す。</summary>
    public static MSGID Msg(string name, MSGID_SPLIT_2 compileFallback)
        => (MSGID)Id(name, (int)compileFallback);
}
