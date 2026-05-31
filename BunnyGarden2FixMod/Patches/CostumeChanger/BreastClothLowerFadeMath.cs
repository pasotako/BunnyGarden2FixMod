using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// full-body 衣装 (1 枚 mesh_costume) の native fit で、skin_lower 域 (腰ラインより下) の衣装頂点へ
/// 胸 flatten の距離保存補正が漏れるのを止める Y フェードの純算術。
///
/// 使い方:
///   1. <see cref="ComputeFadeTopY"/> で baby-lower の max Y (腰ライン) を得る。
///   2. <see cref="ApplyLowerFade"/> で Preserve 出力 (preservedPv/preservedBw) を腰ラインより下で
///      base (native) へ引き戻す。上=full補正 / 下=native / 帯内=滑らか移行。
///
/// 注: yTop (= baby-lower の max Y) は「胸の下端」ではなく「腰の最上端」。胸 flatten 補正は腰より上に
/// 集中するため「腰より下を base へ戻す」は方向として正しい。yTop は baby-lower donor の mesh-local Y、
/// 判定対象は costume の mesh-local Y。両者が同一座標空間にある前提（既存 SkinUpperWeightConformer と同じ）。
///
/// UnityEngine + System のみ依存 (BepInEx/Harmony 非依存) → 純関数として単体テスト可能。
/// </summary>
internal static class BreastClothLowerFadeMath
{
    /// <summary>
    /// baby-lower 頂点群の最大 Y (= 腰ライン) を返す。null / 空は <see cref="float.NaN"/>。
    /// </summary>
    public static float ComputeFadeTopY(Vector3[] babyLowerVerts)
    {
        if (babyLowerVerts == null || babyLowerVerts.Length == 0) return float.NaN;
        float maxY = float.NegativeInfinity;
        for (int i = 0; i < babyLowerVerts.Length; i++)
        {
            float y = babyLowerVerts[i].y;
            if (y > maxY) maxY = y;
        }
        return maxY;
    }

    /// <summary>
    /// Preserve 出力 <paramref name="preservedPv"/> / <paramref name="preservedBw"/> を in-place で
    /// base 側へフェードする。フェード係数:
    ///   f = (width &lt;= 0) ? (y &gt;= yTop ? 1 : 0)
    ///                       : SmoothStep01((y - (yTop - width)) / width)     // y = basePv[i].y
    ///   preservedPv[i] = Lerp(basePv[i], preservedPv[i], f)
    ///   preservedBw[i] = (f &lt; 0.5) ? baseBw[i] : preservedBw[i]  (両 bw 配列が長さ一致のときのみ)
    /// </summary>
    /// <returns>
    /// base 寄りに引き戻した頂点数 (f &lt; 1 の頂点)。デバッグログ用。
    /// <paramref name="yTop"/> が NaN / basePv と preservedPv の長さ不一致のときは -1 (caller は全域焼きに fallback)。
    /// </returns>
    public static int ApplyLowerFade(
        Vector3[] basePv, Vector3[] preservedPv,
        BoneWeight[] baseBw, BoneWeight[] preservedBw,
        float yTop, float width)
    {
        if (float.IsNaN(yTop)) return -1;
        if (basePv == null || preservedPv == null || basePv.Length != preservedPv.Length) return -1;

        bool fadeBw = baseBw != null && preservedBw != null
                      && baseBw.Length == basePv.Length && preservedBw.Length == basePv.Length;

        int faded = 0;
        for (int i = 0; i < basePv.Length; i++)
        {
            float y = basePv[i].y;
            float f;
            if (width <= 0f)
                f = (y >= yTop) ? 1f : 0f;
            else
                f = SmoothStep01((y - (yTop - width)) / width);

            if (f < 1f)
            {
                preservedPv[i] = Vector3.Lerp(basePv[i], preservedPv[i], f);
                faded++;
            }
            if (fadeBw && f < 0.5f)
                preservedBw[i] = baseBw[i];
        }
        return faded;
    }

    /// <summary>Hermite smoothstep。t&lt;=0→0 / t&gt;=1→1 / それ以外 t²(3−2t)。</summary>
    private static float SmoothStep01(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }
}
