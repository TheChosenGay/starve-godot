using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

public enum ShaderFxKind
{
    None,
    WillowFluff,
    AlchemyBounce,
}

/// <summary>光照沙盘右下：点选物体，套 shader 效果并调参。</summary>
public partial class ShaderFxPanel : CanvasLayer
{
    public event Action? Changed;

    public Node3D? Target { get; private set; }
    public ShaderFxKind Kind { get; private set; } = ShaderFxKind.None;
    public bool Loop { get; private set; } = true;
    public WillowFluffStyle Willow { get; } = new();

    private bool _syncing;
    private Label? _target;
    private PanelContainer _frame = null!;
    private VBoxContainer _willowBox = null!;
    private readonly List<Button> _kindBtns = [];
    private readonly List<(HSlider Slider, Label Num, Func<float> Get)> _sliders = [];
    private readonly List<(ColorPickerButton Picker, Func<Color> Get)> _colors = [];
    private CheckButton _loop = null!;

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
        _frame.OffsetLeft = -330;
        _frame.OffsetTop = -420;
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
            Text = "左键点场景物体，再选效果。柳絮从表面取色，再和统一色混合。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _target = new Label { Text = "目标：点一个物体" };
        box.AddChild(_target);

        var kinds = new HBoxContainer();
        AddKind(kinds, "无", ShaderFxKind.None);
        AddKind(kinds, "柳絮", ShaderFxKind.WillowFluff);
        AddKind(kinds, "抖动", ShaderFxKind.AlchemyBounce);
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

        _willowBox = new VBoxContainer();
        box.AddChild(_willowBox);
        _willowBox.AddChild(new Label { Text = "柳絮 / 水网上浮" });
        AddSlider(_willowBox, "混合", 0, 1, 0.02f, () => Willow.ColorMix, v => Willow.ColorMix = v);
        AddColor(_willowBox, "统一色", () => Willow.Tint, c => Willow.Tint = c);
        AddSlider(_willowBox, "升幅", 0.2f, 3.2f, 0.02f, () => Willow.Lift, v => Willow.Lift = v);
        AddSlider(_willowBox, "晃动", 0, 1.2f, 0.02f, () => Willow.Sway, v => Willow.Sway = v);
        AddSlider(_willowBox, "片大小", 0.02f, 0.16f, 0.005f, () => Willow.Size, v => Willow.Size = v);
        AddSlider(_willowBox, "辉光", 0.15f, 2f, 0.02f, () => Willow.Glow, v => Willow.Glow = v);
        AddSlider(_willowBox, "时长", 0.6f, 5f, 0.05f, () => Willow.Duration, v => Willow.Duration = v);
        AddSlider(_willowBox, "数量", 16, 240, 4, () => Willow.Count, v => Willow.Count = Mathf.RoundToInt(v));
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
            if (WillowFluffFx.Find(node) is { } fluff)
            {
                CopyWillow(fluff.Style);
                Loop = fluff.Loop;
            }
        }
        else if (Kind != ShaderFxKind.None)
        {
            ApplyTo(node);
        }

        _syncing = true;
        _loop.ButtonPressed = Loop;
        _loop.Text = Loop ? "循环播放（已开）" : "循环播放";
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
        WillowFluffFx.Detach(host);
        AlchemyBounceFx.Detach(host);
        switch (Kind)
        {
            case ShaderFxKind.WillowFluff:
                WillowFluffFx.PlayOn(host, Willow, Loop);
                break;
            case ShaderFxKind.AlchemyBounce:
                AlchemyBounceFx.Attach(host, Loop);
                break;
        }
    }

    private void Replay()
    {
        if (Target is null) return;
        if (Kind == ShaderFxKind.WillowFluff)
        {
            if (WillowFluffFx.Find(Target) is { } fluff)
                fluff.Replay();
            else
                ApplyTo(Target);
            return;
        }

        ApplyTo(Target);
    }

    private void Push()
    {
        if (Target is null) return;
        if (Kind == ShaderFxKind.WillowFluff && WillowFluffFx.Find(Target) is { } fluff)
        {
            fluff.ApplyStyle(Willow);
            if (fluff.Loop != Loop)
                WillowFluffFx.PlayOn(Target, Willow, Loop);
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
        if (_willowBox is not null)
            _willowBox.Visible = Kind == ShaderFxKind.WillowFluff;
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

    private void CopyWillow(WillowFluffStyle src)
    {
        Willow.ColorMix = src.ColorMix;
        Willow.Tint = src.Tint;
        Willow.Lift = src.Lift;
        Willow.Sway = src.Sway;
        Willow.Size = src.Size;
        Willow.Glow = src.Glow;
        Willow.Duration = src.Duration;
        Willow.Count = src.Count;
    }

    private static ShaderFxKind KindOn(Node3D host)
    {
        if (WillowFluffFx.Find(host) is not null) return ShaderFxKind.WillowFluff;
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
