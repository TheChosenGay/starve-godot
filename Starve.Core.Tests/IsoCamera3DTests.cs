using Starve.Core;

namespace Starve.Core.Tests;

public sealed class IsoCamera3DTests
{
    [Fact]
    public void WorldTo3D_MapsHeightToYAndWyToZ()
    {
        var p = IsoCamera3D.WorldTo3D(3f, 5f, 2f);
        Assert.Equal(3f, p.X);
        Assert.Equal(2f * IsoCamera3D.HeightScale, p.Y);
        Assert.Equal(5f, p.Z);
    }

    [Fact]
    public void HeightScale_ClampsAndScalesVisualY()
    {
        var prev = IsoCamera3D.HeightScale;
        try
        {
            IsoCamera3D.HeightScale = 0.01f;
            Assert.Equal(0.12f, IsoCamera3D.HeightScale);
            IsoCamera3D.HeightScale = 4f;
            Assert.Equal(1f, IsoCamera3D.HeightScale);
            IsoCamera3D.HeightScale = 0.5f;
            Assert.Equal(1f, IsoCamera3D.VisualY(2f));
        }
        finally
        {
            IsoCamera3D.HeightScale = prev;
        }
    }

    [Fact]
    public void WorldFrom3D_RoundTripsXz()
    {
        var world = IsoCamera3D.WorldFrom3D(8f, -2f);
        var again = IsoCamera3D.WorldTo3D(world.X, world.Y);
        Assert.Equal(8f, again.X);
        Assert.Equal(0f, again.Y);
        Assert.Equal(-2f, again.Z);
    }

    [Fact]
    public void WorldOffset_PlacesCameraCenterAtOrigin()
    {
        var entity = IsoCamera3D.WorldTo3D(12f, 4f, 1f);
        var offset = IsoCamera3D.WorldOffset(12f, 4f, 1f);
        var atOrigin = entity + offset;
        Assert.Equal(0f, atOrigin.X, 4);
        Assert.Equal(0f, atOrigin.Y, 4);
        Assert.Equal(0f, atOrigin.Z, 4);
    }

    [Fact]
    public void OrthoSize_HalvesWhenZoomDoubles()
    {
        var wide = IsoCamera3D.OrthoSize(1080f, 1f);
        var tight = IsoCamera3D.OrthoSize(1080f, 2f);
        Assert.InRange(tight, wide / 2f - 1e-4f, wide / 2f + 1e-4f);
        Assert.Equal(54f * IsoCamera3D.ViewScale, wide, 3);
    }

    [Fact]
    public void OrthoSize_MatchesDimetricVerticalScale()
    {
        const float viewH = 1080f;
        const float zoom = 1f;
        var pitch = IsoCamera3D.PitchDegrees * (MathF.PI / 180f);
        var expected = MathF.Sin(pitch) / MathF.Sqrt(2f) * (viewH / (IsoMath.Step / 2f * zoom)) * IsoCamera3D.ViewScale;
        Assert.Equal(expected, IsoCamera3D.OrthoSize(viewH, zoom), 4);
    }

    [Fact]
    public void CameraPose_LooksAtOriginFromDistance()
    {
        var (position, rotation) = IsoCamera3D.CameraPose();
        Assert.Equal(-IsoCamera3D.PitchDegrees, rotation.X);
        Assert.Equal(IsoCamera3D.YawDegrees, rotation.Y);
        Assert.Equal(0f, rotation.Z);

        var forward = IsoCamera3D.ForwardYxz(rotation);
        Assert.Equal(1f, forward.Length(), 4);
        var lookAt = position + forward * IsoCamera3D.Distance;
        Assert.Equal(0f, lookAt.X, 4);
        Assert.Equal(0f, lookAt.Y, 4);
        Assert.Equal(0f, lookAt.Z, 4);
        Assert.Equal(IsoCamera3D.Distance, position.Length(), 4);
    }

    [Fact]
    public void CameraPose_QuarterTurnStillLooksAtOrigin()
    {
        var (position, rotation) = IsoCamera3D.CameraPose(MathF.PI / 2f);
        var forward = IsoCamera3D.ForwardYxz(rotation);
        var lookAt = position + forward * IsoCamera3D.Distance;
        Assert.Equal(0f, lookAt.X, 4);
        Assert.Equal(0f, lookAt.Y, 4);
        Assert.Equal(0f, lookAt.Z, 4);
        Assert.Equal(IsoCamera3D.YawDegrees + 90f, rotation.Y, 3);
    }

    [Fact]
    public void OrbitLocalOffset_MatchesCameraPoseAtDefaultYaw()
    {
        var local = IsoCamera3D.OrbitLocalOffset();
        var yaw = IsoCamera3D.YawDegrees * (MathF.PI / 180f);
        var world = new System.Numerics.Vector3(
            local.Z * MathF.Sin(yaw),
            local.Y,
            local.Z * MathF.Cos(yaw));
        var (pos, _) = IsoCamera3D.CameraPose();
        Assert.Equal(pos.X, world.X, 4);
        Assert.Equal(pos.Y, world.Y, 4);
        Assert.Equal(pos.Z, world.Z, 4);
    }

    [Fact]
    public void FollowPosition_KeepsLookAtOnPlayer()
    {
        const float camX = 12f, camY = 4f, height = 1f;
        var pos = IsoCamera3D.FollowPosition(camX, camY, height);
        var (_, rot) = IsoCamera3D.CameraPose();
        var forward = IsoCamera3D.ForwardYxz(rot);
        var lookAt = pos + forward * IsoCamera3D.Distance;
        var target = IsoCamera3D.WorldTo3D(camX, camY, height);
        Assert.Equal(target.X, lookAt.X, 4);
        Assert.Equal(target.Y, lookAt.Y, 4);
        Assert.Equal(target.Z, lookAt.Z, 4);
    }

    [Fact]
    public void FacingYaw_PlusZIsZero_PlusXIsQuarterTurn()
    {
        Assert.Equal(0f, IsoCamera3D.FacingYaw(0f, 1f), 4);
        Assert.Equal(MathF.PI / 2f, IsoCamera3D.FacingYaw(1f, 0f), 4);
        Assert.Equal(MathF.PI, MathF.Abs(IsoCamera3D.FacingYaw(0f, -1f)), 4);
    }

    [Fact]
    public void ForwardYxz_LooksDownTowardNegativeXz()
    {
        var forward = IsoCamera3D.ForwardYxz(new System.Numerics.Vector3(
            -IsoCamera3D.PitchDegrees, IsoCamera3D.YawDegrees, 0));
        Assert.True(forward.Y < 0f, $"forward.Y={forward.Y}");
        Assert.True(forward.X < 0f, $"forward.X={forward.X}");
        Assert.True(forward.Z < 0f, $"forward.Z={forward.Z}");
        Assert.Equal(forward.X, forward.Z, 4);
    }
}
