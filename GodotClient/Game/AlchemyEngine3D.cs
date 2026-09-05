using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// Meshy 炼金引擎。网格以原点为中心，落地时抬到脚底。
/// 玩家走进范围时加载 bounce shader：先下蹲蓄力，再衰减回弹两三次。
/// </summary>
public partial class AlchemyEngine3D : Node3D
{
    public const string ModelPath = "res://assets/models/alchemy-engine/alchemy-engine.glb";
    public const string AlbedoTexPath = "res://assets/models/alchemy-engine/alchemy-engine_base_color.jpg";
    public const float GroundLift = 0.952f;

    private float _modelScale = 1f;
    private readonly List<ShaderMaterial> _bounceMats = [];
    private bool _near;
    private bool _bouncing;
    private float _bounceT;

    [Export(PropertyHint.Range, "0.2,3,0.05")]
    public float ModelScale
    {
        get => _modelScale;
        set
        {
            _modelScale = Mathf.Max(0.05f, value);
            var visual = GetNodeOrNull<Node3D>("Visual");
            if (visual is not null) visual.Scale = Vector3.One * _modelScale;
        }
    }

    [Export(PropertyHint.Range, "0.6,6,0.1")]
    public float ApproachRadius { get; set; } = 2.6f;

    public bool IsBouncing => _bouncing;

    public bool ApplyToon { get; private set; }

    public void SetApplyToon(bool on)
    {
        if (on == ApplyToon && IsInsideTree() && on == ToonMaterials.HasActorToon(this))
            return;
        ApplyToon = on;
        if (IsInsideTree())
            Rebuild();
    }

    public override void _Ready()
    {
        SetProcess(false);
        Rebuild();
    }

    public void NotifyPlayerDistance(float distance)
    {
        var inside = distance <= ApproachRadius;
        if (inside && !_near)
            PlayBounce();
        _near = inside;
    }

    public void PlayBounce()
    {
        if (_bounceMats.Count == 0) return;
        _bouncing = true;
        _bounceT = 0f;
        AlchemyBounce.SetTime(_bounceMats, 0f);
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (!_bouncing) return;
        _bounceT += (float)delta;
        if (_bounceT >= AlchemyBounce.Duration)
        {
            _bouncing = false;
            _bounceT = 0f;
            AlchemyBounce.SetTime(_bounceMats, 0f);
            SetProcess(false);
            return;
        }

        AlchemyBounce.SetTime(_bounceMats, _bounceT);
    }

    private void Rebuild()
    {
        _bounceMats.Clear();
        var keepToon = ApplyToon;
        var old = GetNodeOrNull<Node>("Visual");
        if (old is not null)
        {
            RemoveChild(old);
            old.Free();
        }

        if (!ResourceLoader.Exists(ModelPath))
        {
            AddChild(new Label3D
            {
                Name = "Visual",
                Text = "缺少 " + ModelPath,
                Position = new Vector3(0, 1.2f, 0),
                PixelSize = 0.01f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            });
            return;
        }

        var packed = GD.Load<PackedScene>(ModelPath);
        if (packed is null)
        {
            AddChild(new Label3D
            {
                Name = "Visual",
                Text = "炼金引擎 glb 尚未导入，请等 Godot 导入完成",
                Position = new Vector3(0, 1.2f, 0),
                PixelSize = 0.01f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            });
            return;
        }

        var visual = new Node3D
        {
            Name = "Visual",
            Scale = Vector3.One * _modelScale,
        };
        AddChild(visual);
        var model = packed.Instantiate<Node3D>();
        model.Position = new Vector3(0, GroundLift, 0);
        visual.AddChild(model);
        if (keepToon)
            BindToon(model);
        else
            AlchemyBounce.BindTree(model, _bounceMats);
    }

    private void BindToon(Node model)
    {
        var fallback = ResourceLoader.Exists(AlbedoTexPath)
            ? GD.Load<Texture2D>(AlbedoTexPath)
            : null;
        foreach (var child in model.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh || child is MultiMeshInstance3D) continue;
            var surfaces = mesh.Mesh?.GetSurfaceCount() ?? 0;
            if (surfaces <= 0)
            {
                var src = ToonMaterials.SourceMaterial(mesh, 0);
                mesh.MaterialOverride = ToonMaterials.Create(
                    ToonMaterials.ExtractAlbedoColor(src),
                    ToonMaterials.ExtractAlbedoTex(src) ?? fallback);
                continue;
            }

            for (var i = 0; i < surfaces; i++)
            {
                var src = ToonMaterials.SourceMaterial(mesh, i);
                mesh.SetSurfaceOverrideMaterial(i, ToonMaterials.Create(
                    ToonMaterials.ExtractAlbedoColor(src),
                    ToonMaterials.ExtractAlbedoTex(src) ?? fallback));
            }
        }

        AlchemyBounce.BindExisting(model, _bounceMats);
    }
}
