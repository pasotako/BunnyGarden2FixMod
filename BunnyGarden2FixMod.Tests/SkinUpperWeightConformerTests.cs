using UnityEngine;
using Xunit;
using BunnyGarden2FixMod.Patches.CostumeChanger;

// 純関数 SkinUpperWeightConformer のテスト。BoneWeight/Vector3 は値型で headless 構築可。
// SpatialGridIndex を内部利用するため点群は query 近傍に密に置く（MaxRadius 走査ハング回避）。
public class SkinUpperWeightConformerTests
{
    private const float SeamDist = 0.005f, NativeMatch = 0.03f, XHalf = 0.25f;

    private static BoneWeight BW(int i0, float w0, int i1 = 0, float w1 = 0)
        => new BoneWeight { boneIndex0 = i0, weight0 = w0, boneIndex1 = i1, weight1 = w1 };

    // 継ぎ目近傍に密な baby-lower クラスタ（referenceY≈1.101, 各頂点 |x|<XHalf）。
    private static Vector3[] LowerNearSeam() => new[]
    {
        new Vector3(0.000f, 1.100f, 0.000f), new Vector3(0.004f, 1.099f, 0.001f),
        new Vector3(-0.003f, 1.101f, -0.002f), new Vector3(0.002f, 1.100f, 0.003f),
    };

    // 継ぎ目近傍に密な native_upper クラスタ（Spine1 0.9 寄り / Hip）。
    private static Vector3[] NativeNearSeam() => new[]
    {
        new Vector3(0.005f, 1.100f, 0f), new Vector3(0.02f, 1.100f, 0f),
        new Vector3(0.005f, 1.090f, 0f), new Vector3(0.02f, 1.110f, 0f),
    };
    private static BoneWeight[] NativeSeamWeights() => new[]
    {
        BW(0, 0.9f, 1, 0.1f), BW(0, 0.85f, 1, 0.15f), BW(0, 0.88f, 1, 0.12f), BW(0, 0.9f, 1, 0.1f),
    };

    [Fact]
    public void Reencode_MapsNativeWeightToBabyIndexByName()
    {
        // native idx: 0=Spine1, 1=Hip。baby idx: 0=Hip, 1=Spine1（順序違い）。
        var nativeNames = new[] { "Spine1", "Hip" };
        var babyNameToIdx = new System.Collections.Generic.Dictionary<string, int> { ["Hip"] = 0, ["Spine1"] = 1 };
        var nat = BW(0, 0.7f, 1, 0.3f);   // Spine1 0.7, Hip 0.3

        var r = SkinUpperWeightConformer.Reencode(nat, nativeNames, babyNameToIdx);

        // baby 空間で Spine1=idx1=0.7, Hip=idx0=0.3。top が Spine1(0.7)=idx1。
        Assert.Equal(1, r.boneIndex0);
        Assert.Equal(0.7f, r.weight0, 3);
        Assert.Equal(0, r.boneIndex1);
        Assert.Equal(0.3f, r.weight1, 3);
    }

    [Fact]
    public void TryReencode_NoMatch_ReturnsFalse()
    {
        var nativeNames = new[] { "Foo", "Bar" };
        var babyNameToIdx = new System.Collections.Generic.Dictionary<string, int> { ["Spine1"] = 0 };
        bool ok = SkinUpperWeightConformer.TryReencode(BW(0, 0.7f, 1, 0.3f), nativeNames, babyNameToIdx, out _);
        Assert.False(ok);
    }

    [Fact]
    public void ConformInPlace_SeamVert_NearLower_ReplacedWithNative()
    {
        // baby-lower 5mm 以内・yCutoff 以下の継ぎ目頂点 → native(Spine1 0.9/Hip 0.1) に全置換。
        var verts = new[] { new Vector3(0f, 1.100f, 0f) };
        var weights = new[] { BW(0, 0.5f, 1, 0.5f) };
        var babyNames = new[] { "Spine1", "Hip" };
        var nNames = new[] { "Spine1", "Hip" };

        int n = SkinUpperWeightConformer.ConformInPlace(
            verts, weights, babyNames,
            NativeNearSeam(), NativeSeamWeights(), nNames,
            LowerNearSeam(), SeamDist, NativeMatch, XHalf);

        Assert.Equal(1, n);
        Assert.Equal(0, weights[0].boneIndex0);     // Spine1 = baby idx0
        Assert.Equal(0.9f, weights[0].weight0, 2);  // フェード無しの全置換
    }

    [Fact]
    public void ConformInPlace_BreastVert_AboveCutoff_Unchanged()
    {
        // Y=1.25 は yCutoff(≈1.106) 超 → prune → 不変（胸保護）。
        var verts = new[] { new Vector3(0f, 1.250f, 0f) };
        var weights = new[] { BW(0, 0.5f, 1, 0.5f) };
        var babyNames = new[] { "Spine1", "Hip" };
        var nNames = new[] { "Spine1", "Hip" };

        int n = SkinUpperWeightConformer.ConformInPlace(
            verts, weights, babyNames,
            NativeNearSeam(), NativeSeamWeights(), nNames,
            LowerNearSeam(), SeamDist, NativeMatch, XHalf);

        Assert.Equal(0, n);
        Assert.Equal(0.5f, weights[0].weight0, 3);
    }

    [Fact]
    public void ConformInPlace_YCutoffBoundary_PrunesAboveOnly()
    {
        // referenceY = max(baby-lower.y, |x|<XHalf) = 1.100 → yCutoff = 1.105。
        // v.y=1.104（cutoff 以下・lower 4mm 以内）→ 補正 / v.y=1.107（cutoff 超）→ prune。
        var verts = new[] { new Vector3(0f, 1.104f, 0f), new Vector3(0f, 1.107f, 0f) };
        var weights = new[] { BW(0, 0.5f, 1, 0.5f), BW(0, 0.5f, 1, 0.5f) };
        var babyNames = new[] { "Spine1", "Hip" };
        var nNames = new[] { "Spine1", "Hip" };
        var lower = new[]
        {
            new Vector3(0f, 1.100f, 0f), new Vector3(0.003f, 1.099f, 0.001f),
            new Vector3(-0.002f, 1.098f, 0f), new Vector3(0.001f, 1.100f, 0.002f),
        };

        int n = SkinUpperWeightConformer.ConformInPlace(
            verts, weights, babyNames,
            NativeNearSeam(), NativeSeamWeights(), nNames,
            lower, SeamDist, NativeMatch, XHalf);

        Assert.Equal(1, n);
        Assert.Equal(0.9f, weights[0].weight0, 2);   // 1.104 = cutoff 以下 → native 全置換
        Assert.Equal(0.5f, weights[1].weight0, 3);   // 1.107 = cutoff 超 → 不変
    }

    [Fact]
    public void ConformInPlace_BeyondSeamDist_Unchanged()
    {
        // yCutoff 以下だが baby-lower 最近傍が 5mm 超（z=0.02）→ ゲート外 → 不変。
        var verts = new[] { new Vector3(0f, 1.100f, 0f) };
        var weights = new[] { BW(0, 0.5f, 1, 0.5f) };
        var babyNames = new[] { "Spine1", "Hip" };
        var nNames = new[] { "Spine1", "Hip" };
        var lower = new[]
        {
            new Vector3(0f, 1.100f, 0.020f), new Vector3(0.004f, 1.100f, 0.022f),
            new Vector3(-0.003f, 1.100f, 0.021f), new Vector3(0.002f, 1.099f, 0.020f),
        };

        int n = SkinUpperWeightConformer.ConformInPlace(
            verts, weights, babyNames,
            NativeNearSeam(), NativeSeamWeights(), nNames,
            lower, SeamDist, NativeMatch, XHalf);

        Assert.Equal(0, n);
        Assert.Equal(0.5f, weights[0].weight0, 3);
    }

    [Fact]
    public void ConformInPlace_ArmVert_BeyondXHalf_Unchanged()
    {
        // |x|=0.45 ≥ XHalf → 腕として除外、不変（grid query 前に skip）。
        var verts = new[] { new Vector3(0.45f, 1.100f, 0f) };
        var weights = new[] { BW(0, 0.5f, 1, 0.5f) };
        var babyNames = new[] { "Spine1", "Hip" };
        var nNames = new[] { "Spine1", "Hip" };

        int n = SkinUpperWeightConformer.ConformInPlace(
            verts, weights, babyNames,
            NativeNearSeam(), NativeSeamWeights(), nNames,
            LowerNearSeam(), SeamDist, NativeMatch, XHalf);

        Assert.Equal(0, n);
        Assert.Equal(0.5f, weights[0].weight0, 3);
    }

    [Fact]
    public void ConformInPlace_NoBoneNameMatch_Skipped()
    {
        // verts は LowerNearSeam 近傍（ゲート通過）。だが native bone 名が baby に 1 つも無い →
        // TryReencode false → skip（baby weight を idx0 フルバインドへ崩さず保持）。
        var verts = new[] { new Vector3(0f, 1.100f, 0f) };
        var weights = new[] { BW(0, 0.5f, 1, 0.5f) };
        var babyNames = new[] { "Spine1", "Hip" };
        var nNames = new[] { "Unknown0", "Unknown1" };   // baby に存在しない

        int n = SkinUpperWeightConformer.ConformInPlace(
            verts, weights, babyNames,
            NativeNearSeam(), NativeSeamWeights(), nNames,
            LowerNearSeam(), SeamDist, NativeMatch, XHalf);

        Assert.Equal(0, n);
        Assert.Equal(0.5f, weights[0].weight0, 3);
        Assert.Equal(0.5f, weights[0].weight1, 3);
    }

    [Fact]
    public void ConformInPlace_OutlierNativeMatch_Skipped()
    {
        // baby-lower ゲートは通るが native_upper 最近傍が NativeMatch 超（x≈0.05）→ skip。
        var verts = new[] { new Vector3(0f, 1.100f, 0f) };
        var weights = new[] { BW(0, 0.5f, 1, 0.5f) };
        var babyNames = new[] { "Spine1", "Hip" };
        var nNames = new[] { "Spine1", "Hip" };
        var nVerts = new[]
        {
            new Vector3(0.05f, 1.100f, 0f), new Vector3(0.07f, 1.100f, 0f),
            new Vector3(0.05f, 1.090f, 0f), new Vector3(0.07f, 1.110f, 0f),
        };

        int n = SkinUpperWeightConformer.ConformInPlace(
            verts, weights, babyNames,
            nVerts, NativeSeamWeights(), nNames,
            LowerNearSeam(), SeamDist, NativeMatch, XHalf);

        Assert.Equal(0, n);
        Assert.Equal(0.5f, weights[0].weight0, 3);
    }

    [Fact]
    public void ConformInPlace_NullOrEmpty_NoOp()
    {
        var verts = new[] { new Vector3(0f, 1.100f, 0f) };
        var weights = new[] { BW(0, 0.5f, 1, 0.5f) };
        var babyNames = new[] { "Spine1", "Hip" };
        var nNames = new[] { "Spine1", "Hip" };

        // 空 lowerVerts → no-op。
        int n1 = SkinUpperWeightConformer.ConformInPlace(
            verts, weights, babyNames,
            NativeNearSeam(), NativeSeamWeights(), nNames,
            new Vector3[0], SeamDist, NativeMatch, XHalf);
        Assert.Equal(0, n1);

        // 空 nativeVerts → no-op。
        int n2 = SkinUpperWeightConformer.ConformInPlace(
            verts, weights, babyNames,
            new Vector3[0], new BoneWeight[0], nNames,
            LowerNearSeam(), SeamDist, NativeMatch, XHalf);
        Assert.Equal(0, n2);
    }
}
