using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 移动输入：WASD/方向键按住即走、每 100ms 重发方向、松开发停。
/// M7 连续速度契约：方向保持——按住持续发当前方向（最后一条输入生效，无队列），
/// 松开发 (0,0) 清方向停止；转弯直接发新方向，无需先清队列。
/// 按键 → 世界方向的映射在 Core.MoveInput，这里只做输入采集。
/// </summary>
public partial class MoveController : Node
{
    /// <summary>方向变化回调（(0,0) = 停止）。</summary>
    public Action<(int Dx, int Dy)>? OnMove;
    /// <summary>本地预测意图：按下/松开/转弯都通知（与是否发服务端命令无关）。</summary>
    public Action<(int Dx, int Dy)>? OnIntent;

    private readonly HashSet<string> _held = new();
    private double _accum;
    private (int Dx, int Dy)? _lastDir;

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey key || key.Echo) return;
        var name = OS.GetKeycodeString(key.Keycode);
        if (MoveInput.TryMap(name) is null) return;

        if (key.Pressed)
        {
            _held.Add(name);
            SendHeld();
        }
        else
        {
            _held.Remove(name);
            if (_held.Count == 0)
            {
                _lastDir = (0, 0);
                OnIntent?.Invoke((0, 0));
                OnMove?.Invoke((0, 0));
            }
            else
            {
                // 组合键松开一轴时立即切换剩余方向，不等待下一次 100ms 保活重发。
                SendHeld();
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_held.Count == 0) return;
        _accum += delta;
        if (_accum >= 0.1) // 方向保持：每 100ms 重发当前方向（防丢包；服务端最后一条输入生效）
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

    private void SendDir((int Dx, int Dy) dir)
    {
        _accum = 0;
        _lastDir = dir;
        OnMove?.Invoke(dir);
        OnIntent?.Invoke(dir);
    }
}
