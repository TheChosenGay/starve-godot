using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 3D 调试：运行时 toon 滑条。F1 显隐。勾选「点选物体」后左键只改该物体材质。
/// </summary>
public partial class ToonTunePanel : Control
{
    public enum Scope
    {
        Actors,
        Terrain,
        Selected,
    }

    public bool PickMode => _pick.ButtonPressed;
    public Scope CurrentScope => (Scope)_scope.Selected;

    public Func<IEnumerable<Node3D>>? CollectActors { get; set; }
    public Node3D? TerrainRoot { get; set; }

    private readonly Dictionary<ulong, ToonStyle> _overrides = new();
    private readonly CheckButton _pick = new() { Text = "点选物体调参（已开）", ButtonPressed = true };
    private PanelContainer? _frame;
    private readonly OptionButton _scope = new();
    private readonly Label _target = new() { Text = "目标：全部角色" };
    private readonly CheckButton _hideOutline = new() { Text = "关掉轮廓（地形无效）" };
    private Node3D? _selectedNode;
    private ulong _selectedId;
    private bool _syncing;
    private HSlider? _bands;
    private HSlider? _shadeMin;
    private HSlider? _fill;
    private HSlider? _rim;
    private HSlider? _outline;
    private ColorPickerButton? _shadow;
    private ColorPickerButton? _outlineColor;

    public override void _Ready()
    {
        Name = "ToonTune";
        SetAnchorsPreset(LayoutPreset.TopRight);
        OffsetLeft = -312;
        OffsetTop = 12;
        OffsetRight = -12;
        OffsetBottom = 520;
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

        box.AddChild(new Label { Text = "Toon 调参  F1 显隐" });
        box.AddChild(new Label
        {
            Text = "范围：全部角色 / 地形 / 已选单个。点选开着时点模型只改它。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _scope.AddItem("全部角色", (int)Scope.Actors);
        _scope.AddItem("地形", (int)Scope.Terrain);
        _scope.AddItem("已选物体", (int)Scope.Selected);
        _scope.ItemSelected += _ =>
        {
            LoadSliders();
            UpdateTargetLabel();
        };
        box.AddChild(_scope);
        box.AddChild(_pick);
        box.AddChild(_target);
        _pick.Toggled += on =>
        {
            _pick.Text = on ? "点选物体调参（已开）" : "点选物体调参（已关）";
            if (on) _scope.Selected = (int)Scope.Selected;
            UpdateTargetLabel();
        };

        _bands = AddSlider(box, "色阶级数", 2, 6, 1, ToonMaterials.ActorDefaults.Bands);
        _shadeMin = AddSlider(box, "暗部亮度", 0, 1, 0.01f, ToonMaterials.ActorDefaults.ShadeMin);
        _fill = AddSlider(box, "填充光", 0, 0.6f, 0.01f, ToonMaterials.ActorDefaults.Fill);
        _rim = AddSlider(box, "边缘光", 0, 1, 0.01f, ToonMaterials.ActorDefaults.Rim);
        _outline = AddSlider(box, "轮廓宽度", 0, 0.08f, 0.001f, ToonMaterials.ActorDefaults.OutlineWidth);
        _shadow = AddColor(box, "阴影色", ToonMaterials.ActorDefaults.ShadowTint);
        _outlineColor = AddColor(box, "轮廓色", ToonMaterials.ActorDefaults.OutlineColor);
        box.AddChild(_hideOutline);
        _hideOutline.Toggled += _ => Push();

        var reset = new Button { Text = "恢复当前目标默认" };
        reset.Pressed += ResetCurrent;
        box.AddChild(reset);
        box.AddChild(new Label
        {
            Text = "点选开启时左键选模型（脚底黄环=已选），不会攻击。关掉即可正常交互。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
    }

    public bool Hits(Vector2 screen) =>
        Visible && _frame is { } frame && frame.GetGlobalRect().HasPoint(screen);

    public void BindSelected(ulong id, Node3D node)
    {
        _selectedId = id;
        _selectedNode = node;
        _scope.Selected = (int)Scope.Selected;
        if (!_overrides.ContainsKey(id))
            _overrides[id] = ToonMaterials.ActorDefaults.Clone();
        LoadSliders();
        UpdateTargetLabel();
        Push();
    }

    private void ResetCurrent()
    {
        if (CurrentScope == Scope.Terrain)
        {
            CopyInto(ToonMaterials.TerrainDefaults, new ToonStyle
            {
                Bands = 3f,
                Rim = 0f,
                ShadeMin = 0.4f,
                Fill = 0.16f,
                ShadowTint = new Color(0.42f, 0.48f, 0.62f),
                OutlineWidth = 0f,
                OutlineColor = new Color(0.07f, 0.05f, 0.09f),
            });
        }
        else if (CurrentScope == Scope.Selected && _selectedId != 0)
        {
            _overrides[_selectedId] = ToonMaterials.ActorDefaults.Clone();
        }
        else
        {
            CopyInto(ToonMaterials.ActorDefaults, new ToonStyle());
        }
        LoadSliders();
        Push();
    }

    private ToonStyle CurrentStyle()
    {
        if (CurrentScope == Scope.Terrain) return ToonMaterials.TerrainDefaults;
        if (CurrentScope == Scope.Selected && _overrides.TryGetValue(_selectedId, out var over))
            return over;
        return ToonMaterials.ActorDefaults;
    }

    private void LoadSliders()
    {
        _syncing = true;
        var s = CurrentStyle();
        if (_bands is not null) _bands.Value = s.Bands;
        if (_shadeMin is not null) _shadeMin.Value = s.ShadeMin;
        if (_fill is not null) _fill.Value = s.Fill;
        if (_rim is not null) _rim.Value = s.Rim;
        if (_outline is not null) _outline.Value = s.OutlineWidth;
        if (_shadow is not null) _shadow.Color = s.ShadowTint;
        if (_outlineColor is not null) _outlineColor.Color = s.OutlineColor;
        _hideOutline.ButtonPressed = s.OutlineWidth <= 1e-4f;
        _syncing = false;
        UpdateTargetLabel();
    }

    private void ReadSliders(ToonStyle s)
    {
        if (_bands is not null) s.Bands = (float)Math.Round(_bands.Value);
        if (_shadeMin is not null) s.ShadeMin = (float)_shadeMin.Value;
        if (_fill is not null) s.Fill = (float)_fill.Value;
        if (_rim is not null) s.Rim = (float)_rim.Value;
        if (_outline is not null) s.OutlineWidth = _hideOutline.ButtonPressed ? 0f : (float)_outline.Value;
        if (_shadow is not null) s.ShadowTint = _shadow.Color;
        if (_outlineColor is not null) s.OutlineColor = _outlineColor.Color;
    }

    private void Push()
    {
        if (_syncing) return;
        var style = CurrentStyle();
        ReadSliders(style);
        if (CurrentScope == Scope.Terrain)
        {
            if (TerrainRoot is not null)
                ToonMaterials.ApplyStyleToTerrain(TerrainRoot, style);
            return;
        }
        if (CurrentScope == Scope.Selected)
        {
            if (_selectedNode is not null)
                ToonMaterials.ApplyStyleToTree(_selectedNode, style);
            return;
        }
        if (CollectActors is null) return;
        foreach (var node in CollectActors())
        {
            if (node.Name.ToString() is { } name &&
                name.StartsWith("Entity_") &&
                ulong.TryParse(name[7..], out var id) &&
                _overrides.ContainsKey(id))
                continue;
            ToonMaterials.ApplyStyleToTree(node, style);
        }
    }

    private void UpdateTargetLabel()
    {
        _target.Text = CurrentScope switch
        {
            Scope.Terrain => "目标：地形",
            Scope.Selected when _selectedNode is not null => $"目标：{_selectedNode.Name}",
            Scope.Selected => "目标：已选物体（先点选）",
            _ => "目标：全部角色",
        };
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

    private ColorPickerButton AddColor(VBoxContainer box, string title, Color color)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = title, CustomMinimumSize = new Vector2(86, 0) });
        var picker = new ColorPickerButton
        {
            Color = color,
            CustomMinimumSize = new Vector2(140, 28),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        picker.ColorChanged += _ => Push();
        row.AddChild(picker);
        box.AddChild(row);
        return picker;
    }

    private static void CopyInto(ToonStyle dst, ToonStyle src)
    {
        dst.Bands = src.Bands;
        dst.Rim = src.Rim;
        dst.ShadeMin = src.ShadeMin;
        dst.Fill = src.Fill;
        dst.ShadowTint = src.ShadowTint;
        dst.OutlineWidth = src.OutlineWidth;
        dst.OutlineColor = src.OutlineColor;
    }

    private static string Format(float v) => v >= 2 ? v.ToString("0") : v.ToString("0.00");
}
