using UnityEngine;
using Xunit;
using BunnyGarden2FixMod.Patches.CostumeChanger;

// 純関数 BreastClothLowerFadeMath のテスト。Vector3 / BoneWeight は値型で headless 構築可。
public class BreastClothLowerFadeMathTests
{
    private static BoneWeight Bw(int idx0) => new BoneWeight { boneIndex0 = idx0, weight0 = 1f };

    // ---- ComputeFadeTopY ----

    [Fact]
    public void ComputeFadeTopY_ReturnsMaxY()
    {
        var v = new[] { new Vector3(0, -0.2f, 0), new Vector3(1, 0.05f, 0), new Vector3(0, -0.1f, 1) };
        Assert.Equal(0.05f, BreastClothLowerFadeMath.ComputeFadeTopY(v), 5);
    }

    [Fact]
    public void ComputeFadeTopY_NullOrEmpty_ReturnsNaN()
    {
        Assert.True(float.IsNaN(BreastClothLowerFadeMath.ComputeFadeTopY(null)));
        Assert.True(float.IsNaN(BreastClothLowerFadeMath.ComputeFadeTopY(new Vector3[0])));
    }

    // ---- ApplyLowerFade ----

    [Fact]
    public void ApplyLowerFade_AboveTop_KeepsPreserved()
    {
        // yTop=0, width=0.05 → y=0.5 は十分上 → f=1 → preserved 維持
        var basePv = new[] { new Vector3(0, 0.5f, 0) };
        var preservedPv = new[] { new Vector3(9, 9, 9) };
        int faded = BreastClothLowerFadeMath.ApplyLowerFade(basePv, preservedPv, null, null, 0f, 0.05f);
        Assert.Equal(0, faded);                       // f<1 の頂点は無し
        Assert.Equal(new Vector3(9, 9, 9), preservedPv[0]);
    }

    [Fact]
    public void ApplyLowerFade_BelowBand_RestoresBase()
    {
        // yTop=0, width=0.05 → 帯下端 = -0.05。y=-0.5 は帯下 → f=0 → base へ復帰
        var basePv = new[] { new Vector3(1, -0.5f, 2) };
        var preservedPv = new[] { new Vector3(9, 9, 9) };
        int faded = BreastClothLowerFadeMath.ApplyLowerFade(basePv, preservedPv, null, null, 0f, 0.05f);
        Assert.Equal(1, faded);
        Assert.Equal(new Vector3(1, -0.5f, 2), preservedPv[0]);
    }

    [Fact]
    public void ApplyLowerFade_WidthZero_IsBinaryAtTop()
    {
        // width=0 → y>=yTop は preserved、未満は base
        var basePv = new[] { new Vector3(0, 0.01f, 0), new Vector3(0, -0.01f, 0) };
        var preservedPv = new[] { new Vector3(5, 5, 5), new Vector3(7, 7, 7) };
        int faded = BreastClothLowerFadeMath.ApplyLowerFade(basePv, preservedPv, null, null, 0f, 0f);
        Assert.Equal(1, faded);
        Assert.Equal(new Vector3(5, 5, 5), preservedPv[0]);          // y=0.01 >= 0 → preserved
        Assert.Equal(new Vector3(0, -0.01f, 0), preservedPv[1]);     // y=-0.01 < 0 → base
    }

    [Fact]
    public void ApplyLowerFade_InsideBand_IsMonotonic()
    {
        // 帯内 (yTop=0, width=0.1, 帯 [-0.1, 0]) で base→preserved が単調に増える
        var ys = new[] { -0.1f, -0.075f, -0.05f, -0.025f, 0f };
        float prev = -1f;
        foreach (var y in ys)
        {
            var basePv = new[] { new Vector3(0, y, 0) };
            var preservedPv = new[] { new Vector3(0, y + 1f, 0) };   // preserved は base から +1 ずれている
            BreastClothLowerFadeMath.ApplyLowerFade(basePv, preservedPv, null, null, 0f, 0.1f);
            float f = preservedPv[0].y - y;                          // 実効ブレンド係数 (0=base, 1=preserved)
            Assert.True(f >= prev - 1e-5f, $"f not monotonic at y={y}: {f} < {prev}");
            Assert.InRange(f, 0f, 1f);
            prev = f;
        }
    }

    [Fact]
    public void ApplyLowerFade_BoneWeight_BinaryAtHalf()
    {
        // f<0.5 → baseBw、f>=0.5 → preservedBw。yTop=0 width=0.1, 帯 [-0.1,0]。
        // y=-0.08 → t=(y-(-0.1))/0.1=0.2 → f=smoothstep(0.2)=0.104 <0.5 → baseBw
        // y=-0.02 → t=0.8 → f=smoothstep(0.8)=0.896 >=0.5 → preservedBw
        var basePv = new[] { new Vector3(0, -0.08f, 0), new Vector3(0, -0.02f, 0) };
        var preservedPv = new[] { new Vector3(0, 0.5f, 0), new Vector3(0, 0.5f, 0) };
        var baseBw = new[] { Bw(1), Bw(1) };
        var preservedBw = new[] { Bw(2), Bw(2) };
        BreastClothLowerFadeMath.ApplyLowerFade(basePv, preservedPv, baseBw, preservedBw, 0f, 0.1f);
        Assert.Equal(1, preservedBw[0].boneIndex0);   // f<0.5 → baseBw に置換
        Assert.Equal(2, preservedBw[1].boneIndex0);   // f>=0.5 → preservedBw 維持
    }

    [Fact]
    public void ApplyLowerFade_NaNTop_ReturnsMinusOneAndNoMutation()
    {
        var basePv = new[] { new Vector3(0, -1f, 0) };
        var preservedPv = new[] { new Vector3(9, 9, 9) };
        int faded = BreastClothLowerFadeMath.ApplyLowerFade(basePv, preservedPv, null, null, float.NaN, 0.05f);
        Assert.Equal(-1, faded);
        Assert.Equal(new Vector3(9, 9, 9), preservedPv[0]);   // 不変
    }

    [Fact]
    public void ApplyLowerFade_LengthMismatch_ReturnsMinusOne()
    {
        var basePv = new[] { new Vector3(0, -1f, 0) };
        var preservedPv = new[] { new Vector3(9, 9, 9), new Vector3(8, 8, 8) };
        int faded = BreastClothLowerFadeMath.ApplyLowerFade(basePv, preservedPv, null, null, 0f, 0.05f);
        Assert.Equal(-1, faded);
    }
}
