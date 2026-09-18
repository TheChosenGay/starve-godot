using Starve.Core;

namespace Starve.Core.Tests;

public sealed class SlopeMeshTests
{
    [Fact]
    public void EdgeHeight_FlatStaysConstant()
    {
        Assert.Equal(4f, SlopeMesh.EdgeHeight(4, 4, 0.5f));
    }

    [Fact]
    public void EdgeHeight_DropStaysHighUntilCliffBand()
    {
        Assert.Equal(2f, SlopeMesh.EdgeHeight(2, 1, 0f), 3);
        Assert.Equal(2f, SlopeMesh.EdgeHeight(2, 1, 1f - SlopeMesh.CliffBand), 3);
        Assert.Equal(1.5f, SlopeMesh.EdgeHeight(2, 1, 1f - SlopeMesh.CliffBand / 2f), 3);
        Assert.Equal(1f, SlopeMesh.EdgeHeight(2, 1, 1f), 3);
    }

    [Fact]
    public void EdgeHeight_RiseCliffsNearStart()
    {
        Assert.Equal(1f, SlopeMesh.EdgeHeight(1, 2, 0f), 3);
        Assert.Equal(1.5f, SlopeMesh.EdgeHeight(1, 2, SlopeMesh.CliffBand / 2f), 3);
        Assert.Equal(2f, SlopeMesh.EdgeHeight(1, 2, SlopeMesh.CliffBand), 3);
        Assert.Equal(2f, SlopeMesh.EdgeHeight(1, 2, 1f), 3);
    }

    [Fact]
    public void HeightAt_FlatTileMatchesCorners()
    {
        var tm = Map(1, 1, [3, 3, 3, 3]);
        Assert.Equal(3f, tm.HeightAt(0.25f, 0.75f), 3);
    }

    [Fact]
    public void HeightAt_SouthDropKeepsTerraceThenCliffs()
    {
        // 北 1，南 0：平台占到 1-band，南缘才落到 0
        var tm = Map(1, 1, [1, 1, 0, 0]);
        Assert.Equal(1f, tm.HeightAt(0.5f, 0.1f), 3);
        Assert.Equal(1f, tm.HeightAt(0.5f, 1f - SlopeMesh.CliffBand), 3);
        Assert.Equal(0f, tm.HeightAt(0.5f, 1f), 3);
        var midCliff = 1f - SlopeMesh.CliffBand / 2f;
        Assert.InRange(tm.HeightAt(0.5f, midCliff), 0.4f, 0.6f);
    }

    [Fact]
    public void HeightAt_SmoothSlopesLerpsAcrossTile()
    {
        var tm = Map(1, 1, [0, 0, 2, 2]);
        tm.SmoothSlopes = true;
        Assert.Equal(0f, tm.HeightAt(0.5f, 0f), 3);
        Assert.Equal(1f, tm.HeightAt(0.5f, 0.5f), 3);
        Assert.Equal(2f, tm.HeightAt(0.5f, 1f), 3);
    }

    [Fact]
    public void LogicalHeightAt_IgnoresSmoothSlopesAndMatchesCliffBand()
    {
        var tm = Map(1, 1, [0, 0, 2, 2]);
        tm.SmoothSlopes = true;
        Assert.Equal(SlopeMesh.SampleHeight(tm, 0.5f, 0.5f), tm.LogicalHeightAt(0.5f, 0.5f), 5);
        Assert.NotEqual(tm.HeightAt(0.5f, 0.5f), tm.LogicalHeightAt(0.5f, 0.5f), 3);
    }

    [Fact]
    public void BuildTileSmooth_RampIsSingleNonCliffQuad()
    {
        var tm = Map(1, 1, [2, 2, 1, 1]);
        var q = Assert.Single(SlopeMesh.BuildTileSmooth(tm, 0, 0));
        Assert.False(q.Cliff);
        Assert.Equal(2f, q.V0.Height, 3);
        Assert.Equal(2f, q.V1.Height, 3);
        Assert.Equal(1f, q.V2.Height, 3);
        Assert.Equal(1f, q.V3.Height, 3);
    }

    [Fact]
    public void HeightAt_SharedEdgeMatchesNeighborTile()
    {
        // 两格东西相邻，中间竖边两端高度 2 / 1
        var tm = Map(2, 1, [2, 2, 1, 1, 1, 0]);
        for (var i = 0; i <= 10; i++)
        {
            var fy = i / 10f;
            var left = SlopeMesh.HeightOnTile(2, 2, 1, 1, 1f, fy);
            var right = SlopeMesh.HeightOnTile(2, 1, 1, 0, 0f, fy);
            Assert.Equal(left, right, 4);
            Assert.Equal(left, tm.HeightAt(1f, fy), 4);
        }
    }

    [Fact]
    public void BuildTile_FlatEmitsSingleGroundQuad()
    {
        var tm = Map(1, 1, [0, 0, 0, 0]);
        var quads = SlopeMesh.BuildTile(tm, 0, 0);
        Assert.Single(quads);
        Assert.False(quads[0].Cliff);
        Assert.Equal(0f, quads[0].FaceSlope);
    }

    [Fact]
    public void BuildTile_SouthDropEmitsCliffFace()
    {
        var tm = Map(1, 1, [2, 2, 1, 1]);
        var quads = SlopeMesh.BuildTile(tm, 0, 0);
        Assert.Equal(2, quads.Count);
        Assert.False(quads[0].Cliff);
        Assert.True(quads[1].Cliff);
        Assert.True(quads[1].FaceSlope >= 0.5f);
        Assert.Equal(2, quads[0].V0.Height, 3);
        Assert.Equal(2, quads[0].V3.Height, 3);
        Assert.Equal(1, quads[1].V3.Height, 3);
    }

    [Fact]
    public void BuildTile_WaterStaysFlatAtMaxCorner()
    {
        var heights = new byte[] { 2, 1, 0, 1 };
        var types = new byte[] { 1, 1, 1, 1 };
        var tm = new TileMap(1, 1, heights, types);
        var quads = SlopeMesh.BuildTile(tm, 0, 0);
        Assert.Single(quads);
        Assert.True(quads[0].Water);
        Assert.False(quads[0].Cliff);
        Assert.Equal(2f, quads[0].V0.Height);
        Assert.Equal(2f, tm.HeightAt(0.4f, 0.6f), 3);
    }

    [Fact]
    public void BuildTile_WorldPositionsStayOnIntegerGridCorners()
    {
        var tm = Map(1, 1, [0, 0, 0, 0]);
        var q = Assert.Single(SlopeMesh.BuildTile(tm, 0, 0));
        Assert.Equal(0f, q.V0.Wx);
        Assert.Equal(0f, q.V0.Wy);
        Assert.Equal(1f, q.V2.Wx);
        Assert.Equal(1f, q.V2.Wy);
    }

    [Fact]
    public void LocalProjection_DropsHeightOnScreenY()
    {
        var high = IsoMath.WorldToLocal(1, 1, 2);
        var low = IsoMath.WorldToLocal(1, 1, 1);
        Assert.Equal(high.X, low.X, 3);
        Assert.Equal(IsoMath.Step, low.Y - high.Y, 3);
    }

    /// <summary>角数组行优先：(0,0) (1,0) … 然后下一行。</summary>
    private static TileMap Map(int w, int h, byte[] cornerHeights, byte type = 3)
    {
        var corners = (w + 1) * (h + 1);
        Assert.Equal(corners, cornerHeights.Length);
        var types = Enumerable.Repeat(type, corners).Select(v => (byte)v).ToArray();
        return new TileMap(w, h, cornerHeights, types);
    }
}
