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
    /// <summary>本地预测意图：按下/松开/转弯都通知（与是否发服务端命令无关）。</summary>
    public Action<(int Dx, int Dy)>? OnIntent;

    private readonly HashSet<string> _held = new();
    private readonly Dictionary<string, long> _keyDownAt = new();
    private double _accum;
    private (int Dx, int Dy)? _lastDir;
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
            if (_held.Count == 0)
            {
                _lastDir = (0, 0);
                // 本地预测：松开立即停（短按的命令已入队，服务端走完后自然停）
                OnIntent?.Invoke((0, 0));
                // 服务端命令：只有长按才发停止（web 端同款，避免清掉刚入队的步）
                if (heldMs >= HoldStopMs) OnMove?.Invoke((0, 0));
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_held.Count == 0) return;
        _accum += delta;
        if (_accum >= 0.08) // 服务端每 100ms 消费一步：80ms 发送略快于消费，队列不饥饿也不积压
        {
            _accum = 0;
            SendHeld();
        }
    }

    private void SendHeld()
    {
        if (_held.Count == 0) return;
        var dirs = _held.Select(k => MoveInput.TryMap(k)!.Value);
        SendDir(MoveInput.Combine(dirs));
    }

    /// <summary>
    /// 发送方向：转弯（非零→非零且变化）先发 (0,0) 清空服务端队列，
    /// 让新方向立即执行，不被旧命令积压延迟。
    /// </summary>
    private void SendDir((int Dx, int Dy) dir)
    {
        if (_lastDir is { } prev && prev != (0, 0) && dir != (0, 0) && prev != dir)
        {
            OnMove?.Invoke((0, 0));
            OnIntent?.Invoke((0, 0));
        }
        _lastDir = dir;
        OnMove?.Invoke(dir);
        OnIntent?.Invoke(dir);
    }
}
