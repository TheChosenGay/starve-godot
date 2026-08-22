using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 移动输入：WASD/方向键按住即走、每 100ms 重发方向、松开发停。
/// 只认 _Input 按键事件，不轮询实体键盘——IsPhysicalKeyPressed 在部分环境下恒为 false，
/// 会把还按着的方向键清掉，角色只剩 idle。
/// </summary>
public partial class MoveController : Node
{
    public Action<(int Dx, int Dy)>? OnMove;
    public Action<(int Dx, int Dy)>? OnIntent;

    private readonly HashSet<string> _held = new();
    private double _accum;
    private (int Dx, int Dy)? _lastDir;
    private bool _blocked;

    public void SetBlocked(bool blocked)
    {
        if (_blocked == blocked) return;
        _blocked = blocked;
        if (!blocked) return;
        _held.Clear();
        _accum = 0;
        _lastDir = (0, 0);
        OnIntent?.Invoke((0, 0));
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey key || key.Echo) return;
        var name = OS.GetKeycodeString(key.Keycode);
        if (MoveInput.TryMap(name) is null) return;
        if (_blocked)
        {
            _held.Remove(name);
            return;
        }

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
                SendHeld();
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_blocked || _held.Count == 0) return;
        _accum += delta;
        if (_accum < 0.1) return;
        _accum = 0;
        SendHeld();
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
