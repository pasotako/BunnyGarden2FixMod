using System.Collections.Generic;
using UnityEngine;
using Xunit;
using BunnyGarden2FixMod.Patches.CostumeChanger;

// サンプル機能テスト: コードベースの純粋ユーティリティ SpatialGridIndex を Compile link で取り込み、
// 実機 UnityEngine.CoreModule に対して単体テストする。新しい純関数をテストする際の雛形。
//
// 検証対象は public API（FindNearest/FindKNearest/FindWithinRadius）の結果であり、内部の
// grid 走査経路ではない。この小規模入力（5 点）は cellSize≈2 で全点が単一セルに収まるため、
// 実際には 1 セル走査 / brute-force fallback 経路を通る（多セル shell 走査はカバーしない）。
// query は点群近傍に置く（点群から cellSize に対して遠い query は shell 走査が増えるため避ける）。
public class SpatialGridIndexTests
{
    // origin 近傍の 5 点。query は点群の近くに置く。
    private static Vector3[] SamplePoints() => new[]
    {
        new Vector3(0, 0, 0), // index 0
        new Vector3(1, 0, 0), // index 1
        new Vector3(0, 1, 0), // index 2
        new Vector3(0, 0, 1), // index 3
        new Vector3(2, 2, 2), // index 4 (遠い)
    };

    [Fact]
    public void FindNearest_ReturnsClosestPointIndex()
    {
        var grid = new SpatialGridIndex(SamplePoints());

        int idx = grid.FindNearest(new Vector3(0.1f, 0.1f, 0f));

        Assert.Equal(0, idx); // (0,0,0) が最近傍
    }

    [Fact]
    public void FindKNearest_ReturnsKIndicesNearestFirst()
    {
        var grid = new SpatialGridIndex(SamplePoints());
        var results = new List<int>();

        grid.FindKNearest(new Vector3(0, 0, 0), 3, results);

        Assert.Equal(3, results.Count);
        Assert.Equal(0, results[0]);        // 完全一致が先頭
        Assert.DoesNotContain(4, results);  // 最遠点 (2,2,2) は含まれない
    }

    [Fact]
    public void FindWithinRadius_ReturnsPointsInsideRadius()
    {
        var grid = new SpatialGridIndex(SamplePoints());
        var results = new List<int>();

        grid.FindWithinRadius(new Vector3(0, 0, 0), 1.1f, results);

        // 半径 1.1 内: index 0(dist0), 1/2/3(dist1)。index 4(dist≈3.46)は範囲外。
        Assert.Equal(4, results.Count);
        Assert.Contains(0, results);
        Assert.Contains(1, results);
        Assert.Contains(2, results);
        Assert.Contains(3, results);
        Assert.DoesNotContain(4, results);
    }

    [Fact]
    public void FindNearest_EmptyPointCloud_ReturnsMinusOne()
    {
        var grid = new SpatialGridIndex(new Vector3[0]);

        Assert.Equal(-1, grid.FindNearest(new Vector3(1, 2, 3)));
    }
}
