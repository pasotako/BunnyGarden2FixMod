using UnityEngine;
using Xunit;
using BunnyGarden2FixMod.Patches.CostumeChanger;

// 純関数 MeshDisplacementSmoother.SmoothInPlace のテスト。Vector3[]/int[] のみ依存で headless 構築可。
// Jacobi-Laplacian の不変条件 (凸結合=トゲ非生成, 定数場不変, 偶奇 copy-back, 孤立据え置き) と
// ガード/範囲外 index skip を検証する。
public class MeshDisplacementSmootherTests
{
    private static void AssertV3(Vector3 expected, Vector3 actual, int precision = 4)
    {
        Assert.Equal(expected.x, actual.x, precision);
        Assert.Equal(expected.y, actual.y, precision);
        Assert.Equal(expected.z, actual.z, precision);
    }

    [Fact]
    public void SmoothInPlace_SingleTriangle_AveragesNeighborsExactly()
    {
        // 1 triangle {0,1,2}: 各頂点は他 2 頂点と隣接。spike を頂点0 に置く。
        var disp = new[] { new Vector3(0, 3, 0), Vector3.zero, Vector3.zero };
        var tris = new[] { 0, 1, 2 };

        MeshDisplacementSmoother.SmoothInPlace(disp, tris, 3, 1, 0.5f);

        // v0: neighbors{1,2} mean=0 → Lerp((0,3,0),0,0.5)=(0,1.5,0)
        // v1: neighbors{0,2} mean=(0,1.5,0) → Lerp(0,mean,0.5)=(0,0.75,0)  (Jacobi: 前 iter 値参照)
        // v2: v1 と対称 → (0,0.75,0)
        AssertV3(new Vector3(0, 1.5f, 0), disp[0]);
        AssertV3(new Vector3(0, 0.75f, 0), disp[1]);
        AssertV3(new Vector3(0, 0.75f, 0), disp[2]);
    }

    [Fact]
    public void SmoothInPlace_ConvexCombination_NeverExceedsInputRange()
    {
        // 凸結合 (近傍平均への lerp) なので出力は入力範囲 [0,5] を超えない = トゲを生成しない。
        var disp = new[] { new Vector3(0, 5, 0), Vector3.zero, Vector3.zero, new Vector3(0, 1, 0) };
        var tris = new[] { 0, 1, 2, 0, 2, 3 };

        MeshDisplacementSmoother.SmoothInPlace(disp, tris, 4, 5, 1.0f);

        foreach (var d in disp)
        {
            Assert.True(d.y >= 0f - 1e-4f, $"y={d.y} below input min 0");
            Assert.True(d.y <= 5f + 1e-4f, $"y={d.y} above input max 5");
        }
    }

    [Fact]
    public void SmoothInPlace_ConstantField_Unchanged_BothParities()
    {
        // 定数場は近傍平均=自分 → Lerp 不変。偶奇どちらの iterations でも copy-back が正しければ不変。
        var tris = new[] { 0, 1, 2 };
        var c = new Vector3(1, 2, 3);

        var even = new[] { c, c, c };
        MeshDisplacementSmoother.SmoothInPlace(even, tris, 3, 2, 0.7f);
        foreach (var d in even) AssertV3(c, d);

        var odd = new[] { c, c, c };
        MeshDisplacementSmoother.SmoothInPlace(odd, tris, 3, 3, 0.7f);
        foreach (var d in odd) AssertV3(c, d);
    }

    [Fact]
    public void SmoothInPlace_EvenIterations_CopyBackApplies()
    {
        // 偶数回反復では swap により src==disp に戻り Array.Copy が skip される分岐。
        // それでも結果が disp に反映され、かつ奇数回(1)より平滑が進む(spike がさらに縮む)ことを確認。
        var spike = new Vector3(0, 4, 0);
        var tris = new[] { 0, 1, 2 };

        var one = new[] { spike, Vector3.zero, Vector3.zero };
        MeshDisplacementSmoother.SmoothInPlace(one, tris, 3, 1, 0.5f);

        var two = new[] { spike, Vector3.zero, Vector3.zero };
        MeshDisplacementSmoother.SmoothInPlace(two, tris, 3, 2, 0.5f);

        // 偶数回でも spike は元値 4 から縮小し、奇数回1 より小さい (= disp へ反映され swap も正しい)。
        Assert.True(two[0].y < one[0].y, $"iter2 y={two[0].y} should be < iter1 y={one[0].y}");
        Assert.True(two[0].y < spike.y, "iter2 should reduce spike from original");
    }

    [Fact]
    public void SmoothInPlace_IsolatedVertex_LeftUnchanged()
    {
        // 頂点3 はどの triangle にも属さない → 隣接0 → 据え置き。
        var iso = new Vector3(9, 9, 9);
        var disp = new[] { new Vector3(0, 2, 0), Vector3.zero, Vector3.zero, iso };
        var tris = new[] { 0, 1, 2 };

        MeshDisplacementSmoother.SmoothInPlace(disp, tris, 4, 3, 0.8f);

        AssertV3(iso, disp[3]);
    }

    // OOB index は a/b/c (triangles[t]/[t+1]/[t+2]) のどの位置でも tri 全体を skip する (実装 L41)。
    // 各位置を Theory で網羅。不正 tri の有効頂点 {3,0} を使い、頂点3 は有効 tri に属さない孤立頂点に置く。
    [Theory]
    [InlineData(99, 3, 0)]   // OOB が a 位置
    [InlineData(3, 99, 0)]   // OOB が b 位置
    [InlineData(3, 0, 99)]   // OOB が c 位置
    public void SmoothInPlace_OutOfRangeTriangleIndex_SkipsWholeTriWithoutFalseAdjacency(int b0, int b1, int b2)
    {
        // 有効 tri {0,1,2} + 不正 tri (99 を含む)。不正 tri は丸ごと skip されるべき。
        // 頂点3 は有効 tri に属さず、不正 tri にのみ {3,0} として登場する。
        // もし skip が誤って 3↔0 辺を追加すると頂点3 は頂点0 の disp 方向へ動く。
        // → 頂点3 が spike 値 (0,9,0) のまま不変なら「誤隣接が無い」ことを実証できる (孤立=据え置き)。
        var disp = new[] { new Vector3(0, 3, 0), Vector3.zero, Vector3.zero, new Vector3(0, 9, 0) };
        var tris = new[] { 0, 1, 2, b0, b1, b2 };
        MeshDisplacementSmoother.SmoothInPlace(disp, tris, 4, 2, 0.5f);

        // 頂点3: 不正 tri が skip され隣接ゼロ → 据え置き (誤隣接が無いことの証明)。
        AssertV3(new Vector3(0, 9, 0), disp[3]);

        // 有効 tri {0,1,2} は通常どおり平滑化され、tris={0,1,2} 単独と一致するべき。
        var clean = new[] { new Vector3(0, 3, 0), Vector3.zero, Vector3.zero };
        MeshDisplacementSmoother.SmoothInPlace(clean, new[] { 0, 1, 2 }, 3, 2, 0.5f);
        for (int i = 0; i < 3; i++) AssertV3(clean[i], disp[i]);
    }

    // ---- ガード群 (すべて no-op) ----

    [Fact]
    public void SmoothInPlace_NullDisp_NoThrow()
    {
        MeshDisplacementSmoother.SmoothInPlace(null, new[] { 0, 1, 2 }, 3, 1, 0.5f);
    }

    [Fact]
    public void SmoothInPlace_LengthMismatch_NoOp()
    {
        var disp = new[] { new Vector3(1, 1, 1), new Vector3(2, 2, 2), new Vector3(3, 3, 3) };
        MeshDisplacementSmoother.SmoothInPlace(disp, new[] { 0, 1, 2 }, 4, 1, 0.5f); // vertexCount≠Length
        AssertV3(new Vector3(1, 1, 1), disp[0]);
        AssertV3(new Vector3(2, 2, 2), disp[1]);
        AssertV3(new Vector3(3, 3, 3), disp[2]);
    }

    [Fact]
    public void SmoothInPlace_NullOrTooShortTriangles_NoOp()
    {
        var orig = new Vector3(0, 3, 0);

        var d1 = new[] { orig, Vector3.zero, Vector3.zero };
        MeshDisplacementSmoother.SmoothInPlace(d1, null, 3, 1, 0.5f);
        AssertV3(orig, d1[0]);

        var d2 = new[] { orig, Vector3.zero, Vector3.zero };
        MeshDisplacementSmoother.SmoothInPlace(d2, new[] { 0, 1 }, 3, 1, 0.5f); // length < 3
        AssertV3(orig, d2[0]);
    }

    [Fact]
    public void SmoothInPlace_NonPositiveIterations_NoOp()
    {
        var orig = new Vector3(0, 3, 0);
        var disp = new[] { orig, Vector3.zero, Vector3.zero };

        MeshDisplacementSmoother.SmoothInPlace(disp, new[] { 0, 1, 2 }, 3, 0, 0.5f);

        AssertV3(orig, disp[0]);
    }

    [Fact]
    public void SmoothInPlace_NonPositiveStrength_NoOp()
    {
        var orig = new Vector3(0, 3, 0);
        var disp = new[] { orig, Vector3.zero, Vector3.zero };

        MeshDisplacementSmoother.SmoothInPlace(disp, new[] { 0, 1, 2 }, 3, 3, 0f);

        AssertV3(orig, disp[0]);
    }
}
