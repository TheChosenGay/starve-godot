using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Starve.Core;
using Starve.Game.V1;
using Starve.Protocol.World;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

public readonly record struct EntityStyle(Color Color, float Radius, bool IsFire);

/// <summary>实体层：世界实体 → 彩色菱形占位（阶段 1 简化；阶段 2 换骨骼/贴图）。</summary>
public partial class EntityLayer : Node2D
{
	private readonly Dictionary<ulong, EntityNode> _nodes = new();
	private readonly Dictionary<ulong, ActorNode> _actors = new();
	private TileMap? _tilemap;
	private long _lastNow;

	public void SetTilemap(TileMap? tm) => _tilemap = tm;

	/// <summary>按世界表增删实体并刷新颜色（阶段 1 每次世界变更全量同步，量小够用）。</summary>
	public void SyncEntities(IReadOnlyDictionary<ulong, EntityView> entities)
	{
		var ids = _nodes.Keys.Concat(_actors.Keys).Distinct().ToList();
		foreach (var id in ids.Where(id => !entities.ContainsKey(id)))
		{
			Remove(id);
		}

		foreach (var (id, view) in entities)
		{
			var hasPos = view.Get("Position", Starve.Game.V1.Position.Parser) is not null;
			var isPlayer = view.Get("Player", Player.Parser) is not null;

			if (isPlayer)
			{
				if (_nodes.TryGetValue(id, out var diamond))
				{
					diamond.QueueFree();
					_nodes.Remove(id);
				}
				if (!_actors.TryGetValue(id, out var actor))
				{
					actor = new ActorNode();
					_actors[id] = actor;
					AddChild(actor);
				}
				actor.Visible = hasPos;
				continue;
			}

			if (_actors.TryGetValue(id, out var old))
			{
				old.QueueFree();
				_actors.Remove(id);
			}

			if (!hasPos)
			{
				if (_nodes.TryGetValue(id, out var gone)) gone.Visible = false;
				continue;
			}

			if (!_nodes.TryGetValue(id, out var node))
			{
				node = new EntityNode();
				_nodes[id] = node;
				AddChild(node);
			}
			node.Visible = true;
			node.Configure(StyleFor(view));
		}
	}

	/// <summary>每帧用平滑位置更新实体本地坐标 + 画家排序。</summary>
	public void UpdatePositions(
		IReadOnlyDictionary<ulong, PositionSmoother> smoothers,
		Func<ulong, bool> isMoving,
		long now)
	{
		var deltaMs = _lastNow == 0 ? 16 : now - _lastNow;
		_lastNow = now;

		foreach (var (id, node) in _nodes)
		{
			if (!smoothers.TryGetValue(id, out var sm)) continue;
			var p = sm.Current(now);
			var h = _tilemap?.HeightAt(p.X, p.Y) ?? 0;
			var local = IsoMath.WorldToLocal(p.X, p.Y, h);
			node.Position = new Vector2(local.X, local.Y);
			node.ZIndex = (int)local.Y;
		}

		foreach (var (id, actor) in _actors)
		{
			if (!smoothers.TryGetValue(id, out var sm)) continue;
			var p = sm.Current(now);
			var h = _tilemap?.HeightAt(p.X, p.Y) ?? 0;
			var local = IsoMath.WorldToLocal(p.X, p.Y, h);
			actor.Position = new Vector2(local.X, local.Y);
			actor.ZIndex = (int)local.Y;
			actor.Update(deltaMs, isMoving(id));
		}
	}

	/// <summary>触发动作动画（攻击/采集）。</summary>
	public void PlayAction(ulong id, string action)
	{
		if (_actors.TryGetValue(id, out var actor)) actor.Play(action);
	}

	private void Remove(ulong id)
	{
		if (_nodes.TryGetValue(id, out var n))
		{
			n.QueueFree();
			_nodes.Remove(id);
		}
		if (_actors.TryGetValue(id, out var a))
		{
			a.QueueFree();
			_actors.Remove(id);
		}
	}

	private static EntityStyle StyleFor(EntityView view)
	{
		if (view.Get("Player", Player.Parser) is not null)
			return new EntityStyle(new Color(0.31f, 0.75f, 0.37f), 10, false);
		if (view.Components.ContainsKey("Dead"))
			return new EntityStyle(new Color(0.47f, 0.47f, 0.47f), 8, false);
		if (view.Get("Loot", Loot.Parser) is not null)
			return new EntityStyle(new Color(1f, 0.85f, 0.31f), 6, false);

		var station = view.Get("Workstation", Workstation.Parser);
		if (station is not null)
			return (int)station.Type == 1
				? new EntityStyle(new Color(1f, 0.55f, 0.26f), 10, true)
				: new EntityStyle(new Color(0.60f, 0.42f, 0.25f), 10, false);

		var workable = view.Get("Workable", Workable.Parser);
		if (workable is not null)
		{
			return new EntityStyle((int)workable.Kind switch
			{
				1 => new Color(0.89f, 0.34f, 0.30f), // berry
				2 => new Color(0.60f, 0.42f, 0.25f), // wood
				3 => new Color(0.60f, 0.63f, 0.66f), // flint
				4 => new Color(0.85f, 0.42f, 0.31f), // meat
				_ => new Color(0.71f, 0.54f, 0.85f),
			}, 7, false);
		}

		var creature = view.Get("Creature", Creature.Parser);
		if (creature is not null)
		{
			return new EntityStyle((int)creature.Kind switch
			{
				1 => new Color(0.63f, 0.44f, 0.31f),
				2 => new Color(0.54f, 0.56f, 0.60f),
				3 => new Color(0.36f, 0.25f, 0.22f),
				4 => new Color(0.84f, 0.72f, 0.60f),
				5 => new Color(0.29f, 0.14f, 0.35f),
				_ => new Color(1f, 1f, 1f),
			}, 9, false);
		}

		var building = view.Get("Building", Building.Parser);
		if (building is not null)
			return (int)building.Kind == 1
				? new EntityStyle(new Color(1f, 0.55f, 0.26f), 10, building.Placed)
				: new EntityStyle(new Color(0.60f, 0.42f, 0.25f), 8, false);

		return new EntityStyle(new Color(1f, 1f, 1f), 8, false);
	}
}

/// <summary>单个实体占位：彩色菱形 + 描边（自绘）。</summary>
public partial class EntityNode : Node2D
{
	private Color _color = Colors.White;
	private float _radius = 8;
	private PointLight2D? _light;
	private static Texture2D? _glowTexture;

	public void Configure(EntityStyle style)
	{
		_color = style.Color;
		_radius = style.Radius;
		QueueRedraw();
		if (style.IsFire)
		{
			_light ??= MakeLight();
			_light.Enabled = true;
		}
		else if (_light is not null)
		{
			_light.Enabled = false;
		}
	}

	public override void _Draw()
	{
		DrawColoredPolygon(
			new[] { new Vector2(0, -_radius), new Vector2(_radius, 0), new Vector2(0, _radius), new Vector2(-_radius, 0) },
			_color);
		DrawPolyline(
			new[] { new Vector2(0, -_radius), new Vector2(_radius, 0), new Vector2(0, _radius), new Vector2(-_radius, 0), new Vector2(0, -_radius) },
			new Color(0.1f, 0.1f, 0.1f, 0.7f),
			1.5f);
	}

	private static PointLight2D MakeLight()
	{
		_glowTexture ??= CreateGlowTexture();
		return new PointLight2D
		{
			Texture = _glowTexture,
			Color = new Color(1f, 0.63f, 0.29f),
			Energy = 1.5f,
			TextureScale = 2.5f,
		};
	}

	/// <summary>柔和径向光斑（白心 → 透明边缘），PointLight2D 用它做软光。</summary>
	private static Texture2D CreateGlowTexture()
	{
		const int s = 64;
		var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
		for (var y = 0; y < s; y++)
		{
			for (var x = 0; x < s; x++)
			{
				var d = new Vector2(x - s / 2, y - s / 2).Length() / (s / 2);
				img.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp(1 - d, 0, 1)));
			}
		}
		return ImageTexture.CreateFromImage(img);
	}
}
