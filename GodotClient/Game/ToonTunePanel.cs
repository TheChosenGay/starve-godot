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
    private readonly Label _target = new() { Text = "目标：已选物体（先点选）" };
    private readonly CheckButton _hideOutline = new() { Text = "关掉轮廓（地形无效）" };
    private readonly OptionButton _kind = new();
    private Node3D? _selectedNode;
    private ulong _selectedId;
    private bool _syncing;
    private VBoxContainer _bandsBox = null!;
    private VBoxContainer _celBox = null!;
    private HSlider? _bands;
    private HSlider? _shadeMin;
    private HSlider? _fill;
    private HSlider? _rim;
    private HSlider? _outline;
    private ColorPickerButton? _shadow;
    private ColorPickerButton? _outlineColor;
    private HSlider? _threshold;
    private HSlider? _shadowStrength;
    private HSlider? _specThreshold;
    private HSlider? _specStrength;
    private ColorPickerButton? _specColor;
    private HSlider? _rimWidth;
    private HSlider? _rimPower;
    private HSlider? _rimStrength;
    private ColorPickerButton? _rimColor;
    private CheckButton _rimLitOnly = new() { Text = "边缘光只在受光面" };

    public override void _Ready()
    {
        Name = "ToonTune";
        SetAnchorsPreset(LayoutPreset.TopRight);
        OffsetLeft = -312;
        OffsetTop = 12;
        OffsetRight = -12;
        OffsetBottom = 640;
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
            Text = "范围：已选物体 / 全部已套用 Toon 的角色 / 地形。点模型后点「套用 Toon」。两套 shader 可切换，不会删掉原来的色阶 Toon。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _kind.AddItem("色阶 Toon（现有）", (int)ToonShaderKind.Bands);
        _kind.AddItem("Cel Toon（gameidea）", (int)ToonShaderKind.Cel);
        _kind.Selected = (int)ToonShaderKind.Bands;
        _kind.ItemSelected += OnKindSelected;
        box.AddChild(_kind);
        _scope.AddItem("全部角色", (int)Scope.Actors);
        _scope.AddItem("地形", (int)Scope.Terrain);
        _scope.AddItem("已选物体", (int)Scope.Selected);
        _scope.Selected = (int)Scope.Selected;
        _scope.ItemSelected += _ =>
        {
            _kind.Disabled = CurrentScope == Scope.Terrain;
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

        var toonRow = new HBoxContainer();
        var enableToon = new Button { Text = "套用 Toon", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var disableToon = new Button { Text = "去掉 Toon", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        enableToon.Pressed += EnableSelectedToon;
        disableToon.Pressed += DisableSelectedToon;
        toonRow.AddChild(enableToon);
        toonRow.AddChild(disableToon);
        box.AddChild(toonRow);

        _bandsBox = new VBoxContainer();
        box.AddChild(_bandsBox);
        _bands = AddSlider(_bandsBox, "色阶级数", 2, 6, 1, ToonMaterials.ActorDefaults.Bands);
        _shadeMin = AddSlider(_bandsBox, "暗部亮度", 0, 1, 0.01f, ToonMaterials.ActorDefaults.ShadeMin);
        _fill = AddSlider(_bandsBox, "填充光", 0, 0.6f, 0.01f, ToonMaterials.ActorDefaults.Fill);
        _rim = AddSlider(_bandsBox, "边缘光", 0, 1, 0.01f, ToonMaterials.ActorDefaults.Rim);
        _shadow = AddColor(_bandsBox, "阴影色", ToonMaterials.ActorDefaults.ShadowTint);

        _celBox = new VBoxContainer();
        box.AddChild(_celBox);
        var celHint = new Label
        {
            Text = "硬边阈值 + 高光 + 边缘光。阈值把 Lambert 切成亮/暗两档。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _celBox.AddChild(celHint);
        _threshold = AddSlider(_celBox, "明暗阈值", 0, 1, 0.01f, 0.5f);
        _shadowStrength = AddSlider(_celBox, "阴影强度", 0, 1, 0.01f, 0.5f);
        _specThreshold = AddSlider(_celBox, "高光阈值", 0.85f, 1, 0.005f, 0.99f);
        _specStrength = AddSlider(_celBox, "高光强度", 0, 4, 0.05f, 2f);
        _specColor = AddColor(_celBox, "高光色", Colors.White);
        _rimWidth = AddSlider(_celBox, "边缘宽度", 0.2f, 4, 0.05f, 2f);
        _rimPower = AddSlider(_celBox, "边缘锐度", 1, 8, 0.1f, 4f);
        _rimStrength = AddSlider(_celBox, "边缘强度", 0, 2, 0.02f, 1f);
        _rimColor = AddColor(_celBox, "边缘色", Colors.White);
        _rimLitOnly.Toggled += _ => Push();
        _celBox.AddChild(_rimLitOnly);

        _outline = AddSlider(box, "轮廓宽度", 0, 0.08f, 0.001f, ToonMaterials.ActorDefaults.OutlineWidth);
        _outlineColor = AddColor(box, "轮廓色", ToonMaterials.ActorDefaults.OutlineColor);
        box.AddChild(_hideOutline);
        _hideOutline.Toggled += _ => Push();
        UpdateSliderVisibility();

        var reset = new Button { Text = "恢复当前目标默认" };
        reset.Pressed += ResetCurrent;
        box.AddChild(reset);
        box.AddChild(new Label
        {
            Text = "点选开启时左键选模型（脚底黄环=已选），不会攻击。Toon 只作用在点过「套用 Toon」的物体上。",
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
        foreach (var mat in ToonMaterials.CollectActorMaterials(node))
        {
            _overrides[id].Kind = ToonMaterials.ReadKind(mat);
            break;
        }
        LoadSliders();
        UpdateTargetLabel();
        if (ToonMaterials.HasActorToon(node))
            Push();
    }

    private void OnKindSelected(long index)
    {
        if (CurrentScope == Scope.Terrain) return;
        var kind = (ToonShaderKind)(int)index;
        CurrentStyle().Kind = kind;
        ToonMaterials.CreateKind = kind;
        UpdateSliderVisibility();
        Push();
    }

    private void UpdateSliderVisibility()
    {
        var cel = CurrentScope != Scope.Terrain && (ToonShaderKind)_kind.Selected == ToonShaderKind.Cel;
        _bandsBox.Visible = !cel;
        _celBox.Visible = cel;
    }

    private void EnableSelectedToon()
    {
        if (_selectedNode is null) return;
        _scope.Selected = (int)Scope.Selected;
        ToonMaterials.CreateKind = CurrentStyle().Kind;
        ToonMaterials.EnableOn(_selectedNode);
        Push();
        UpdateTargetLabel();
    }

    private void DisableSelectedToon()
    {
        if (_selectedNode is null) return;
        ToonMaterials.DisableOn(_selectedNode);
        UpdateTargetLabel();
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
            var kind = CurrentStyle().Kind;
            _overrides[_selectedId] = kind == ToonShaderKind.Cel
                ? ToonStyle.CelDefaults()
                : ToonMaterials.ActorDefaults.Clone();
            _overrides[_selectedId].Kind = kind;
        }
        else
        {
            var kind = ToonMaterials.ActorDefaults.Kind;
            CopyInto(ToonMaterials.ActorDefaults, kind == ToonShaderKind.Cel ? ToonStyle.CelDefaults() : new ToonStyle());
            ToonMaterials.ActorDefaults.Kind = kind;
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
        _kind.Selected = (int)s.Kind;
        if (_bands is not null) _bands.Value = s.Bands;
        if (_shadeMin is not null) _shadeMin.Value = s.ShadeMin;
        if (_fill is not null) _fill.Value = s.Fill;
        if (_rim is not null) _rim.Value = s.Rim;
        if (_outline is not null) _outline.Value = s.OutlineWidth;
        if (_shadow is not null) _shadow.Color = s.ShadowTint;
        if (_outlineColor is not null) _outlineColor.Color = s.OutlineColor;
        if (_threshold is not null) _threshold.Value = s.DiffuseThreshold;
        if (_shadowStrength is not null) _shadowStrength.Value = s.ShadowStrength;
        if (_specThreshold is not null) _specThreshold.Value = s.SpecularThreshold;
        if (_specStrength is not null) _specStrength.Value = s.SpecularStrength;
        if (_specColor is not null) _specColor.Color = s.SpecularColor;
        if (_rimWidth is not null) _rimWidth.Value = s.RimWidth;
        if (_rimPower is not null) _rimPower.Value = s.RimPower;
        if (_rimStrength is not null) _rimStrength.Value = s.RimStrength;
        if (_rimColor is not null) _rimColor.Color = s.RimColor;
        _rimLitOnly.ButtonPressed = s.RimLitOnly;
        _hideOutline.ButtonPressed = s.OutlineWidth <= 1e-4f;
        _syncing = false;
        UpdateSliderVisibility();
        UpdateTargetLabel();
    }

    private void ReadSliders(ToonStyle s)
    {
        if (CurrentScope != Scope.Terrain)
            s.Kind = (ToonShaderKind)_kind.Selected;
        if (_bands is not null) s.Bands = (float)Math.Round(_bands.Value);
        if (_shadeMin is not null) s.ShadeMin = (float)_shadeMin.Value;
        if (_fill is not null) s.Fill = (float)_fill.Value;
        if (_rim is not null) s.Rim = (float)_rim.Value;
        if (_outline is not null) s.OutlineWidth = _hideOutline.ButtonPressed ? 0f : (float)_outline.Value;
        if (_shadow is not null) s.ShadowTint = _shadow.Color;
        if (_outlineColor is not null) s.OutlineColor = _outlineColor.Color;
        if (_threshold is not null) s.DiffuseThreshold = (float)_threshold.Value;
        if (_shadowStrength is not null) s.ShadowStrength = (float)_shadowStrength.Value;
        if (_specThreshold is not null) s.SpecularThreshold = (float)_specThreshold.Value;
        if (_specStrength is not null) s.SpecularStrength = (float)_specStrength.Value;
        if (_specColor is not null) s.SpecularColor = _specColor.Color;
        if (_rimWidth is not null) s.RimWidth = (float)_rimWidth.Value;
        if (_rimPower is not null) s.RimPower = (float)_rimPower.Value;
        if (_rimStrength is not null) s.RimStrength = (float)_rimStrength.Value;
        if (_rimColor is not null) s.RimColor = _rimColor.Color;
        s.RimLitOnly = _rimLitOnly.ButtonPressed;
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
        ToonMaterials.CreateKind = style.Kind;
        if (CurrentScope == Scope.Selected)
        {
            if (_selectedNode is not null)
                ToonMaterials.ApplyStyleToTree(_selectedNode, style);
            return;
        }
        if (CollectActors is null) return;
        foreach (var node in CollectActors())
        {
            if (!ToonMaterials.HasActorToon(node)) continue;
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
            Scope.Selected when _selectedNode is not null =>
                $"目标：{_selectedNode.Name}" + (ToonMaterials.HasActorToon(_selectedNode)
                    ? (CurrentStyle().Kind == ToonShaderKind.Cel ? "（Cel Toon）" : "（色阶 Toon）")
                    : "（未 Toon）"),
            Scope.Selected => "目标：已选物体（先点选）",
            _ => "目标：已套用 Toon 的角色",
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
        dst.Kind = src.Kind;
        dst.Bands = src.Bands;
        dst.Rim = src.Rim;
        dst.ShadeMin = src.ShadeMin;
        dst.Fill = src.Fill;
        dst.ShadowTint = src.ShadowTint;
        dst.OutlineWidth = src.OutlineWidth;
        dst.OutlineColor = src.OutlineColor;
        dst.DiffuseThreshold = src.DiffuseThreshold;
        dst.ShadowStrength = src.ShadowStrength;
        dst.SpecularThreshold = src.SpecularThreshold;
        dst.SpecularStrength = src.SpecularStrength;
        dst.SpecularColor = src.SpecularColor;
        dst.RimWidth = src.RimWidth;
        dst.RimPower = src.RimPower;
        dst.RimStrength = src.RimStrength;
        dst.RimColor = src.RimColor;
        dst.RimLitOnly = src.RimLitOnly;
    }

    private static string Format(float v) => v >= 2 ? v.ToString("0") : v.ToString("0.00");
}
