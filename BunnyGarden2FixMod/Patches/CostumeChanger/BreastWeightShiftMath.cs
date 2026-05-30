using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// breast1 系 boneWeight を親骨 (Spine3_skinJT) へ flatten amount 比例で再配分する純算術 helper。
/// <see cref="BreastClothWeightShifter"/> (cloth = mesh_costume*) と <see cref="BreastFlattenApplier"/>
/// (skin = mesh_skin_upper) の両方から呼ばれ、cloth と skin の weight 再配分を構造的に一致させることで
/// flatten 時の motion 一致 (突き抜け [[flatten-bone-rotation-asymmetry]] 解消) を保証する。
///
/// 数学的性質: Linear skinning では weight 平行移動 (breast → parent) が bind pose 頂点位置を変えないため、
/// 静止 frame では視覚差は無い。runtime motion (breast bone の物理駆動) への追従のみ抑制される。
/// </summary>
internal static class BreastWeightShiftMath
{
    /// <summary>breast weight (wR+wL) がこの閾値未満の vert は再配分対象外。</summary>
    public const float BreastWeightThreshold = 0.05f;
    private const float MinSumForNormalize = 0.01f;

    /// <summary>
    /// breast1 の親骨 index を bones[] から解決。diag 実測で 6 character 全部 Spine3_skinJT が親と確定済
    /// (docs/costume-system.md "mesh_costume の breast 骨構造")。Spine2 / Spine1 は防衛 fallback。全欠如で -1。
    /// </summary>
    public static int ResolveParentBoneIndex(Transform[] bones)
    {
        int idx = FindBoneByName(bones, "Spine3_skinJT");
        if (idx >= 0) return idx;
        idx = FindBoneByName(bones, "Spine2_skinJT");
        if (idx >= 0) return idx;
        return FindBoneByName(bones, "Spine1_skinJT");
    }

    /// <summary>BoneWeight (Unity の 4 ボーンまでの skin weight) から指定 bone index の weight を抽出 (不在で 0)。</summary>
    public static float GetWeightForBone(BoneWeight entry, int boneIndex)
    {
        if (entry.boneIndex0 == boneIndex) return entry.weight0;
        if (entry.boneIndex1 == boneIndex) return entry.weight1;
        if (entry.boneIndex2 == boneIndex) return entry.weight2;
        if (entry.boneIndex3 == boneIndex) return entry.weight3;
        return 0f;
    }

    /// <summary>
    /// weight 移送量を flatten 量 × 倍率で解決する純関数。範囲外入力に備え [0,1] へ clamp する
    /// (amount&gt;1 だと <see cref="RedistributeWeight"/> で breast slot が負になり破綻するため)。
    /// flatten 量 0 なら 0（= 移送なし）。
    /// </summary>
    public static float ComputeWeightShiftAmount(float flattenAmount, float multiplier)
        => Mathf.Clamp01(flattenAmount * multiplier);

    /// <summary>
    /// breast slot weight の <paramref name="amount"/> 割合を parent slot へ移送する
    /// (slot は (1-amount) 倍が残る。amount=1 で全移送 / amount=0 で無変化)。
    /// case 分岐: A (parent 既存) / B (空き slot あり) / C (非 breast 最小 slot 置換) / D 病態 (4 slot 全 breast)。
    /// 数学的に sum=1 が保たれる weight 平行移動 (move、scale ではない)。
    /// </summary>
    public static BoneWeight RedistributeWeight(BoneWeight bw, int rIdx, int lIdx, int parentIdx, float amount)
    {
        int[] idx = { bw.boneIndex0, bw.boneIndex1, bw.boneIndex2, bw.boneIndex3 };
        float[] w = { bw.weight0, bw.weight1, bw.weight2, bw.weight3 };

        // Step 1: breast slot を amount 倍に減衰、削減分を集計
        float breastShifted = 0f;
        for (int s = 0; s < 4; s++)
        {
            if ((rIdx >= 0 && idx[s] == rIdx) || (lIdx >= 0 && idx[s] == lIdx))
            {
                float removed = w[s] * amount;
                w[s] -= removed;
                breastShifted += removed;
            }
        }

        // Step 2: parent slot 確保
        int parentSlot = -1;
        for (int s = 0; s < 4; s++)
        {
            if (idx[s] == parentIdx) { parentSlot = s; break; }
        }
        if (parentSlot >= 0)
        {
            // case A: parent 既存
            w[parentSlot] += breastShifted;
        }
        else
        {
            // case B: weight 0 (空き) slot を探す
            int emptySlot = -1;
            for (int s = 0; s < 4; s++)
            {
                if (w[s] <= 0f) { emptySlot = s; break; }
            }
            if (emptySlot >= 0)
            {
                idx[emptySlot] = parentIdx;
                w[emptySlot] = breastShifted;
            }
            else
            {
                // case C: 非 breast 最小 slot を置換 (犠牲 weight は新 parent に吸収)
                int minSlot = -1;
                float minW = float.MaxValue;
                for (int s = 0; s < 4; s++)
                {
                    bool isBreast = (rIdx >= 0 && idx[s] == rIdx) || (lIdx >= 0 && idx[s] == lIdx);
                    if (isBreast) continue;
                    if (w[s] < minW) { minW = w[s]; minSlot = s; }
                }
                if (minSlot >= 0)
                {
                    idx[minSlot] = parentIdx;
                    w[minSlot] = breastShifted + minW;
                }
                else
                {
                    // case D 病態: 4 slot 全 breast → 全 slot を parentIdx に統一して 1.0 集約。
                    // 実際には R/L_breast1 の 2 bone しかないため max 2 slot しか breast にならず、
                    // 本 case は理論的に到達不能。万一到達した場合の防衛で、Step 3 正規化が
                    // breast 残量を再膨張させる経路を断つため正規化前に直接 BoneWeight を返す。
                    return new BoneWeight
                    {
                        boneIndex0 = parentIdx, weight0 = 1f,
                        boneIndex1 = 0, weight1 = 0f,
                        boneIndex2 = 0, weight2 = 0f,
                        boneIndex3 = 0, weight3 = 0f,
                    };
                }
            }
        }

        // Step 3: 数値誤差防衛 — sum 検査 + 正規化
        // case A/B/C はすべて weight 平行移動 (sum 不変) なので理論 sum=1。
        // case D は早期 return 済で本経路に到達しない
        float sum = w[0] + w[1] + w[2] + w[3];
        if (sum < MinSumForNormalize)
        {
            // フェイルセーフ: slot[0] に parent 1.0 集約
            return new BoneWeight
            {
                boneIndex0 = parentIdx, weight0 = 1f,
                boneIndex1 = 0, weight1 = 0f,
                boneIndex2 = 0, weight2 = 0f,
                boneIndex3 = 0, weight3 = 0f,
            };
        }
        if (!Mathf.Approximately(sum, 1f))
        {
            float inv = 1f / sum;
            for (int s = 0; s < 4; s++) w[s] *= inv;
        }

        // Step 4: weight 降順 sort (Unity の slot 順位を維持)
        SortSlotsDescending(idx, w);

        return new BoneWeight
        {
            boneIndex0 = idx[0], weight0 = w[0],
            boneIndex1 = idx[1], weight1 = w[1],
            boneIndex2 = idx[2], weight2 = w[2],
            boneIndex3 = idx[3], weight3 = w[3],
        };
    }

    private static int FindBoneByName(Transform[] bones, string name)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null && bones[i].name == name) return i;
        }
        return -1;
    }

    private static void SortSlotsDescending(int[] idx, float[] w)
    {
        // 4 要素 insertion sort (allocation 0)
        for (int i = 1; i < 4; i++)
        {
            int curIdx = idx[i];
            float curW = w[i];
            int j = i - 1;
            while (j >= 0 && w[j] < curW)
            {
                idx[j + 1] = idx[j];
                w[j + 1] = w[j];
                j--;
            }
            idx[j + 1] = curIdx;
            w[j + 1] = curW;
        }
    }
}
