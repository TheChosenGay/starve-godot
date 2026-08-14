using System;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 游戏 HUD（阶段 3 简化版）：状态栏 + 日志 + 操作按钮。
/// 纯 UI 层：不碰协议，按钮事件由 GameRoot 接线到命令层。
/// </summary>
public partial class Hud : Control
{
    public event Action? GatherPressed;
    public event Action? AttackPressed;
    public event Action? PickupPressed;
    public event Action? DemolishPressed;
    public event Action<int>? BuildPressed;

    private Label? _status;
    private RichTextLabel? _log;

    public override void _Ready()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _status = new Label
        {
            Position = new Vector2(16, 16),
            Modulate = new Color(1f, 0.95f, 0.8f),
        };
        AddChild(_status);

        _log = new RichTextLabel
        {
            Position = new Vector2(16, 540),
            Size = new Vector2(480, 230),
            BbcodeEnabled = true,
            Modulate = new Color(0.95f, 0.9f, 0.8f, 0.9f),
        };
        AddChild(_log);

        var bar = new HBoxContainer
        {
            Position = new Vector2(16, 500),
        };
        bar.AddChild(MakeButton("采集", () => GatherPressed?.Invoke()));
        bar.AddChild(MakeButton("攻击", () => AttackPressed?.Invoke()));
        bar.AddChild(MakeButton("拾取", () => PickupPressed?.Invoke()));
        bar.AddChild(MakeButton("拆除", () => DemolishPressed?.Invoke()));
        bar.AddChild(MakeButton("建火堆", () => BuildPressed?.Invoke(1)));
        bar.AddChild(MakeButton("建木墙", () => BuildPressed?.Invoke(2)));
        AddChild(bar);
    }

    public void SetStatus(string text)
    {
        if (_status is not null) _status.Text = text;
    }

    public void Log(string line)
    {
        if (_log is null) return;
        _log.AppendText(line + "\n");
    }

    private static Button MakeButton(string text, Action onPressed)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(72, 34) };
        b.Pressed += onPressed;
        return b;
    }
}
