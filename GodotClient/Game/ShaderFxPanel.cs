using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

public enum ShaderFxKind
{
    None,
    AlchemyBounce,
    LiquidRise,
}

/// <summary>光照沙盘右下：点选物体，套 shader 效果并调参。</summary>
public partial class ShaderFxPanel : CanvasLayer
{
    public event Action? Changed;

    public Node3D? Target { get; private set; }
    public ShaderFxKind Kind { get; private set; } = ShaderFxKind.None;
    public bool Loop { get; private set; } = true;
    public LiquidRiseStyle Liquid { get; } = new();

    private bool _syncing;
    private Label? _target;
    private PanelContainer _frame = null!;
    private VBoxContainer _liquidBox = null!;
    private readonly List<Button> _kindBtns = [];
    private readonly List<(HSlider Slider, Label Num, Func<float> Get)> _sliders = [];
    private readonly List<(ColorPickerButton Picker, Func<Color> Get)> _colors = [];
    private CheckButton _loop = null!;
    private CheckButton _bake = null!;

    public override void _Ready()
    {
        Name = "ShaderFxPanel";
        var ui = new Control
        {
            Name = "Ui",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        ui.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(ui);

        _frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        _frame.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _frame.OffsetLeft = -348;
        _frame.OffsetTop = -440;
        _frame.OffsetRight = -12;
        _frame.OffsetBottom = -12;
        _frame.Theme = HudTheme.Create();
        ui.AddChild(_frame);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _frame.AddChild(scroll);
        var box = new VBoxContainer();
        scroll.AddChild(box);
        box.AddThemeConstantOverride("separation", 5);

        box.AddChild(new Label { Text = "Shader 效果" });
        box.AddChild(new Label
        {
            Text = "左键点场景物体，再选效果。有骨骼的角色可开「静态网格」，从烤好的姿势上往上飘。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _target = new Label { Text = "目标：点一个物体" };
        box.AddChild(_target);

        var kinds = new HBoxContainer();
        AddKind(kinds, "无", ShaderFxKind.None);
        AddKind(kinds, "抖动", ShaderFxKind.AlchemyBounce);
        AddKind(kinds, "飘液", ShaderFxKind.LiquidRise);
        box.AddChild(kinds);

        _loop = new CheckButton { Text = "循环播放", ButtonPressed = true };
        _loop.Toggled += on =>
        {
            Loop = on;
            _loop.Text = on ? "循环播放（已开）" : "循环播放";
            if (!_syncing) Push();
        };
        box.AddChild(_loop);

        var replay = new Button { Text = "再播一次" };
        replay.Pressed += Replay;
        box.AddChild(replay);

        _liquidBox = new VBoxContainer();
        box.AddChild(_liquidBox);
        _liquidBox.AddChild(new Label { Text = "飘液 / 分块覆膜" });
        AddSlider(_liquidBox, "混合", 0, 1, 0.02f, () => Liquid.ColorMix, v => Liquid.ColorMix = v);
        AddColor(_liquidBox, "颜色", () => Liquid.Tint, c => Liquid.Tint = c);
        AddSlider(_liquidBox, "覆膜", 0.01f, 0.14f, 0.005f, () => Liquid.Thickness, v => Liquid.Thickness = v);
        AddSlider(_liquidBox, "升空", 0.3f, 2.2f, 0.02f, () => Liquid.Rise, v => Liquid.Rise = v);
        AddSlider(_liquidBox, "摇曳", 0, 1.4f, 0.02f, () => Liquid.Swirl, v => Liquid.Swirl = v);
        AddSlider(_liquidBox, "窜动", 0, 2.4f, 0.05f, () => Liquid.Ripple, v => Liquid.Ripple = v);
        AddSlider(_liquidBox, "分块", 1.2f, 10f, 0.1f, () => Liquid.Dissolve, v => Liquid.Dissolve = v);
        AddSlider(_liquidBox, "辉光", 0.15f, 2f, 0.02f, () => Liquid.Glow, v => Liquid.Glow = v);
        AddSlider(_liquidBox, "时长", 0.6f, 6f, 0.05f, () => Liquid.Duration, v => Liquid.Duration = v);
        _bake = new CheckButton { Text = "静态网格（骨骼）" };
        _bake.Toggled += on =>
        {
            Liquid.BakeStatic = on;
            _bake.Text = on ? "静态网格（骨骼，已开）" : "静态网格（骨骼）";
            if (!_syncing) Push();
        };
        _liquidBox.AddChild(_bake);
        RefreshKindUi();
    }

    public void Select(Node3D? node, string? label = null)
    {
        Target = node;
        if (_target is not null)
            _target.Text = node is null ? "目标：点一个物体" : $"目标：{label ?? node.Name}";
        if (node is null)
        {
            RefreshKindUi();
            return;
        }

        var existing = KindOn(node);
        if (existing != ShaderFxKind.None)
        {
            Kind = existing;
            if (LiquidRiseFx.Find(node) is { } liquid)
            {
                CopyLiquid(liquid.Style);
                Loop = liquid.Loop;
            }
        }
        else if (Kind != ShaderFxKind.None)
        {
            ApplyTo(node);
        }

        _syncing = true;
        _loop.ButtonPressed = Loop;
        _loop.Text = Loop ? "循环播放（已开）" : "循环播放";
        _bake.ButtonPressed = Liquid.BakeStatic;
        _bake.Text = Liquid.BakeStatic ? "静态网格（骨骼，已开）" : "静态网格（骨骼）";
        ReloadSliderValues();
        _syncing = false;
        RefreshKindUi();
    }

    public bool Hits(Vector2 screen) =>
        _frame is not null && _frame.GetGlobalRect().HasPoint(screen);

    public void ApplyTo(Node3D? host)
    {
        if (host is null || !GodotObject.IsInstanceValid(host))
            return;
        AlchemyBounceFx.Detach(host);
        LiquidRiseFx.Detach(host);
        switch (Kind)
        {
            case ShaderFxKind.AlchemyBounce:
                AlchemyBounceFx.Attach(host, Loop);
                break;
            case ShaderFxKind.LiquidRise:
                LiquidRiseFx.PlayOn(host, Liquid, Loop);
                break;
        }
    }

    private void Replay()
    {
        if (Target is null) return;
        if (Kind == ShaderFxKind.LiquidRise)
        {
            if (LiquidRiseFx.Find(Target) is { } liquid)
                liquid.Replay();
            else
                ApplyTo(Target);
            return;
        }

        ApplyTo(Target);
    }

    private void Push()
    {
        if (Target is null) return;
        if (Kind == ShaderFxKind.LiquidRise && LiquidRiseFx.Find(Target) is { } liquid)
        {
            if (liquid.Loop != Loop)
                liquid.SetLoop(Loop);
            liquid.ApplyStyle(Liquid);
            return;
        }

        ApplyTo(Target);
        Changed?.Invoke();
    }

    private void SetKind(ShaderFxKind kind)
    {
        Kind = kind;
        RefreshKindUi();
        if (!_syncing)
            Push();
    }

    private void RefreshKindUi()
    {
        if (_liquidBox is not null)
            _liquidBox.Visible = Kind == ShaderFxKind.LiquidRise;
        for (var i = 0; i < _kindBtns.Count; i++)
            _kindBtns[i].Disabled = i == (int)Kind;
    }

    private void AddKind(HBoxContainer row, string title, ShaderFxKind kind)
    {
        var btn = new Button { Text = title, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        btn.Pressed += () => SetKind(kind);
        _kindBtns.Add(btn);
        row.AddChild(btn);
    }

    private void CopyLiquid(LiquidRiseStyle src)
    {
        Liquid.ColorMix = src.ColorMix;
        Liquid.Tint = src.Tint;
        Liquid.Thickness = src.Thickness;
        Liquid.Rise = src.Rise;
        Liquid.Swirl = src.Swirl;
        Liquid.Ripple = src.Ripple;
        Liquid.Glow = src.Glow;
        Liquid.Dissolve = src.Dissolve;
        Liquid.Duration = src.Duration;
        Liquid.BakeStatic = src.BakeStatic;
    }

    private static ShaderFxKind KindOn(Node3D host)
    {
        if (LiquidRiseFx.Find(host) is not null) return ShaderFxKind.LiquidRise;
        if (host.GetNodeOrNull<AlchemyBounceFx>(AlchemyBounceFx.NodeName) is not null)
            return ShaderFxKind.AlchemyBounce;
        return ShaderFxKind.None;
    }

    private void ReloadSliderValues()
    {
        foreach (var (slider, num, get) in _sliders)
        {
            var v = get();
            slider.SetValueNoSignal(v);
            num.Text = Format(v);
        }
        foreach (var (picker, get) in _colors)
            picker.Color = get();
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
            EditAlpha = true,
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
