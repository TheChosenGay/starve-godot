using Godot;

namespace GodotClient.Game;

/// <summary>
/// 天空云层：薄的水平体积层 + 地面投影云影。跟着注视点走，盒子要大到看不见边缘。
/// </summary>
public partial class CloudLayer3D : Node3D
{
    public const float DefaultHeight = 13f;
    public static readonly Vector3 DefaultBoxSize = new(320f, 10f, 320f);
    public static readonly Vector2 DefaultShadowSize = new(180f, 180f);

    public ShaderMaterial VolumeMat { get; }
    public ShaderMaterial ShadowMat { get; }

    private readonly MeshInstance3D _volume;
    private readonly MeshInstance3D _shadow;
    private readonly BoxMesh _box;
    private readonly PlaneMesh _shadowPlane;
    private float _height = DefaultHeight;
    private Vector3 _boxSize = DefaultBoxSize;

    public CloudLayer3D(bool groundShadow = false) : this(CloudTune.Default, groundShadow)
    {
    }

    public CloudLayer3D(CloudTune tune, bool groundShadow = false)
    {
        Name = "CloudLayer";
        VolumeMat = GhibliSky.CreateVolume();
        ShadowMat = GhibliSky.CreateShadow();

        _box = new BoxMesh { Size = _boxSize, Material = VolumeMat };
        _volume = new MeshInstance3D
        {
            Name = "Volume",
            Mesh = _box,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 128f,
            SortingOffset = 4f,
        };
        AddChild(_volume);

        _shadowPlane = new PlaneMesh { Size = DefaultShadowSize, Material = ShadowMat };
        _shadow = new MeshInstance3D
        {
            Name = "Shadow",
            Mesh = _shadowPlane,
            Position = new Vector3(0, 0.04f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_shadow);
        _shadow.Visible = groundShadow;
        SetHeight(tune.Height);
    }

    public void Follow(Vector3 focus)
    {
        Position = new Vector3(focus.X, 0f, focus.Z);
        PushBounds();
    }

    public void SetHeight(float height)
    {
        _height = Mathf.Clamp(height, 6f, 56f);
        _volume.Position = new Vector3(0, _height, 0);
        PushBounds();
    }

    private void PushBounds()
    {
        var origin = IsInsideTree() ? GlobalPosition : Position;
        var center = new Vector3(origin.X, _height, origin.Z);
        VolumeMat.SetShaderParameter("box_center", center);
        VolumeMat.SetShaderParameter("box_size", _boxSize);
        ShadowMat.SetShaderParameter("box_center", center);
        ShadowMat.SetShaderParameter("box_size", _boxSize);
    }
}
