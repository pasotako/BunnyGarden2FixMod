using System.Collections.Generic;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 頂点変位フィールド (disp) を mesh トポロジ (triangles) 上で Jacobi-Laplacian 平滑化する utility。
///
/// 用途: distance-preserve (<see cref="MeshDistancePreserver"/>) の出力補正成分 <c>disp = preserved - base</c>
/// を平滑化し、逆距離² サンプリングの高周波ノイズ (BreastFlatten 高 amount で可視化する「ガタつき」) を除去する。
/// 平滑化対象は補正成分のみで base 形状は呼出側が保持するため、cloth 固有ディテール (襞・縫い目) は鈍らない。
///
/// Laplacian (近傍平均への lerp) は凸結合 = 縮小写像なので、出力は近傍 disp の範囲内に収まり過大値 (トゲ) を
/// 生成しない。base 形状でなく既存の低バイアス補正成分のみを平均するため新たな大変位を作らない。
/// </summary>
internal static class MeshDisplacementSmoother
{
    /// <summary>
    /// 変位フィールド <paramref name="disp"/> を <paramref name="triangles"/> の頂点隣接で
    /// Jacobi-Laplacian 平滑化する (in-place)。
    /// </summary>
    /// <param name="disp">頂点変位 (in-place 書換)。長さは <paramref name="vertexCount"/> と一致前提。</param>
    /// <param name="triangles">mesh の triangle index 配列 (3 つ組)。</param>
    /// <param name="vertexCount">頂点数 (disp.Length)。</param>
    /// <param name="iterations">反復回数。0 以下で no-op。</param>
    /// <param name="strength">各反復の近傍平均への lerp 率。Clamp01 される。</param>
    public static void SmoothInPlace(Vector3[] disp, int[] triangles, int vertexCount, int iterations, float strength)
    {
        if (disp == null || disp.Length != vertexCount) return;
        if (triangles == null || triangles.Length < 3) return;
        if (iterations <= 0) return;
        strength = Mathf.Clamp01(strength);
        if (strength <= 0f) return;

        // 頂点隣接を triangle から構築 (各 tri の 3 辺)。
        var adj = new HashSet<int>[vertexCount];
        for (int i = 0; i < vertexCount; i++) adj[i] = new HashSet<int>();
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                continue;
            adj[a].Add(b); adj[a].Add(c);
            adj[b].Add(a); adj[b].Add(c);
            adj[c].Add(a); adj[c].Add(b);
        }

        var src = disp;
        var dst = new Vector3[vertexCount];
        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = 0; i < vertexCount; i++)
            {
                var neighbors = adj[i];
                if (neighbors.Count == 0)
                {
                    // 孤立頂点は据え置き。この明示コピーが無いと swap 後の dst 側に前々 iter 値/初期ゼロが
                    // 残り Jacobi が壊れる (全頂点が毎 iter dst[i] に必ず書かれる前提が swap 正当性に必須)。
                    dst[i] = src[i];
                    continue;
                }
                Vector3 sum = Vector3.zero;
                foreach (int j in neighbors) sum += src[j];
                Vector3 mean = sum / neighbors.Count;
                dst[i] = Vector3.Lerp(src[i], mean, strength);
            }
            var tmp = src; src = dst; dst = tmp;   // swap (Jacobi: 前 iter 値を参照)
        }

        // 最終結果は src 側にある (各 iter 末の swap 後)。disp 配列に残すため src != disp のとき copy back。
        // iterations 偶奇どちらでも正しい: 偶数回は src==disp (copy 不要)、奇数回は src==dst バッファ (copy)。
        if (!ReferenceEquals(src, disp))
            System.Array.Copy(src, disp, vertexCount);
    }
}
