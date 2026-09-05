using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>光照沙盘左侧：选一簇火，改颜色和塑形/噪音参数。</summary>
public partial class FireTunePanel : CanvasLayer
{
    public int Selected { get; private set; }
    public bool ApplyAll { get; private set; }

    private readonly List<FireFlame3D> _flames = [];
    private bool _syncing;
    private Label? _target;
    private GridContainer _picks = null!;
    private PanelContainer _frame = null!;
    private readonly List<(HSlider Slider, Label Num, Func<float> Get)> _sliders = [];
    private readonly List<(ColorPickerButton Picker, Func<Color> Get)> _colors = [];

    public override void _Ready()
    {
        Name = "FireTunePanel";
        var ui = new Control
        {
            Name = "Ui",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        ui.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(ui);

        _frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        _frame.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _frame.OffsetLeft = 12;
        _frame.OffsetTop = 12;
        _frame.OffsetRight = 336;
        _frame.OffsetBottom = 860;
        _frame.Theme = HudTheme.Create();
        ui.AddChild(_frame);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _frame.AddChild(scroll);
        var box = new VBoxContainer();
        scroll.AddChild(box);
        box.AddThemeConstantOverride("separation", 5);

        box.AddChild(new Label { Text = "火焰调参" });
        box.AddChild(new Label
        {
            Text = "点场景里的火，或点下面名字。强度 = 水滴外形 − 噪音，不是噪音原值。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _target = new Label { Text = "目标：—" };
        box.AddChild(_target);

        _picks = new GridContainer { Name = "Picks", Columns = 3 };
        box.AddChild(_picks);
        var all = new CheckButton { Text = "同时改全部" };
        all.Toggled += on =>
        {
            ApplyAll = on;
            all.Text = on ? "同时改全部（已开）" : "同时改全部";
        };
        box.AddChild(all);

        box.AddChild(new Label { Text = "颜色（染色=0 用原文 RGB 幂次）" });
        AddColor(box, "芯", () => Current.ColorCore, c => Current.ColorCore = c);
        AddColor(box, "中", () => Current.ColorMid, c => Current.ColorMid = c);
        AddColor(box, "边", () => Current.ColorEdge, c => Current.ColorEdge = c);
        AddSlider(box, "染色", 0, 1, 0.02f, () => Current.ColorMix, v => Current.ColorMix = v);

        box.AddChild(new Label { Text = "外形" });
        AddSlider(box, "细节", 0.5f, 7, 0.05f, () => Current.DetailStrength, v => Current.DetailStrength = v);
        AddSlider(box, "滚动", 0, 4, 0.05f, () => Current.ScrollSpeed, v => Current.ScrollSpeed = v);
        AddSlider(box, "高度", 0.3f, 2.2f, 0.02f, () => Current.FireHeight, v => Current.FireHeight = v);
        AddSlider(box, "外形", 0.2f, 3.2f, 0.02f, () => Current.FireShape, v => Current.FireShape = v);
        AddSlider(box, "粗细", 0.15f, 1.4f, 0.02f, () => Current.FireThickness, v => Current.FireThickness = v);
        AddSlider(box, "锐度", 0.2f, 2.2f, 0.02f, () => Current.FireSharpness, v => Current.FireSharpness = v);
        AddSlider(box, "强度", 0.2f, 2, 0.02f, () => Current.Intensity, v => Current.Intensity = v);
        AddSlider(box, "片宽", 0.35f, 2, 0.02f, () => Current.MeshWidth, v => Current.MeshWidth = v);
        AddSlider(box, "片高", 0.6f, 2.6f, 0.02f, () => Current.MeshHeight, v => Current.MeshHeight = v);

        box.AddChild(new Label { Text = "噪音 fBm" });
        AddSlider(box, "层数", 1, 8, 1, () => Current.NoiseOctaves, v => Current.NoiseOctaves = Mathf.RoundToInt(v));
        AddSlider(box, "间隙", 1.2f, 4.5f, 0.05f, () => Current.NoiseLacunarity, v => Current.NoiseLacunarity = v);
        AddSlider(box, "增益", 0.2f, 0.9f, 0.02f, () => Current.NoiseGain, v => Current.NoiseGain = v);
        AddSlider(box, "振幅", 0.3f, 2, 0.02f, () => Current.NoiseAmplitude, v => Current.NoiseAmplitude = v);
        AddSlider(box, "频率", 0.4f, 3.2f, 0.05f, () => Current.NoiseFrequency, v => Current.NoiseFrequency = v);

        box.AddChild(new Label { Text = "自带点光（场景火堆仍用右侧）" });
        AddSlider(box, "能量", 0, 6, 0.05f, () => Current.LightEnergy, v => Current.LightEnergy = v);
        AddSlider(box, "范围", 1, 12, 0.1f, () => Current.LightRange, v => Current.LightRange = v);
        AddColor(box, "光色", () => Current.LightColor, c => Current.LightColor = c);

        var reset = new Button { Text = "恢复此火的预设" };
        reset.Pressed += ResetSelected;
        box.AddChild(reset);
    }

    public void Bind(IReadOnlyList<FireFlame3D> flames)
    {
        _flames.Clear();
        _flames.AddRange(flames);
        foreach (var child in _picks.GetChildren())
            child.QueueFree();
        for (var i = 0; i < _flames.Count; i++)
        {
            var idx = i;
            var btn = new Button
            {
                Text = _flames[i].Style.Label,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            btn.Pressed += () => Select(idx);
            _picks.AddChild(btn);
        }
        if (_flames.Count > 0)
            Select(0);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _flames.Count) return;
        Selected = index;
        for (var i = 0; i < _flames.Count; i++)
            _flames[i].SetSelected(i == index);
        if (_target is not null)
            _target.Text = $"目标：{_flames[index].Style.Label}";
        Reload();
    }

    public bool Hits(Vector2 screen)
    {
        return _frame is not null && _frame.GetGlobalRect().HasPoint(screen);
    }

    private FireStyle Current =>
        _flames.Count == 0 ? FireStyle.Campfire() : _flames[Selected].Style;

    private void ResetSelected()
    {
        if (_flames.Count == 0) return;
        var id = _flames[Selected].Style.Id;
        ApplyToTargets(FireStyle.FromId(id));
        Reload();
    }

    private void Push()
    {
        if (_syncing || _flames.Count == 0) return;
        ApplyToTargets(_flames[Selected].Style.Clone());
    }

    private void ApplyToTargets(FireStyle style)
    {
        if (ApplyAll)
        {
            foreach (var flame in _flames)
            {
                var copy = style.Clone();
                copy.Id = flame.Style.Id;
                copy.Label = flame.Style.Label;
                flame.ApplyStyle(copy);
            }
            return;
        }
        var keep = _flames[Selected].Style;
        style.Id = keep.Id;
        style.Label = keep.Label;
        _flames[Selected].ApplyStyle(style);
    }

    private void Reload()
    {
        _syncing = true;
        foreach (var (slider, num, get) in _sliders)
        {
            var v = get();
            slider.SetValueNoSignal(v);
            num.Text = Format(v);
        }
        foreach (var (picker, get) in _colors)
            picker.Color = get();
        _syncing = false;
    }

    private void AddSlider(VBoxContainer box, string title, float min, float max, float step, Func<float> get, Action<float> set)
    {
        var value = get();
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = title, CustomMinimumSize = new Vector2(44, 0) });
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        var num = new Label { Text = Format(value), CustomMinimumSize = new Vector2(36, 0) };
        slider.ValueChanged += v =>
        {
            num.Text = Format((float)v);
            if (_syncing) return;
            set((float)v);
            Push();
        };
        _sliders.Add((slider, num, get));
        row.AddChild(slider);
        row.AddChild(num);
        box.AddChild(row);
    }

    private void AddColor(VBoxContainer box, string title, Func<Color> get, Action<Color> set)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = title, CustomMinimumSize = new Vector2(44, 0) });
        var picker = new ColorPickerButton
        {
            Color = get(),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 28),
        };
        picker.ColorChanged += c =>
        {
            if (_syncing) return;
            set(c);
            Push();
        };
        _colors.Add((picker, get));
        row.AddChild(picker);
        box.AddChild(row);
    }

    private static string Format(float v) => v >= 10 ? v.ToString("0") : v.ToString("0.00");
}
