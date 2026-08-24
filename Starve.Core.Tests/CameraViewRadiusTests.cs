using Google.Protobuf;
using Starve.Core;
using Starve.Game.V1;

namespace Starve.Core.Tests;

public class CameraViewRadiusTests
{
    [Fact]
    public void ViewportChebyshev_ShrinksWhenZoomingIn()
    {
        var wide = Camera.ViewportChebyshev(1920, 1080, 1f);
        var tight = Camera.ViewportChebyshev(1920, 1080, 2f);
        Assert.True(wide > 40, $"zoom1 cheb={wide}");
        Assert.InRange(tight, wide / 2 - 0.01f, wide / 2 + 0.01f);
    }

    [Fact]
    public void SetViewRadius_ZeroUsesDefault()
    {
        var cam = new Camera();
        cam.SetViewRadius(0);
        Assert.Equal(Camera.DefaultViewRadius, cam.ViewRadius);
        Assert.Equal(Camera.DefaultViewRadius, cam.ViewRadiusMax);
    }

    [Fact]
    public void SetViewRadius_NegativeMeansUnlimited()
    {
        var cam = new Camera();
        cam.SetViewRadius(-8);
        Assert.Equal(-1, cam.ViewRadius);
        Assert.Equal(-1, cam.ViewRadiusMax);
    }

    [Fact]
    public void SetViewRange_ZeroMinUsesDefaultAndKeepsMax()
    {
        var cam = new Camera();
        cam.SetViewRange(0, 32);
        Assert.Equal(Camera.DefaultViewRadius, cam.ViewRadius);
        Assert.Equal(32, cam.ViewRadiusMax);
    }

    [Fact]
    public void SetViewRange_LiftsMaxUpToMin()
    {
        var cam = new Camera();
        cam.SetViewRange(24, 8);
        Assert.Equal(24, cam.ViewRadius);
        Assert.Equal(24, cam.ViewRadiusMax);
    }

    [Fact]
    public void SyncToViewport_RaisesMinZoomToFitServerRadius()
    {
        var cam = new Camera();
        cam.SetViewRadius(24);
        cam.SetZoom(0.4f);
        cam.SyncToViewport(1920, 1080);
        Assert.True(cam.MinZoom > 1.5f, $"minZoom={cam.MinZoom}");
        Assert.Equal(cam.MinZoom, cam.ZoomLevel);
        Assert.True(Camera.ViewportChebyshev(1920, 1080, cam.ZoomLevel) <= 24 + 0.05f);
    }

    [Fact]
    public void SyncToViewport_RangeAllowsZoomOutToMax()
    {
        var tight = new Camera();
        tight.SetViewRadius(24);
        tight.SyncToViewport(1920, 1080);

        var wide = new Camera();
        wide.SetViewRange(12, 32);
        wide.SetZoom(0.4f);
        wide.SyncToViewport(1920, 1080);
        Assert.True(wide.MinZoom < tight.MinZoom, $"wide={wide.MinZoom} tight={tight.MinZoom}");
        Assert.True(Camera.ViewportChebyshev(1920, 1080, wide.ZoomLevel) <= 32 + 0.05f);
    }

    [Fact]
    public void SyncToViewport_RangeClampsZoomInAndOut()
    {
        var cam = new Camera();
        cam.SetViewRange(12, 32);
        cam.SetZoom(0.4f);
        cam.SyncToViewport(800, 600);
        Assert.Equal(cam.MinZoom, cam.ZoomLevel);
        Assert.True(Camera.ViewportChebyshev(800, 600, cam.ZoomLevel) <= 32 + 0.05f);

        cam.SetZoom(10f);
        cam.SyncToViewport(800, 600);
        Assert.Equal(cam.MaxZoom, cam.ZoomLevel);
        Assert.True(cam.MaxZoom > cam.MinZoom);
        Assert.InRange(Camera.ViewportChebyshev(800, 600, cam.ZoomLevel), 12 - 0.05f, 12 + 0.05f);
    }

    [Fact]
    public void SyncToViewport_LargeViewportStillFitsRadius()
    {
        var cam = new Camera();
        cam.SetViewRadius(24);
        cam.SetZoom(1f);
        cam.SyncToViewport(3840, 2160);
        Assert.True(cam.MinZoom > 3f, $"minZoom={cam.MinZoom}");
        Assert.Equal(cam.MinZoom, cam.ZoomLevel);
        Assert.True(Camera.ViewportChebyshev(3840, 2160, cam.ZoomLevel) <= 24 + 0.05f);
    }

    [Fact]
    public void SyncToViewport_UnlimitedKeepsDefaultMinZoom()
    {
        var cam = new Camera();
        cam.SetViewRadius(-1);
        cam.SetZoom(0.4f);
        cam.SyncToViewport(1920, 1080);
        Assert.Equal(0.4f, cam.MinZoom);
        Assert.Equal(0.4f, cam.ZoomLevel);
    }

    [Fact]
    public void SyncToViewport_ClampsPanInsideRadius()
    {
        var cam = new Camera();
        cam.SetViewRadius(24);
        cam.SetZoom(3f);
        cam.Teleport(100, 0);
        cam.SyncToViewport(800, 600);
        Assert.True(MathF.Abs(cam.CenterX()) <= 24 + 0.05f);
    }

    [Fact]
    public void GameConfig_ViewRadius_RoundtripsThroughProtobuf()
    {
        var parsed = GameConfig.Parser.ParseFrom(new GameConfig
        {
            ViewRadius = 16,
            ViewRadiusMax = 32,
            ViewPreload = 4,
        }.ToByteArray());
        Assert.Equal(16, parsed.ViewRadius);
        Assert.Equal(32, parsed.ViewRadiusMax);
        Assert.Equal(4, parsed.ViewPreload);

        var unlimited = GameConfig.Parser.ParseFrom(new GameConfig { ViewRadius = -1 }.ToByteArray());
        Assert.Equal(-1, unlimited.ViewRadius);
    }
}
