using System;
using System.Collections.Generic;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 3D 调试：物体缩放、移速、动画速度，以及相机俯仰和光雾。F2 显隐。
/// </summary>
public partial class ActorTunePanel : Control
{
    public bool PickMode => _pick.ButtonPressed;
    public float MoveSpeedMul { get; private set; } = 1f;
    public Action? MoveSpeedChanged { get; set; }
    public World3DView? World { get; set; }

    private readonly CheckButton _pick = new() { Text = "点选物体调参（已开）", ButtonPressed = true };
    private readonly Label _target = new() { Text = "目标：已选物体（先点选）" };
    private readonly CheckButton _fog = new() { Text = "深度雾（已关）", ButtonPressed = false };
    private readonly CheckButton _skyAmbient = new() { Text = "天空环境光（已开）", ButtonPressed = true };
    private PanelContainer? _frame;
    private Node3D? _selectedNode;
    private ulong _selectedId;
    private bool _syncing;
    private readonly Dictionary<ulong, float> _animMul = new();
    private HSlider? _pitch;
    private HSlider? _scale;
    private HSlider? _move;
    private HSlider? _anim;
    private HSlider? _sunEnergy;
    private HSlider? _sunPitch;
    private HSlider? _fogBegin;
    private HSlider? _fogEnd;
    private HSlider? _ambient;
    private HSlider? _cloudCover;
    private HSlider? _cloudThick;
    private HSlider? _cloudWind;
    private HSlider? _cloudHeight;
    private HSlider? _heightScale;
    private HSlider? _subdiv;
    private HSlider? _tiling;
    private HSlider? _blendSharp;
    private HSlider? _slopeRock;
    private Timer? _terrainRebuild;

    public override void _Ready()
    {
        Name = "ActorTune";
        SetAnchorsPreset(LayoutPreset.TopLeft);
        OffsetLeft = 12;
        OffsetTop = 12;
        OffsetRight = 324;
        OffsetBottom = 720;
        MouseFilter = MouseFilterEnum.Ignore;
        Theme = HudTheme.Create();

        _frame = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        _frame.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_frame);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _frame.AddChild(scroll);
        var box = new VBoxContainer();
        scroll.AddChild(box);
        box.AddThemeConstantOverride("separation", 6);

        box.AddChild(new Label { Text = "表现调参  F2 显隐" });
        box.AddChild(new Label
        {
            Text = "点模型改缩放/动画。移动速度只作用于自己。俯仰越小越不像从天上往下看。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        box.AddChild(_pick);
        box.AddChild(_target);
        _pick.Toggled += on =>
            _pick.Text = on ? "点选物体调参（已开）" : "点选物体调参（已关）";

        box.AddChild(new Label { Text = "相机" });
        _pitch = AddSlider(box, "俯仰°", 18, 55, 1, IsoCamera3D.PitchDegrees);

        box.AddChild(new Label { Text = "地形缓坡（只改外形）" });
        _heightScale = AddSlider(box, "高度比例", 0.12f, 1f, 0.02f, IsoCamera3D.HeightScale);
        _subdiv = AddSlider(box, "细分", 1, 8, 1, MapMeshBuilder.SmoothSubdiv);
        _tiling = AddSlider(box, "贴图密度", 0.12f, 1f, 0.02f, MapMeshBuilder.WorldTiling);
        _blendSharp = AddSlider(box, "高度混合", 0.04f, 0.7f, 0.02f, TerrainHaven.DefaultHeightSharpness);
        _slopeRock = AddSlider(box, "陡坡出岩", 0f, 1f, 0.02f, TerrainHaven.DefaultSlopeRock);

        box.AddChild(new Label { Text = "自己" });
        _move = AddSlider(box, "移动速度", 0.3f, 2f, 0.05f, 1f);

        box.AddChild(new Label { Text = "已选物体" });
        _scale = AddSlider(box, "缩放", 0.3f, 2.5f, 0.05f, 1f);
        _anim = AddSlider(box, "动画速度", 0.3f, 3f, 0.05f, 1f);

        box.AddChild(new Label { Text = "光雾（叠在日夜周期上）" });
        _sunEnergy = AddSlider(box, "阳光倍率", 0.3f, 2.2f, 0.05f, 1f);
        _sunPitch = AddSlider(box, "太阳高度±", -25, 25, 1, 0);
        _ambient = AddSlider(box, "环境光倍率", 0.3f, 2.2f, 0.05f, 1f);
        box.AddChild(_skyAmbient);
        _skyAmbient.Toggled += on =>
        {
            _skyAmbient.Text = on ? "天空环境光（已开）" : "天空环境光（已关）";
            Push();
        };
        box.AddChild(_fog);
        _fog.Toggled += on =>
        {
            _fog.Text = on ? "深度雾（已开）" : "深度雾（已关）";
            Push();
        };
        _fogBegin = AddSlider(box, "雾近倍率", 0.5f, 2f, 0.05f, 1f);
        _fogEnd = AddSlider(box, "雾远倍率", 0.5f, 2f, 0.05f, 1f);

        box.AddChild(new Label { Text = "吉卜力云" });
        _cloudCover = AddSlider(box, "云量", 0.05f, 0.95f, 0.01f, CloudTune.Default.Coverage);
        _cloudThick = AddSlider(box, "厚度", 18f, 90f, 1f, CloudTune.Default.Thickness);
        _cloudWind = AddSlider(box, "风速", 0f, 2f, 0.01f, CloudTune.Default.Wind);
        _cloudHeight = AddSlider(box, "高度", 8f, 48f, 0.5f, CloudTune.Default.Height);

        var reset = new Button { Text = "恢复默认" };
        reset.Pressed += ResetAll;
        box.AddChild(reset);

        _terrainRebuild = new Timer { OneShot = true, WaitTime = 0.18 };
        _terrainRebuild.Timeout += () => World?.RebuildTerrain();
        AddChild(_terrainRebuild);
    }

    public bool Hits(Vector2 screen) =>
        Visible && _frame is { } frame && frame.GetGlobalRect().HasPoint(screen);

    public void BindSelected(ulong id, Node3D node)
    {
        _selectedId = id;
        _selectedNode = node;
        _syncing = true;
        if (_scale is not null) _scale.Value = ReadScale(node);
        if (_anim is not null)
            _anim.Value = _animMul.TryGetValue(id, out var mul) ? mul : ReadAnimMul(node);
        _syncing = false;
        _target.Text = $"目标：{node.Name}";
        Push();
    }

    private void ResetAll()
    {
        _syncing = true;
        if (_pitch is not null) _pitch.Value = 45;
        if (_heightScale is not null) _heightScale.Value = IsoCamera3D.DefaultHeightScale;
        if (_subdiv is not null) _subdiv.Value = MapMeshBuilder.DefaultSmoothSubdiv;
        if (_tiling is not null) _tiling.Value = MapMeshBuilder.DefaultWorldTiling;
        if (_blendSharp is not null) _blendSharp.Value = TerrainHaven.DefaultHeightSharpness;
        if (_slopeRock is not null) _slopeRock.Value = TerrainHaven.DefaultSlopeRock;
        if (_move is not null) _move.Value = 1;
        if (_scale is not null) _scale.Value = 1;
        if (_anim is not null) _anim.Value = 1;
        if (_sunEnergy is not null) _sunEnergy.Value = 1;
        if (_sunPitch is not null) _sunPitch.Value = 0;
        if (_ambient is not null) _ambient.Value = 1;
        if (_fogBegin is not null) _fogBegin.Value = 1;
        if (_fogEnd is not null) _fogEnd.Value = 1;
        if (_cloudCover is not null) _cloudCover.Value = CloudTune.Default.Coverage;
        if (_cloudThick is not null) _cloudThick.Value = CloudTune.Default.Thickness;
        if (_cloudWind is not null) _cloudWind.Value = CloudTune.Default.Wind;
        if (_cloudHeight is not null) _cloudHeight.Value = CloudTune.Default.Height;
        _fog.ButtonPressed = false;
        _skyAmbient.ButtonPressed = true;
        _syncing = false;
        Push();
    }

    private void Push()
    {
        if (_syncing) return;
        if (_pitch is not null)
            IsoCamera3D.PitchDegrees = (float)_pitch.Value;
        var terrainDirty = false;
        if (_heightScale is not null)
        {
            var scale = (float)_heightScale.Value;
            if (MathF.Abs(scale - IsoCamera3D.HeightScale) > 1e-4f)
            {
                IsoCamera3D.HeightScale = scale;
                terrainDirty = true;
            }
        }
        if (_subdiv is not null)
        {
            var n = (int)Math.Round(_subdiv.Value);
            if (n != MapMeshBuilder.SmoothSubdiv)
            {
                MapMeshBuilder.SmoothSubdiv = n;
                terrainDirty = true;
            }
        }
        if (terrainDirty)
        {
            _terrainRebuild?.Stop();
            _terrainRebuild?.Start();
        }
        if (_tiling is not null)
            World?.SetTerrainTiling((float)_tiling.Value);
        World?.SetTerrainBlend(
            (float)(_blendSharp?.Value ?? TerrainHaven.DefaultHeightSharpness),
            (float)(_slopeRock?.Value ?? TerrainHaven.DefaultSlopeRock));
        if (_move is not null)
        {
            var mul = (float)_move.Value;
            if (MathF.Abs(mul - MoveSpeedMul) > 1e-4f)
            {
                MoveSpeedMul = mul;
                MoveSpeedChanged?.Invoke();
            }
        }
        if (_selectedNode is not null)
        {
            if (_scale is not null) ApplyScale(_selectedNode, (float)_scale.Value);
            if (_anim is not null)
            {
                var anim = (float)_anim.Value;
                if (_selectedId != 0) _animMul[_selectedId] = anim;
                ApplyAnim(_selectedNode, anim);
            }
        }
        World?.SetTune(new LightTune(
            (float)(_sunEnergy?.Value ?? 1),
            (float)(_sunPitch?.Value ?? 0),
            (float)(_ambient?.Value ?? 1),
            _fog.ButtonPressed,
            (float)(_fogBegin?.Value ?? 1),
            (float)(_fogEnd?.Value ?? 1),
            new CloudTune(
                (float)(_cloudCover?.Value ?? CloudTune.Default.Coverage),
                (float)(_cloudThick?.Value ?? CloudTune.Default.Thickness),
                (float)(_cloudWind?.Value ?? CloudTune.Default.Wind),
                (float)(_cloudHeight?.Value ?? CloudTune.Default.Height)),
            _skyAmbient.ButtonPressed));
    }

    private static float ReadScale(Node3D node)
    {
        if (node is PigmanActor3D pig) return pig.ModelScale;
        if (node is TreeActor3D tree) return tree.ModelScale;
        return ActorMesh3D.TuneScaleOf(node);
    }

    private static void ApplyScale(Node3D node, float scale)
    {
        if (node is PigmanActor3D pig)
            pig.ModelScale = scale;
        else if (node is TreeActor3D tree)
            tree.ModelScale = scale;
        else
            ActorMesh3D.SetTuneScale(node, scale);
    }

    private static float ReadAnimMul(Node3D node) =>
        node is PigmanActor3D pig ? pig.AnimSpeedMul : 1f;

    private static void ApplyAnim(Node3D node, float mul)
    {
        if (node is PigmanActor3D pig)
        {
            pig.AnimSpeedMul = mul;
            return;
        }
        foreach (var child in node.FindChildren("*", "AnimationPlayer", true, false))
        {
            if (child is AnimationPlayer player)
                player.SpeedScale = mul;
        }
    }

    private HSlider AddSlider(VBoxContainer box, string title, float min, float max, float step, float value)
    {
        var row = new HBoxContainer();
        var label = new Label { Text = title, CustomMinimumSize = new Vector2(86, 0) };
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(140, 0),
        };
        var num = new Label { Text = Format(value), CustomMinimumSize = new Vector2(40, 0) };
        slider.ValueChanged += v =>
        {
            num.Text = Format((float)v);
            Push();
        };
        row.AddChild(label);
        row.AddChild(slider);
        row.AddChild(num);
        box.AddChild(row);
        return slider;
    }

    private static string Format(float v) => v >= 10 ? v.ToString("0") : v.ToString("0.00");
}
