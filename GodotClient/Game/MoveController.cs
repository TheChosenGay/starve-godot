using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 移动输入：WASD/方向键按住即走、每 50ms 重复、松开发停。
/// 按键 → 世界方向的映射在 Core.MoveInput，这里只做输入采集。
/// </summary>
public partial class MoveController : Node
{
    /// <summary>方向变化回调（(0,0) = 停止）。</summary>
    public Action<(int Dx, int Dy)>? OnMove;

    private readonly HashSet<string> _held = new();
    private readonly Dictionary<string, long> _keyDownAt = new();
    private double _accum;
    private const long HoldStopMs = 220; // 与 web 端一致：短按视为单步，不取消队列

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || key.Echo) return;
        var name = OS.GetKeycodeString(key.Keycode);
        if (MoveInput.TryMap(name) is null) return;

        if (key.Pressed)
        {
            _held.Add(name);
            _keyDownAt[name] = (long)Time.GetTicksMsec();
            SendHeld();
        }
        else
        {
            _held.Remove(name);
            var heldMs = (long)Time.GetTicksMsec() - _keyDownAt.GetValueOrDefault(name);
            _keyDownAt.Remove(name);
            // 只有长按才发停止：短按的命令已入队，走一步后自然停（web 端同款）
            if (_held.Count == 0 && heldMs >= HoldStopMs) OnMove?.Invoke((0, 0));
        }
    }

    public override void _Process(double delta)
    {
        if (_held.Count == 0) return;
        _accum += delta;
        if (_accum >= 0.05)
        {
            _accum = 0;
            SendHeld();
        }
    }

    private void SendHeld()
    {
        if (_held.Count == 0) return;
        var dirs = _held.Select(k => MoveInput.TryMap(k)!.Value);
        OnMove?.Invoke(MoveInput.Combine(dirs));
    }
}
