using System;
using System.Collections.Generic;
using Godot;
using Starve.Game.V1;
using Starve.Protocol.World;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>小地图：地形底图 + 实体点 + 视口框（约 4Hz 刷新）。右上角锚点，随窗口贴边。</summary>
public partial class MinimapView : Control
{
	private const float MapSize = 112f;
	private const float Margin = 12f;

	private readonly TextureRect _tex = new();
	private PanelContainer? _frame;
	private TileMap? _map;
	private IReadOnlyDictionary<ulong, EntityView> _entities = new Dictionary<ulong, EntityView>();
	private Vector2 _camCenter;
	private float _zoom = 1f;
	private Vector2 _screenSize;
	private double _timer;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		Theme = HudTheme.Create();

		_frame = new PanelContainer
		{
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_frame.SetAnchorsPreset(LayoutPreset.TopLeft);
		Resized += PlaceFrame;
		CallDeferred(MethodName.PlaceFrame);

		var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
		margin.AddThemeConstantOverride("margin_left", 6);
		margin.AddThemeConstantOverride("margin_top", 6);
		margin.AddThemeConstantOverride("margin_right", 6);
		margin.AddThemeConstantOverride("margin_bottom", 6);
		_tex.CustomMinimumSize = new Vector2(MapSize, MapSize);
		_tex.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
		_tex.StretchMode = TextureRect.StretchModeEnum.Scale;
		_tex.MouseFilter = MouseFilterEnum.Ignore;
		margin.AddChild(_tex);
		_frame.AddChild(margin);
		AddChild(_frame);
	}

	public override void _ExitTree()
	{
		Resized -= PlaceFrame;
	}

	private void PlaceFrame()
	{
		if (_frame is null) return;
		var size = Size.X > 1 ? Size : GetViewportRect().Size;
		const float frame = MapSize + 16;
		var pos = new Vector2(size.X - Margin - frame, Margin + 28);
		if (_frame.Position.DistanceSquaredTo(pos) < 1f && Mathf.IsEqualApprox(_frame.Size.X, frame))
			return;
		_frame.Position = pos;
		_frame.Size = new Vector2(frame, frame);
	}

	public void SetMap(TileMap tm)
	{
		_map = tm;
		var img = Image.CreateEmpty(tm.Width, tm.Height, false, Image.Format.Rgba8);
		var colors = new[]
		{
			new Color(0.4f, 0.4f, 0.4f),
			new Color(0.17f, 0.42f, 0.69f),
			new Color(0.85f, 0.7f, 0.55f),
			new Color(0.35f, 0.56f, 0.31f),
			new Color(0.54f, 0.56f, 0.6f),
			new Color(0.85f, 0.9f, 0.93f),
		};
		for (var cy = 0; cy < tm.Height; cy++)
		{
			for (var cx = 0; cx < tm.Width; cx++)
			{
				var type = tm.CornerType(cx, cy);
				img.SetPixel(cx, cy, colors[Math.Clamp(type, 0, colors.Length - 1)]);
			}
		}
		_tex.Texture = ImageTexture.CreateFromImage(img);
	}

	public void SetView(IReadOnlyDictionary<ulong, EntityView> entities, Vector2 camCenter, float zoom, Vector2 screenSize)
	{
		_entities = entities;
		_camCenter = camCenter;
		_zoom = zoom;
		_screenSize = screenSize;
		QueueRedraw();
	}

	public override void _Process(double delta)
	{
		_timer += delta;
		if (_timer >= 0.25)
		{
			_timer = 0;
			QueueRedraw();
		}
	}

	public override void _Draw()
	{
		if (_map is null) return;
		var origin = _tex.GlobalPosition;
		var sx = MapSize / _map.Width;
		var sy = MapSize / _map.Height;

		var vw = _screenSize.X / (40f * _zoom) * sx;
		var vh = _screenSize.Y / (40f * _zoom) * sy;
		var ccx = _camCenter.X * sx;
		var ccy = _camCenter.Y * sy;
		DrawRect(
			new Rect2(origin + new Vector2(ccx - vw / 2f, ccy - vh / 2f), new Vector2(vw, vh)),
			new Color(1, 1, 1, 0.45f),
			false,
			1f);

		foreach (var view in _entities.Values)
		{
			var pos = view.Get("Position", Starve.Game.V1.Position.Parser);
			if (pos is null) continue;
			var mx = pos.X * sx + origin.X;
			var my = pos.Y * sy + origin.Y;
			DrawCircle(new Vector2(mx, my), 2f, ColorFor(view));
		}
	}

	private static Color ColorFor(EntityView view)
	{
		if (view.Get("Hauntable", Hauntable.Parser) is not null) return new Color(0.35f, 0.9f, 1f);
		if (view.Get("Player", Player.Parser) is not null) return new Color(0.94f, 0.78f, 0.38f);
		if (view.LootOf() is not null) return new Color(1f, 0.85f, 0.31f);
		if (view.Get("Workstation", Workstation.Parser) is not null) return new Color(1f, 0.63f, 0.29f);
		var building = view.Get("Building", Building.Parser);
		if (building is not null) return (int)building.Kind == 1 ? new Color(1f, 0.63f, 0.29f) : new Color(0.6f, 0.42f, 0.25f);
		var creature = view.Get("Creature", Creature.Parser);
		if (creature is not null) return (int)creature.Kind switch
		{
			1 => new Color(0.63f, 0.44f, 0.31f),
			2 => new Color(0.54f, 0.56f, 0.6f),
			3 => new Color(0.36f, 0.25f, 0.22f),
			4 => new Color(0.84f, 0.72f, 0.6f),
			_ => new Color(0.29f, 0.14f, 0.35f),
		};
		return new Color(0.8f, 0.8f, 0.8f);
	}
}
