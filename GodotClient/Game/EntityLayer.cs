using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Starve.Core;
using Starve.Game.V1;
using Starve.Protocol.World;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

public readonly record struct EntityStyle(Color Color, float Radius, bool IsFire, bool IsTree = false);

/// <summary>实体层：世界实体 → 菱形占位（带投影/血条/受击闪白）或骨骼角色。</summary>
public partial class EntityLayer : Node2D
{
	private readonly Dictionary<ulong, EntityNode> _nodes = new();
	private readonly Dictionary<ulong, ActorNode> _actors = new();
	private TileMap? _tilemap;
	private long _lastNow;
	private float _sunT = 1f;

	public void SetTilemap(TileMap? tm) => _tilemap = tm;

	public void SetDayLight(float dayLight) => _sunT = 1f - Mathf.Max(0, 1f - dayLight * 2f);

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
			var hp = view.Get("Health", Health.Parser);
			node.Configure(StyleFor(view), hp?.Cur ?? 0, hp?.Max ?? 0, hp?.Max > 0);
		}
	}

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
			node.Tick(now, _sunT);
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
			actor.SetSunT(_sunT);
		}
	}

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

		// M7 交互重构：受激能力拆成 Choppable/Minable/Pickable（载荷都是 WorkTarget），
		// 树/矿/浆果按组件名区分，不再用旧的 Workable。
		var reactive = view.Get("Choppable", WorkTarget.Parser)
			?? view.Get("Minable", WorkTarget.Parser)
			?? view.Get("Pickable", WorkTarget.Parser);
		if (reactive is not null)
		{
			var kind = (int)reactive.Kind;
			var isTree = view.Get("Choppable", WorkTarget.Parser) is not null;
			return new EntityStyle(kind switch
			{
				1 => new Color(0.89f, 0.34f, 0.30f),
				2 => new Color(0.60f, 0.42f, 0.25f),
				3 => new Color(0.60f, 0.63f, 0.66f),
				4 => new Color(0.85f, 0.42f, 0.31f),
				_ => new Color(0.71f, 0.54f, 0.85f),
			}, isTree ? 16 : 7, false, isTree);
		}

		// 兼容旧档/旧协议：仍下发 Workable 时兜底。
		var workable = view.Get("Workable", Workable.Parser);
		if (workable is not null)
		{
			return new EntityStyle((int)workable.Kind switch
			{
				1 => new Color(0.89f, 0.34f, 0.30f),
				2 => new Color(0.60f, 0.42f, 0.25f),
				3 => new Color(0.60f, 0.63f, 0.66f),
				4 => new Color(0.85f, 0.42f, 0.31f),
				_ => new Color(0.71f, 0.54f, 0.85f),
			}, (int)workable.Kind == 2 ? 16 : 7, false, (int)workable.Kind == 2);
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

/// <summary>单个实体占位：彩色菱形 + 方向投影 + 血条 + 受击闪白 + 可选火堆点光。</summary>
public partial class EntityNode : Node2D
{
	private Color _color = Colors.White;
	private float _radius = 8;
	private PointLight2D? _light;
	private GpuParticles2D? _fire;
	private static Texture2D? _glowTexture;
	private static Texture2D? _particleDot;
	private static Texture2D? _treeTexture;
	private Sprite2D? _treeSprite;
	private bool _isTree;
	private int _healthCur;
	private int _healthMax;
	private bool _showBar;
	private int _lastHealth = -1;
	private long _flashUntil;
	private float _sunT = 1f;

	public void Configure(EntityStyle style, int healthCur, int healthMax, bool showBar)
	{
		_color = style.Color;
		_radius = style.Radius;
		_isTree = style.IsTree;
		_healthCur = healthCur;
		_healthMax = healthMax;
		_showBar = showBar;
		if (_lastHealth >= 0 && healthCur < _lastHealth)
		{
			_flashUntil = (long)Time.GetTicksMsec() + 300;
		}
		_lastHealth = healthCur;
		if (_isTree)
		{
			_treeTexture ??= GD.Load<Texture2D>("res://assets/tiles/tile_001.png");
			_treeSprite ??= new Sprite2D
			{
				Texture = _treeTexture,
				Centered = true,
				Position = new Vector2(0, -27),
				Scale = new Vector2(34f / _treeTexture.GetWidth(), 54f / _treeTexture.GetHeight()),
			};
			if (_treeSprite.GetParent() is null) AddChild(_treeSprite);
			_treeSprite.Visible = true;
		}
		else if (_treeSprite is not null)
		{
			_treeSprite.Visible = false;
		}
		QueueRedraw();
		if (style.IsFire)
		{
			_light ??= MakeLight();
			_light.Enabled = true;
			_fire ??= MakeFire();
			_fire.Emitting = true;
		}
		else
		{
			if (_light is not null) _light.Enabled = false;
			if (_fire is not null) _fire.Emitting = false;
		}
	}

	public void Tick(long now, float sunT)
	{
		_sunT = sunT;
		if (_treeSprite is not null && _isTree)
		{
			_treeSprite.Rotation = Mathf.Sin(now / 560f + GetInstanceId() * 0.001f) * 0.045f;
		}
		if (now < _flashUntil) QueueRedraw();
	}

	public override void _Draw()
	{
		// 方向投影：太阳越高越短、越淡；影子朝太阳反方向偏移
		var len = 0.35f + (1f - _sunT) * 1.0f;
		var shadowW = Mathf.Max(7, _radius * 1.1f);
		DrawEllipsePoly(
			new Vector2(-shadowW * 0.5f * len, _radius * 0.15f),
			shadowW * len,
			shadowW * 0.35f,
			new Color(0, 0, 0, 0.16f + 0.16f * _sunT));

		if (!_isTree)
		{
			DrawColoredPolygon(
				new[] { new Vector2(0, -_radius), new Vector2(_radius, 0), new Vector2(0, _radius), new Vector2(-_radius, 0) },
				_color);
			DrawPolyline(
				new[] { new Vector2(0, -_radius), new Vector2(_radius, 0), new Vector2(0, _radius), new Vector2(-_radius, 0), new Vector2(0, -_radius) },
				new Color(0.1f, 0.1f, 0.1f, 0.7f),
				1.5f);
		}

		if (_showBar && _healthMax > 0)
		{
			var ratio = Mathf.Clamp(_healthCur / (float)_healthMax, 0, 1);
			DrawRect(new Rect2(-11, -_radius - 12, 22, 4), new Color(0.2f, 0.2f, 0.2f, 0.9f));
			DrawRect(new Rect2(-11, -_radius - 12, 22 * ratio, 4), ratio > 0.3f ? new Color(0.31f, 0.75f, 0.37f) : new Color(0.89f, 0.34f, 0.30f));
		}

		// 受击闪白
		if (!_isTree && (long)Time.GetTicksMsec() < _flashUntil)
		{
			DrawColoredPolygon(
				new[] { new Vector2(0, -_radius), new Vector2(_radius, 0), new Vector2(0, _radius), new Vector2(-_radius, 0) },
				new Color(1, 1, 1, 0.6f));
		}
	}

	private void DrawEllipsePoly(Vector2 center, float rx, float ry, Color color)
	{
		const int n = 24;
		var pts = new Vector2[n];
		for (var i = 0; i < n; i++)
		{
			var a = Mathf.Tau * i / n;
			pts[i] = center + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
		}
		DrawColoredPolygon(pts, color);
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

	/// <summary>火堆粒子：向上飘散的火花/余烬（Godot 内建 GPUParticles2D）。</summary>
	private GpuParticles2D MakeFire()
	{
		_particleDot ??= CreateParticleDot();
		var mat = new ParticleProcessMaterial
		{
			Direction = new Vector3(0, -1, 0),
			InitialVelocityMin = 30,
			InitialVelocityMax = 80,
			Gravity = new Vector3(0, -24, 0),
			EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
			EmissionSphereRadius = 5,
			ScaleMin = 0.5f,
			ScaleMax = 1.3f,
		};
		var p = new GpuParticles2D
		{
			Texture = _particleDot,
			ProcessMaterial = mat,
			Amount = 22,
			Lifetime = 0.9,
			Emitting = true,
			Modulate = new Color(1f, 0.72f, 0.35f),
			Position = new Vector2(0, -4),
		};
		AddChild(p);
		return p;
	}

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

	private static Texture2D CreateParticleDot()
	{
		const int s = 16;
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
