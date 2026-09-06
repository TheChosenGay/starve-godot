using System;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// F1/F2/F3 共用：标题栏折叠/关闭。折叠时把 Body 卸出树，避免隐藏控件仍占 draw call。
/// </summary>
internal sealed class DebugPanelChrome
{
    public enum Corner
    {
        TopLeft,
        TopRight,
        BottomLeft,
    }

    private readonly Control _host;
    private readonly Corner _corner;
    private readonly Button _collapse;
    private readonly VBoxContainer _col;
    private readonly float _margin;

    public PanelContainer Frame { get; }
    public VBoxContainer Body { get; }

    public bool Collapsed => Body.GetParent() is null;

    public DebugPanelChrome(
        Control host,
        string title,
        Corner corner,
        bool startCollapsed = false,
        float margin = 12)
    {
        _host = host;
        _corner = corner;
        _margin = margin;
        host.MouseFilter = Control.MouseFilterEnum.Ignore;
        host.Theme ??= HudTheme.CreateDebug();

        Frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        host.AddChild(Frame);

        _col = new VBoxContainer();
        _col.AddThemeConstantOverride("separation", 4);
        var pad = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 6);
        pad.AddChild(_col);
        Frame.AddChild(pad);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 6);
        header.AddChild(new Label
        {
            Text = title,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        _collapse = new Button
        {
            Text = startCollapsed ? "展开" : "折叠",
            FocusMode = Control.FocusModeEnum.None,
        };
        var close = new Button
        {
            Text = "关闭",
            FocusMode = Control.FocusModeEnum.None,
        };
        _collapse.Pressed += () => SetCollapsed(!Collapsed);
        close.Pressed += () => host.Visible = false;
        header.AddChild(_collapse);
        header.AddChild(close);
        _col.AddChild(header);

        Body = new VBoxContainer();
        Body.AddThemeConstantOverride("separation", 4);
        if (!startCollapsed)
            _col.AddChild(Body);

        Fit();
        Callable.From(Fit).CallDeferred();
    }

    public void SetCollapsed(bool collapsed)
    {
        if (collapsed)
        {
            if (Body.GetParent() is not null)
                _col.RemoveChild(Body);
        }
        else if (Body.GetParent() is null)
        {
            _col.AddChild(Body);
        }
        _collapse.Text = collapsed ? "展开" : "折叠";
        Fit();
        Callable.From(Fit).CallDeferred();
    }

    public void ToggleVisible()
    {
        if (_host.Visible)
        {
            _host.Visible = false;
            return;
        }
        _host.Visible = true;
        SetCollapsed(false);
    }

    public void Fit()
    {
        Frame.ResetSize();
        var size = Frame.GetCombinedMinimumSize();
        if (size.X < 160) size.X = 160;
        if (size.Y < 32) size.Y = 32;
        var m = _margin;
        switch (_corner)
        {
            case Corner.TopRight:
                _host.SetAnchorsPreset(Control.LayoutPreset.TopRight);
                _host.OffsetLeft = -m - size.X;
                _host.OffsetTop = m;
                _host.OffsetRight = -m;
                _host.OffsetBottom = m + size.Y;
                break;
            case Corner.TopLeft:
                _host.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                _host.OffsetLeft = m;
                _host.OffsetTop = m;
                _host.OffsetRight = m + size.X;
                _host.OffsetBottom = m + size.Y;
                break;
            default:
                _host.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
                _host.OffsetLeft = m;
                _host.OffsetTop = -m - size.Y;
                _host.OffsetRight = m + size.X;
                _host.OffsetBottom = -m;
                break;
        }

        Frame.SetAnchorsPreset(Control.LayoutPreset.FullRect);
    }
}

/// <summary>色块按钮。点开才建一个共享 ColorPicker，避免每条滑条自带一整棵取色器。</summary>
internal sealed partial class DebugColorSwatch : Button
{
    private static Window? _window;
    private static ColorPicker? _picker;
    private static DebugColorSwatch? _active;

    private Color _color = Colors.White;
    private ColorRect _swatch = null!;

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            if (_swatch is not null)
                _swatch.Color = value;
        }
    }

    public event Action<Color>? ColorChanged;

    public override void _Ready()
    {
        FocusMode = FocusModeEnum.None;
        CustomMinimumSize = new Vector2(140, 26);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _swatch = new ColorRect
        {
            Color = _color,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_swatch);
        _swatch.SetAnchorsPreset(LayoutPreset.FullRect);
        _swatch.OffsetLeft = 8;
        _swatch.OffsetTop = 5;
        _swatch.OffsetRight = -8;
        _swatch.OffsetBottom = -5;
        Pressed += OpenPicker;
    }

    private void OpenPicker()
    {
        EnsureWindow();
        _active = this;
        _picker!.Color = _color;
        _window!.PopupCentered(new Vector2I(300, 380));
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;
        _picker = new ColorPicker
        {
            ColorModesVisible = false,
            PresetsVisible = false,
            SamplerVisible = false,
            SlidersVisible = true,
        };
        _picker.ColorChanged += color =>
        {
            if (_active is null) return;
            _active.Color = color;
            _active.ColorChanged?.Invoke(color);
        };
        _window = new Window
        {
            Title = "颜色",
            Transient = true,
            Exclusive = true,
            Unresizable = true,
            WrapControls = true,
        };
        _window.CloseRequested += () => _window.Hide();
        var pad = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 8);
        pad.AddChild(_picker);
        _window.AddChild(pad);
        GetTree().Root.AddChild(_window);
    }
}
