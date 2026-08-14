using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>相机输入：滚轮缩放 + 左键拖拽平移（只改 Core.Camera，不碰场景结构）。</summary>
public partial class CameraController : Node
{
	public Camera? Camera { get; set; }

	private bool _dragging;
	private Vector2 _lastMouse;

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			if (mb.ButtonIndex == MouseButton.WheelUp)
			{
				Camera?.ZoomBy(1.15f);
			}
			else if (mb.ButtonIndex == MouseButton.WheelDown)
			{
				Camera?.ZoomBy(1f / 1.15f);
			}
			else if (mb.ButtonIndex == MouseButton.Left)
			{
				_dragging = mb.Pressed;
				_lastMouse = mb.Position;
			}
		}
		else if (@event is InputEventMouseMotion mm && _dragging)
		{
			Camera?.PanBy(mm.Position.X - _lastMouse.X, mm.Position.Y - _lastMouse.Y);
			_lastMouse = mm.Position;
		}
	}
}
