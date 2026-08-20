using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Starve.Core;
using Starve.Game.V1;
using Starve.Protocol.World;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

public readonly record struct EntityStyle(
	Color Color,
	float Radius,
	bool IsFire,
	bool IsTree = false,
	bool IsWorkbench = false);

/// <summary>实体层：世界实体 → 菱形占位（带投影/血条/受击闪白）或骨骼角色。</summary>
public partial class EntityLayer : Node2D
{
	private readonly Dictionary<ulong, EntityNode> _nodes = new();
	private readonly Dictionary<ulong, RigNode> _rigs = new();
	private readonly Dictionary<ulong, (float X, float Y)> _rigLastPos = new();
	private readonly Dictionary<ulong, float> _heightSm = new();
	private TileMap? _tilemap;
	private ulong _ownId;
	private Func<EntityView, string?>? _nameProvider;
	private long _lastNow;
	private float _sunT = 1f;
	private float _viewSin;
	private float _viewCos = 1f;

	public void SetTilemap(TileMap? tm) => _tilemap = tm;

	public void SetDayLight(float dayLight) => _sunT = 1f - Mathf.Max(0, 1f - dayLight * 2f);

	/// <summary>视图旋转角（弧度）：Z 排序按旋转后的屏幕 Y，实体随世界节点一起转。</summary>
	public void SetViewRotation(float radians)
	{
		_viewSin = Mathf.Sin(radians);
		_viewCos = Mathf.Cos(radians);
	}

	public void SetOwnId(ulong id) => _ownId = id;

	/// <summary>实体名字提供器（GameRoot 注入：资源/掉落/生物/建筑的中文名）。</summary>
	public void SetNameProvider(Func<EntityView, string?> provider) => _nameProvider = provider;

	public void SyncEntities(IReadOnlyDictionary<ulong, EntityView> entities)
	{
		var ids = _nodes.Keys.Concat(_rigs.Keys).Distinct().ToList();
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
				// 主角 = 鱼人（人鱼）帧动画；掉落/建筑等仍走 EntityNode。
				if (_nodes.TryGetValue(id, out var diamond))
				{
					diamond.QueueFree();
					_nodes.Remove(id);
				}
				if (!_rigs.TryGetValue(id, out var rigNode))
				{
					rigNode = new RigNode(RigRegistry.Player);
					_rigs[id] = rigNode;
					AddChild(rigNode);
				}
				var hpSelf = view.Get("Health", Health.Parser);
				rigNode.Configure(hpSelf?.Cur ?? 0, hpSelf?.Max ?? 0, false, "");
				rigNode.Visible = hasPos;
				continue;
			}

			if (!hasPos)
			{
				if (_nodes.TryGetValue(id, out var gone)) gone.Visible = false;
				if (_rigs.TryGetValue(id, out var goneRig)) goneRig.Visible = false;
				continue;
			}

			// M7 生物视觉：鱼人/蜥蜴等注册了骨架的生物用帧动画，其余维持菱形占位。
			var rig = view.Get("Creature", Creature.Parser) is { } cr
				&& !view.Components.ContainsKey("Dead")
				? RigRegistry.RigOf((int)cr.Kind)
				: null;
			if (rig is not null)
			{
				if (_nodes.TryGetValue(id, out var diamond))
				{
					diamond.QueueFree();
					_nodes.Remove(id);
				}
				if (!_rigs.TryGetValue(id, out var rigNode))
				{
					rigNode = new RigNode(rig);
					_rigs[id] = rigNode;
					AddChild(rigNode);
				}
				var hp2 = view.Get("Health", Health.Parser);
				rigNode.Configure(hp2?.Cur ?? 0, hp2?.Max ?? 0, hp2?.Max > 0,
					_nameProvider?.Invoke(view) ?? "");
				continue;
			}

			if (_rigs.TryGetValue(id, out var oldRig))
			{
				oldRig.QueueFree();
				_rigs.Remove(id);
			}

			if (!_nodes.TryGetValue(id, out var node))
			{
				node = new EntityNode();
				_nodes[id] = node;
				AddChild(node);
			}
			node.Visible = true;
			var hp = view.Get("Health", Health.Parser);
			node.Configure(StyleFor(view), hp?.Cur ?? 0, hp?.Max ?? 0, hp?.Max > 0,
				_nameProvider?.Invoke(view) ?? "");
		}
	}

	public void UpdatePositions(
		IReadOnlyDictionary<ulong, PositionSmoother> smoothers,
		Func<ulong, bool> isMoving,
		long now,
		System.Numerics.Vector2? ownPos = null)
	{
		var deltaMs = _lastNow == 0 ? 16 : now - _lastNow;
		_lastNow = now;

		foreach (var (id, node) in _nodes)
		{
			if (!smoothers.TryGetValue(id, out var sm)) continue;
			var p = sm.Current(now);
			var targetHeight = _tilemap?.HeightAt(p.X, p.Y) ?? 0;
			// 自己与相机必须使用同一瞬时高度，否则坡地上两套滤波会造成视觉回拉。
			var h = id == _ownId ? targetHeight : SmoothHeight(id, targetHeight, deltaMs);
			var local = IsoMath.WorldToLocal(p.X, p.Y, h);
			node.Position = new Vector2(local.X, local.Y);
			node.ZIndex = (int)(local.X * _viewSin + local.Y * _viewCos);
			node.Tick(now, _sunT);
		}

		foreach (var (id, rig) in _rigs)
		{
			System.Numerics.Vector2 p;
			if (id == _ownId && ownPos is { } op)
			{
				p = new System.Numerics.Vector2(op.X, op.Y);
			}
			else if (smoothers.TryGetValue(id, out var sm))
			{
				p = sm.Current(now);
			}
			else
			{
				continue;
			}
			var targetHeight = _tilemap?.HeightAt(p.X, p.Y) ?? 0;
			var h = id == _ownId ? targetHeight : SmoothHeight(id, targetHeight, deltaMs);
			var local = IsoMath.WorldToLocal(p.X, p.Y, h);
			rig.Position = new Vector2(local.X, local.Y);
			rig.ZIndex = (int)(local.X * _viewSin + local.Y * _viewCos);
			if (_rigLastPos.TryGetValue(id, out var last))
			{
				var dx = p.X - last.X;
				var dy = p.Y - last.Y;
				if (MathF.Abs(dx) + MathF.Abs(dy) > 0.03f)
				{
					rig.SetMovementDirection(dx, dy, _viewSin, _viewCos);
				}
			}
			_rigLastPos[id] = (p.X, p.Y);
			rig.Update(deltaMs, isMoving(id));
			rig.SetSunT(_sunT);
		}
	}

	public void PlayAction(ulong id, string action)
	{
		if (_rigs.TryGetValue(id, out var rig)) rig.Play(action);
	}

	private void Remove(ulong id)
	{
		if (_nodes.TryGetValue(id, out var n))
		{
			n.QueueFree();
			_nodes.Remove(id);
		}
		if (_rigs.TryGetValue(id, out var r))
		{
			r.QueueFree();
			_rigs.Remove(id);
		}
		_rigLastPos.Remove(id);
		_heightSm.Remove(id);
	}

	/// <summary>
	/// 坡道高度平滑：屏幕 Y 含 -h×20 项，陡坡会让屏幕速度骤变（上坡卡、下坡冲）。
	/// 用 ~40ms 时间常数低通，缓解速度突变（陡坡上角色会短暂轻微浮起，可接受）。
	/// </summary>
	private float SmoothHeight(ulong id, float target, float deltaMs)
	{
		if (_heightSm.TryGetValue(id, out var prev))
		{
			var k = Mathf.Min(1f, deltaMs / 40f);
			target = prev + (target - prev) * k;
		}
		_heightSm[id] = target;
		return target;
	}

	private static EntityStyle StyleFor(EntityView view)
	{
		if (view.Get("Player", Player.Parser) is not null)
			return new EntityStyle(new Color(0.31f, 0.75f, 0.37f), 10, false);
		// 掉落物优先于 Dead：挖完的矿/树是 Dead+Loot，应显示成可拾取的黄色，
		// 而不是尸体的灰色（否则看不出能捡）。
		if (view.LootOf() is not null)
			return new EntityStyle(new Color(1f, 0.85f, 0.31f), 6, false);
		if (view.Components.ContainsKey("Dead"))
			return new EntityStyle(new Color(0.47f, 0.47f, 0.47f), 8, false);

		var station = view.Get("Workstation", Workstation.Parser);
		if (station is not null)
			return (int)station.Type == 1
				? new EntityStyle(new Color(1f, 0.55f, 0.26f), 10, true)
				: new EntityStyle(new Color(0.60f, 0.42f, 0.25f), 10, false, IsWorkbench: true);

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
				6 => new Color(0.22f, 0.55f, 0.58f), // 鱼人
				7 => new Color(0.55f, 0.75f, 0.35f), // 蜥蜴
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

/// <summary>单个实体占位：彩色菱形 + 方向投影 + 血条 + 受击闪白 + 火盆/工作站结构视觉。</summary>
public partial class EntityNode : Node2D
{
	private Color _color = Colors.White;
	private float _radius = 8;
	private static Texture2D? _treeTexture;
	private Sprite2D? _treeSprite;
	private Node2D? _structure;
	private Label? _nameLabel;
	private bool _isTree;
	private bool _hasStructure;
	private int _healthCur;
	private int _healthMax;
	private bool _showBar;
	private int _lastHealth = -1;
	private long _flashUntil;
	private float _sunT = 1f;
	private static Texture2D? _firePitTex;
	private static SpriteFrames? _alchemyFrames;

	/// <summary>2×2 火盆：Building.Position 是左上角锚点，视觉中心偏移到脚印中心。</summary>
	private static readonly Vector2 FirePitCenter = new(0, 20);

	public void Configure(EntityStyle style, int healthCur, int healthMax, bool showBar, string name = "")
	{
		_color = style.Color;
		_radius = style.Radius;
		_isTree = style.IsTree;
		UpdateNameLabel(name);
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
			EnsureFirePit();
		}
		else if (style.IsWorkbench)
		{
			EnsureAlchemy();
		}
		else if (_structure is not null)
		{
			_structure.Visible = false;
			_hasStructure = false;
		}
	}

	/// <summary>火盆：fire-pit 底座贴图 + 手绘粒子火焰（FirePitFire）。</summary>
	private void EnsureFirePit()
	{
		if (_structure is null)
		{
			_firePitTex ??= GD.Load<Texture2D>("res://assets/structures/fire-pit/fire-pit-cutout.png");
			_structure = new Node2D { Position = FirePitCenter };
			_structure.AddChild(new Sprite2D
			{
				Texture = _firePitTex,
				Centered = true,
				Scale = Vector2.One * 0.09f,
			});
			_structure.AddChild(new FirePitFire());
			AddChild(_structure);
		}
		_structure.Visible = true;
		_hasStructure = true;
	}

	/// <summary>工作站：alchemy-engine 空闲动画（15 帧循环）。</summary>
	private void EnsureAlchemy()
	{
		if (_structure is null)
		{
			_alchemyFrames ??= BuildAlchemyFrames();
			_structure = new Node2D();
			var anim = new AnimatedSprite2D
			{
				SpriteFrames = _alchemyFrames,
				Centered = true,
				Scale = Vector2.One * 0.11f,
			};
			anim.Play("idle");
			_structure.AddChild(anim);
			AddChild(_structure);
		}
		_structure.Visible = true;
		_hasStructure = true;
	}

	private static SpriteFrames BuildAlchemyFrames()
	{
		var sf = new SpriteFrames();
		sf.AddAnimation("idle");
		sf.SetAnimationLoopMode("idle", SpriteFrames.LoopMode.Linear);
		sf.SetAnimationSpeed("idle", 8);
		for (var i = 1; i <= 15; i++)
		{
			sf.AddFrame("idle", GD.Load<Texture2D>($"res://assets/structures/alchemy-engine/cutout/frame_{i:000}.png"));
		}
		return sf;
	}

	private void UpdateNameLabel(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			if (_nameLabel is not null) _nameLabel.Visible = false;
			return;
		}
		_nameLabel ??= new Label
		{
			CustomMinimumSize = new Vector2(140, 20),
			HorizontalAlignment = HorizontalAlignment.Center,
			ZIndex = 100,
			LabelSettings = new LabelSettings
			{
				FontSize = 12,
				FontColor = Colors.White,
				OutlineSize = 4,
				OutlineColor = new Color(0, 0, 0, 0.85f),
			},
		};
		_nameLabel.Text = name;
		_nameLabel.Position = new Vector2(-70, -_radius - 38);
		_nameLabel.Visible = true;
		if (_nameLabel.GetParent() is null) AddChild(_nameLabel);
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

		if (!_isTree && !_hasStructure)
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

}
