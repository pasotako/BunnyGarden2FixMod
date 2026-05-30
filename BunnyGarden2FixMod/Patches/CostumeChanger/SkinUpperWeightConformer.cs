using System.Collections.Generic;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// swap した Babydoll mesh_skin_upper の腰継ぎ目頂点の boneWeight を native upper の weight へ戻す純関数。
/// swap で upper weight が Babydoll 順に変わり native lower との skinning が pose 依存で食い違う問題を、
/// 継ぎ目頂点のみ native weight へ全置換して解消する（胸域は触らない＝flatten 保護）。
///
/// 継ぎ目の localization は「target 自身の Babydoll mesh_skin_lower への近接 (seamDist 以内)」で行う。
/// upper clone と同一 Babydoll asset なので seam が構造的に coincident で、live lower の mask/push に
/// 影響されない。胸保護は baby-lower の最大 Y (+seamDist) より上を不変にすることで自動化する。
/// 補正先 native weight は bone 名で baby index 空間に再エンコードする。
/// UnityEngine + System のみ依存（実機 CoreModule で単体テストする純関数）。
/// </summary>
internal static class SkinUpperWeightConformer
{
    /// <summary>
    /// <paramref name="weights"/>（clone=Babydoll の boneWeights）を in-place で native upper の weight に全置換する。
    /// </summary>
    /// <param name="verts">clone 頂点（Babydoll upper）。</param>
    /// <param name="weights">clone boneWeights（in-place 改変）。baby bone 順の index。</param>
    /// <param name="babyBoneNames">clone SMR の bones[].name（baby 順、index ↔ 名の対応）。</param>
    /// <param name="nativeVerts">native upper 頂点（補正先 weight の NN ソース）。</param>
    /// <param name="nativeWeights">native upper boneWeights（native bone 順 index）。</param>
    /// <param name="nativeBoneNames">native bones 名（native 順、index ↔ 名）。</param>
    /// <param name="lowerVerts">target Babydoll mesh_skin_lower 頂点（継ぎ目 localization 用）。</param>
    /// <param name="seamDist">clone 頂点と baby-lower の最近傍がこの距離以内なら継ぎ目とみなし補正（m）。</param>
    /// <param name="nativeMatchDist">native upper への NN マッチがこの距離を超える頂点は skip（誤マッチ除外, m）。</param>
    /// <param name="xHalf">|x| がこれ以上の頂点は腕として除外。referenceY 計算でも腕 lower を除外する。</param>
    /// <returns>補正した頂点数。</returns>
    public static int ConformInPlace(
        Vector3[] verts, BoneWeight[] weights, string[] babyBoneNames,
        Vector3[] nativeVerts, BoneWeight[] nativeWeights, string[] nativeBoneNames,
        Vector3[] lowerVerts, float seamDist, float nativeMatchDist, float xHalf)
    {
        if (verts == null || weights == null || babyBoneNames == null) return 0;
        if (nativeVerts == null || nativeVerts.Length == 0 || nativeWeights == null) return 0;
        if (lowerVerts == null || lowerVerts.Length == 0) return 0;
        if (seamDist <= 0f) return 0;

        var babyNameToIdx = new Dictionary<string, int>();
        for (int i = 0; i < babyBoneNames.Length; i++)
            if (babyBoneNames[i] != null) babyNameToIdx[babyBoneNames[i]] = i;

        // baby-lower の継ぎ目上端 Y（torso 域 |x|<xHalf のみ）。これ + seamDist より上の clone 頂点は
        // lower から必ず seamDist 外 → 不変（胸保護）。NN 前の枝刈りに使う torso 域の厳密な上界。
        float referenceY = float.NegativeInfinity;
        for (int i = 0; i < lowerVerts.Length; i++)
        {
            if (Mathf.Abs(lowerVerts[i].x) >= xHalf) continue;
            if (lowerVerts[i].y > referenceY) referenceY = lowerVerts[i].y;
        }
        if (float.IsNegativeInfinity(referenceY)) return 0;   // torso 域の lower 頂点が無い
        float yCutoff = referenceY + seamDist;

        var lowerGrid = new SpatialGridIndex(lowerVerts);    // 継ぎ目ゲート用
        var upperGrid = new SpatialGridIndex(nativeVerts);   // 補正先 weight 用
        float seamSq = seamDist * seamDist;
        float nativeSq = nativeMatchDist * nativeMatchDist;
        int conformed = 0;

        // Mesh 由来なら verts/weights は同長。防御的に短い方で止める。
        int count = Mathf.Min(verts.Length, weights.Length);
        for (int i = 0; i < count; i++)
        {
            Vector3 v = verts[i];
            if (Mathf.Abs(v.x) >= xHalf) continue;   // 腕除外（torso seam のみ）
            if (v.y > yCutoff) continue;             // 胸/胴上部は不変（厳密な必要条件 prune）

            int kl = lowerGrid.FindNearest(v);
            if (kl < 0 || (lowerVerts[kl] - v).sqrMagnitude > seamSq) continue;   // ゲート: baby-lower 近接のみ

            int ku = upperGrid.FindNearest(v);
            if (ku < 0 || ku >= nativeWeights.Length) continue;
            if ((nativeVerts[ku] - v).sqrMagnitude > nativeSq) continue;

            // 寄せ先 native weight の bone 名が baby に 1 つも無い頂点は skip（baby weight 保持）。
            // 全置換すると Pack の fallback で baby idx0 へフルバインドし当該頂点だけ skinning が崩れるため。
            if (!TryReencode(nativeWeights[ku], nativeBoneNames, babyNameToIdx, out var nat)) continue;
            weights[i] = nat;   // 二値・全置換（フェード無し）
            conformed++;
        }
        return conformed;
    }

    // native BoneWeight を bone 名経由で baby index 空間に再エンコードする。
    // baby に存在しない bone 名の influence は捨てて残りを正規化する。
    // 対応 bone 名が 1 つも無ければ Pack の fallback (idx0=1.0) を返すので、用途では TryReencode を使う。
    internal static BoneWeight Reencode(BoneWeight nw, string[] nativeNames, Dictionary<string, int> babyNameToIdx)
    {
        TryReencode(nw, nativeNames, babyNameToIdx, out var result);
        return result;
    }

    // Reencode の bool 版。baby に対応する bone 名が 1 つも無ければ false（result は不定、寄せ先 weight 不明）。
    internal static bool TryReencode(BoneWeight nw, string[] nativeNames, Dictionary<string, int> babyNameToIdx, out BoneWeight result)
    {
        var acc = new Dictionary<int, float>();
        AddInfluence(acc, nw.boneIndex0, nw.weight0, nativeNames, babyNameToIdx);
        AddInfluence(acc, nw.boneIndex1, nw.weight1, nativeNames, babyNameToIdx);
        AddInfluence(acc, nw.boneIndex2, nw.weight2, nativeNames, babyNameToIdx);
        AddInfluence(acc, nw.boneIndex3, nw.weight3, nativeNames, babyNameToIdx);
        result = Pack(acc);
        return acc.Count > 0;
    }

    private static void AddInfluence(Dictionary<int, float> acc, int nativeIdx, float w,
        string[] nativeNames, Dictionary<string, int> babyNameToIdx)
    {
        if (w <= 0f) return;
        if (nativeNames == null || nativeIdx < 0 || nativeIdx >= nativeNames.Length) return;
        string name = nativeNames[nativeIdx];
        if (name == null || !babyNameToIdx.TryGetValue(name, out int bIdx)) return;
        acc.TryGetValue(bIdx, out float cur);
        acc[bIdx] = cur + w;
    }

    // dict を weight 降順 top4 に詰め、合計 1.0 に正規化した BoneWeight を返す。
    private static BoneWeight Pack(Dictionary<int, float> acc)
    {
        int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        float w0 = 0, w1 = 0, w2 = 0, w3 = 0;
        foreach (var kv in acc)
        {
            float w = kv.Value;
            if (w > w0) { b3 = b2; w3 = w2; b2 = b1; w2 = w1; b1 = b0; w1 = w0; b0 = kv.Key; w0 = w; }
            else if (w > w1) { b3 = b2; w3 = w2; b2 = b1; w2 = w1; b1 = kv.Key; w1 = w; }
            else if (w > w2) { b3 = b2; w3 = w2; b2 = kv.Key; w2 = w; }
            else if (w > w3) { b3 = kv.Key; w3 = w; }
        }
        float sum = w0 + w1 + w2 + w3;
        if (sum <= 0f) return new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        float inv = 1f / sum;
        return new BoneWeight
        {
            boneIndex0 = b0, weight0 = w0 * inv,
            boneIndex1 = b1, weight1 = w1 * inv,
            boneIndex2 = b2, weight2 = w2 * inv,
            boneIndex3 = b3, weight3 = w3 * inv,
        };
    }
}
