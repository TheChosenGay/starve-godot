using System;
using Godot;

namespace GodotClient.Game;

/// <summary>交叉四边形火焰：同一套 shader 材质，可选自带点光。</summary>
public partial class FireFlame3D : Node3D
{
    public FireStyle Style { get; private set; } = FireStyle.Campfire();
    public bool OwnsLight { get; set; } = true;
    public bool ShowCaption { get; set; } = true;
    public bool LightOn { get; set; } = true;
    public OmniLight3D? OwnedLight { get; private set; }
    public Label3D Caption { get; private set; } = null!;

    private ShaderMaterial _mat = null!;
    private MeshInstance3D _quadA = null!;
    private MeshInstance3D _quadB = null!;

    public override void _Ready()
    {
        Name = string.IsNullOrEmpty(Name) ? Style.Label : Name;
        _mat = FireMaterials.Create(Style);
        _quadA = MakeQuad(0);
        _quadB = MakeQuad(90);
        AddChild(_quadA);
        AddChild(_quadB);
        Caption = new Label3D
        {
            Text = Style.Label,
            PixelSize = 0.012f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            OutlineSize = 8,
            FontSize = 26,
            Position = new Vector3(0, Style.MeshHeight + 0.28f, 0),
        };
        AddChild(Caption);
        Caption.Visible = ShowCaption;
        if (OwnsLight)
        {
            OwnedLight = new OmniLight3D
            {
                Name = "Light",
                Position = new Vector3(0, Style.MeshHeight * 0.45f, 0),
                ShadowEnabled = false,
                OmniAttenuation = 0.9f,
            };
            AddChild(OwnedLight);
        }
        ApplyStyle(Style);
    }

    public override void _Process(double delta)
    {
        if (OwnedLight is null) return;
        if (!LightOn || Style.LightEnergy < 0.01f)
        {
            OwnedLight.LightEnergy = 0f;
            OwnedLight.Visible = false;
            return;
        }
        var t = (float)Time.GetTicksMsec();
        var flick = 1f
            + 0.12f * MathF.Sin(t * 0.0087f + GetInstanceId() * 0.17f)
            + 0.07f * MathF.Sin(t * 0.0173f + GetInstanceId() * 0.09f);
        OwnedLight.Visible = true;
        OwnedLight.LightEnergy = Style.LightEnergy * flick;
    }

    public void ApplyStyle(FireStyle style)
    {
        Style = style;
        if (!IsNodeReady()) return;
        FireMaterials.Apply(_mat, style);
        ResizeQuads();
        Caption.Text = style.Label;
        Caption.Position = new Vector3(0, style.MeshHeight + 0.28f, 0);
        if (OwnedLight is not null)
        {
            OwnedLight.LightColor = style.LightColor;
            OwnedLight.OmniRange = style.LightRange;
            OwnedLight.Position = new Vector3(0, style.MeshHeight * 0.45f, 0);
        }
    }

    public void SetSelected(bool on)
    {
        if (!IsNodeReady()) return;
        Caption.Modulate = on ? new Color(1f, 0.92f, 0.45f) : Colors.White;
        Caption.OutlineModulate = on ? new Color(0.35f, 0.18f, 0.02f) : Colors.Black;
    }

    private MeshInstance3D MakeQuad(float yaw)
    {
        return new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(Style.MeshWidth, Style.MeshHeight) },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0, Style.MeshHeight * 0.5f, 0),
            RotationDegrees = new Vector3(0, yaw, 0),
        };
    }

    public static Node3D CreatePit(bool caption = false, bool ownsLight = false)
    {
        var root = new Node3D { Name = "FirePit" };
        var stone = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.32f, 0.3f, 0.28f),
            Roughness = 0.95f,
        };
        for (var i = 0; i < 6; i++)
        {
            var a = i / 6f * MathF.Tau;
            root.AddChild(new MeshInstance3D
            {
                Position = new Vector3(MathF.Cos(a) * 0.42f, 0.08f, MathF.Sin(a) * 0.42f),
                Mesh = new SphereMesh { Radius = 0.12f, Height = 0.16f },
                MaterialOverride = stone,
            });
        }
        var flame = new FireFlame3D
        {
            Name = "Flame",
            OwnsLight = ownsLight,
            ShowCaption = caption,
        };
        flame.ApplyStyle(FireStyle.Wide());
        root.AddChild(flame);
        return root;
    }

    private void ResizeQuads()
    {
        var size = new Vector2(Style.MeshWidth, Style.MeshHeight);
        foreach (var quad in new[] { _quadA, _quadB })
        {
            if (quad.Mesh is QuadMesh mesh)
                mesh.Size = size;
            quad.Position = new Vector3(0, Style.MeshHeight * 0.5f, 0);
        }
    }
}
