using UnityEngine;
using Xunit;
using BunnyGarden2FixMod.Patches.CostumeChanger;

// 純関数 BreastWeightShiftMath のテスト。BoneWeight は値型で headless 構築可。
// Transform 依存の ResolveParentBoneIndex/FindBoneByName は GameObject 生成が要るため対象外
// (既存 SkinUpperWeightConformerTests も bone は名前/index で渡す同方針)。
public class BreastWeightShiftMathTests
{
    // 慣例: rIdx=1 (R_breast1), lIdx=2 (L_breast1), parentIdx=10 (Spine3_skinJT 相当)。
    private const int R = 1, L = 2, P = 10;

    private static BoneWeight BW(int i0, float w0, int i1, float w1, int i2, float w2, int i3, float w3)
        => new BoneWeight
        {
            boneIndex0 = i0, weight0 = w0,
            boneIndex1 = i1, weight1 = w1,
            boneIndex2 = i2, weight2 = w2,
            boneIndex3 = i3, weight3 = w3,
        };

    private static float Sum(BoneWeight b) => b.weight0 + b.weight1 + b.weight2 + b.weight3;

    // ---- RedistributeWeight ----

    [Fact]
    public void Redistribute_AmountZero_ParentPresent_IsIdentity()
    {
        // parent が既存 slot (case A) かつ降順整列済なら amount=0 は完全恒等。
        // (parent 不在だと Step2 で空き slot 挿入/最小 slot 置換が走り idx 並びが変わるため、
        //  恒等を主張するには parent 既存入力に固定する必要がある。)
        var bw = BW(P, 0.5f, R, 0.3f, L, 0.2f, 0, 0f);

        var r = BreastWeightShiftMath.RedistributeWeight(bw, R, L, P, 0f);

        Assert.Equal(P, r.boneIndex0); Assert.Equal(0.5f, r.weight0, 5);
        Assert.Equal(R, r.boneIndex1); Assert.Equal(0.3f, r.weight1, 5);
        Assert.Equal(L, r.boneIndex2); Assert.Equal(0.2f, r.weight2, 5);
        Assert.Equal(0f, r.weight3, 5);
    }

    [Fact]
    public void Redistribute_AmountOne_MovesAllBreastWeightToParent()
    {
        var bw = BW(P, 0.5f, R, 0.3f, L, 0.2f, 0, 0f);

        var r = BreastWeightShiftMath.RedistributeWeight(bw, R, L, P, 1f);

        // breast 全移送 → breast weight 0, parent 1.0。
        Assert.Equal(0f, BreastWeightShiftMath.GetWeightForBone(r, R), 5);
        Assert.Equal(0f, BreastWeightShiftMath.GetWeightForBone(r, L), 5);
        Assert.Equal(1f, BreastWeightShiftMath.GetWeightForBone(r, P), 5);
        Assert.Equal(1f, Sum(r), 5);
    }

    [Fact]
    public void Redistribute_CaseA_ParentExisting_Accumulates()
    {
        var bw = BW(P, 0.4f, R, 0.4f, L, 0.2f, 0, 0f);

        var r = BreastWeightShiftMath.RedistributeWeight(bw, R, L, P, 0.5f);

        // removed: R 0.2 + L 0.1 = 0.3 → parent 0.4+0.3=0.7。breast は (1-0.5) 倍残。
        Assert.Equal(0.7f, BreastWeightShiftMath.GetWeightForBone(r, P), 5);
        Assert.Equal(0.2f, BreastWeightShiftMath.GetWeightForBone(r, R), 5);
        Assert.Equal(0.1f, BreastWeightShiftMath.GetWeightForBone(r, L), 5);
        Assert.Equal(1f, Sum(r), 5);
    }

    [Fact]
    public void Redistribute_CaseB_InsertsParentIntoEmptySlot()
    {
        // parent 不在 + 空き slot (w=0) あり。
        var bw = BW(R, 0.5f, L, 0.3f, 5, 0.2f, 0, 0f);

        var r = BreastWeightShiftMath.RedistributeWeight(bw, R, L, P, 0.5f);

        // removed: R 0.25 + L 0.15 = 0.4 → 空き slot に parentIdx=0.4 挿入。
        Assert.Equal(0.4f, BreastWeightShiftMath.GetWeightForBone(r, P), 5);
        Assert.Equal(0.25f, BreastWeightShiftMath.GetWeightForBone(r, R), 5);
        Assert.Equal(0.15f, BreastWeightShiftMath.GetWeightForBone(r, L), 5);
        Assert.Equal(0.2f, BreastWeightShiftMath.GetWeightForBone(r, 5), 5);
        Assert.Equal(1f, Sum(r), 5);
    }

    [Fact]
    public void Redistribute_CaseC_ReplacesSmallestNonBreastSlot()
    {
        // parent 不在 + 空き slot 無し (4 slot 全 fill) + breast slot は amount 後も >0 → case C。
        // (amount=1 だと breast が 0 化して空き slot 扱いになり case B を踏むため amount<1 が必須。)
        var bw = BW(R, 0.4f, L, 0.3f, 5, 0.2f, 6, 0.1f);

        var r = BreastWeightShiftMath.RedistributeWeight(bw, R, L, P, 0.5f);

        // removed: R 0.2 + L 0.15 = 0.35。非 breast 最小 slot = idx6(0.1) を parentIdx に置換。
        // 置換 slot weight = breastShifted + minW = 0.35 + 0.1 = 0.45。
        Assert.Equal(0.45f, BreastWeightShiftMath.GetWeightForBone(r, P), 5);
        Assert.Equal(0f, BreastWeightShiftMath.GetWeightForBone(r, 6), 5);   // 犠牲 slot は消える
        Assert.Equal(0.2f, BreastWeightShiftMath.GetWeightForBone(r, R), 5);
        Assert.Equal(0.15f, BreastWeightShiftMath.GetWeightForBone(r, L), 5);
        Assert.Equal(0.2f, BreastWeightShiftMath.GetWeightForBone(r, 5), 5);
        Assert.Equal(1f, Sum(r), 5);
    }

    [Fact]
    public void Redistribute_CaseD_AllBreastSlots_CollapsesToParent()
    {
        // 4 slot 全 breast + parent 不在 + 空き slot 無し → 病態 case D の防衛経路。
        // (amount<1 で breast slot が 0 化せず空き slot 判定を回避し、非 breast slot も無いため
        //  最小 slot 置換が成立せず case D に到達する。)
        // 注: 同一 boneIndex を複数 slot に重複させた退化入力は実機では生成され得ない。
        // R/L の 2 bone しか breast に無いため通常は最大 2 slot のみ breast = case D は理論到達不能。
        // ここでは重複 index で防衛経路を意図的に踏ませ、早期 return の正当性を確認する。
        var bw = BW(R, 0.25f, L, 0.25f, R, 0.25f, L, 0.25f);

        var r = BreastWeightShiftMath.RedistributeWeight(bw, R, L, P, 0.5f);

        // parentIdx に 1.0 集約して早期 return。
        Assert.Equal(P, r.boneIndex0);
        Assert.Equal(1f, r.weight0, 5);
        Assert.Equal(0f, r.weight1, 5);
        Assert.Equal(0f, r.weight2, 5);
        Assert.Equal(0f, r.weight3, 5);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1.0f)]
    public void Redistribute_PreservesSumAndDescendingOrder(float amount)
    {
        // 代表入力 × amount で、不変条件 sum≈1 + weight 降順を検証 (踏む case は amount により変動)。
        // 例: 3 番目の入力は amount<1 で case C、amount=1 では breast 0 化で case B に落ちる。
        var inputs = new[]
        {
            BW(P, 0.4f, R, 0.4f, L, 0.2f, 0, 0f),    // parent 既存 (低 amount で case A)
            BW(R, 0.5f, L, 0.3f, 5, 0.2f, 0, 0f),    // 空き slot あり (case B)
            BW(R, 0.4f, L, 0.3f, 5, 0.2f, 6, 0.1f),  // 4 slot 全 fill (低 amount で case C)
        };

        foreach (var bw in inputs)
        {
            var r = BreastWeightShiftMath.RedistributeWeight(bw, R, L, P, amount);

            Assert.Equal(1f, Sum(r), 5);   // weight 平行移動なので sum 保存
            Assert.True(r.weight0 >= r.weight1, "weight0 >= weight1");
            Assert.True(r.weight1 >= r.weight2, "weight1 >= weight2");
            Assert.True(r.weight2 >= r.weight3, "weight2 >= weight3");
        }
    }

    // ---- GetWeightForBone ----

    [Fact]
    public void GetWeightForBone_ExtractsEachSlot()
    {
        var bw = BW(3, 0.4f, 7, 0.3f, 8, 0.2f, 9, 0.1f);

        Assert.Equal(0.4f, BreastWeightShiftMath.GetWeightForBone(bw, 3), 5);
        Assert.Equal(0.3f, BreastWeightShiftMath.GetWeightForBone(bw, 7), 5);
        Assert.Equal(0.2f, BreastWeightShiftMath.GetWeightForBone(bw, 8), 5);
        Assert.Equal(0.1f, BreastWeightShiftMath.GetWeightForBone(bw, 9), 5);
    }

    [Fact]
    public void GetWeightForBone_AbsentBone_ReturnsZero()
    {
        var bw = BW(3, 0.5f, 7, 0.5f, 0, 0f, 0, 0f);

        Assert.Equal(0f, BreastWeightShiftMath.GetWeightForBone(bw, 99), 5);
    }

    [Fact]
    public void GetWeightForBone_DuplicateIndex_ReturnsFirstSlot()
    {
        // 同一 boneIndex が複数 slot にある場合は最初の slot を返す (早期 return)。
        var bw = BW(5, 0.6f, 5, 0.4f, 0, 0f, 0, 0f);

        Assert.Equal(0.6f, BreastWeightShiftMath.GetWeightForBone(bw, 5), 5);
    }

    // ---- ComputeWeightShiftAmount ----

    [Fact]
    public void ComputeWeightShiftAmount_MultiplierOne_IsIdentity()
    {
        // 倍率 1.0 = 現状（flatten 量そのまま）。回帰ゼロ保証。
        Assert.Equal(0.5f, BreastWeightShiftMath.ComputeWeightShiftAmount(0.5f, 1.0f), 5);
    }

    [Fact]
    public void ComputeWeightShiftAmount_MultiplierZero_IsZero()
    {
        // 倍率 0 = 移送なし（揺れ最大）。
        Assert.Equal(0f, BreastWeightShiftMath.ComputeWeightShiftAmount(0.8f, 0f), 5);
    }

    [Fact]
    public void ComputeWeightShiftAmount_FlattenZero_IsZero()
    {
        // flatten=0 なら倍率に依らず 0（= 移送なし）。
        Assert.Equal(0f, BreastWeightShiftMath.ComputeWeightShiftAmount(0f, 1.0f), 5);
    }

    [Fact]
    public void ComputeWeightShiftAmount_Partial_Multiplies()
    {
        // 0.6 × 0.5 = 0.3。
        Assert.Equal(0.3f, BreastWeightShiftMath.ComputeWeightShiftAmount(0.6f, 0.5f), 5);
    }

    [Fact]
    public void ComputeWeightShiftAmount_OverOne_ClampsToOne()
    {
        // 範囲外入力は [0,1] へ clamp（RedistributeWeight の amount>1 破綻を防ぐ防御）。
        Assert.Equal(1f, BreastWeightShiftMath.ComputeWeightShiftAmount(1.0f, 2.0f), 5);
    }

    [Fact]
    public void ComputeWeightShiftAmount_NegativeMultiplier_ClampsToZero()
    {
        // 負値（config range[0,1] では来ないが、ResolveShiftMultiplier は clamp しない設計なので
        // 純関数の下限契約として 0 へ clamp されることを保証）。
        Assert.Equal(0f, BreastWeightShiftMath.ComputeWeightShiftAmount(0.5f, -1f), 5);
    }
}
