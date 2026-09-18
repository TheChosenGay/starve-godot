using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Google.Protobuf;
using Starve.Core;
using Starve.Game.V1;
using Starve.Netcode;
using Starve.Protocol;
using Starve.Protocol.World;
using Camera = Starve.Core.Camera;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>客户端可发起的交互意图（输入路由与服务端一致）。</summary>
public enum Intent { Gather, Chop, Mine, Pickup, Attack }

/// <summary>
/// 游戏主节点：分层编排——
/// 协议层（StarveClient）→ Core 逻辑（相机/地形/平滑/法线）→ 渲染层。
/// 渲染层不直接碰协议细节，协议层不碰渲染。
/// </summary>
public partial class GameRoot : Node
{
	private readonly Camera _camera = new();
	private readonly Dictionary<ulong, PositionSmoother> _smoothers = new();
	private readonly ConcurrentQueue<ActionOutcome> _actionOutcomes = new();
	private readonly ConcurrentQueue<WorldEvent> _worldEvents = new();
	private IOwnMovementSim? _ownSim;
	private NetcodeMetrics? _netcodeMetrics;
	/// <summary>上行冗余窗口（组件模式：每个 tick 把未确认的操作整批发出去）。</summary>
	private readonly List<ClientSmoother<OwnMoveState, MoveIntent>.OpRef> _ownUnacked = new();
	private Starve.Protocol.MoveOp[] _ownUnackedWire = new Starve.Protocol.MoveOp[8];
	// 占位物形状（树/岩的格心圆 + 建筑/工作站的占格盒）：只喂本地移动预测，不是阻挡网格
	private readonly List<BlockerShape> _blockers = new();
	private readonly List<OrcaNeighbor> _orcaNeighbors = new();
	private DebugShapeLayer3D? _debugShapes;

	// 投掷瞄准（T 进入，点地图选落点，再按 T 确认）。
	// 为什么用"模式"而不是直接点地图就扔：投掷是两段动作（windup → 抛出），
	// 且落点需要先看清抛物线再确认——直接扔会频繁误触。
	private ThrowAimLayer3D? _throwAim;
	/// <summary>本地预测的投掷物（ghost）表现层；权威飞行体由 EntityLayer3D 画。</summary>
	private ThrowFlightLayer3D? _throwFlight;
	/// <summary>
	/// 投掷飞行跟踪器：ghost 起手/飞行 + 权威样本和解。
	///
	/// **与 EntityLayer3D 共用同一个实例**（它在创建世界视图时注入）：自己的投掷交接那一刻，
	/// ghost 已经飞到 ~1 tick 之后，只有同一条 tracker 才看得见 ghost 的进度并接管过来；
	/// 两条各自 tracker 会让权威从 elapsed≈0 重新开始 ⇒ 画面向后跳半格。
	/// 纪律：快照只在这里喂一次（ApplyWorld），每帧只在这里 Tick 一次。
	/// </summary>
	private readonly ThrowFlightTracker _throwFlights = new();
	// 上一份 / 本份快照里带 Thrown 的实体（复用，避免每份快照分配）。
	private HashSet<ulong> _thrownSeen = new();
	private HashSet<ulong> _thrownNow = new();
	/// <summary>最近一次自己发起的投掷 request_id：用于把"被拒/取消"的 outcome 对上本地 ghost。</summary>
	private ulong _throwRequestId;
	private BlastFxLayer3D? _blastFx;
	private bool _throwAiming;
	private System.Numerics.Vector2 _throwTarget;
	private bool _throwHasTarget;
	/// <summary>投掷力量（从自己的 Thrower 组件读；0 = 不能投掷）。</summary>
	private int _ownThrowStrength;
	/// <summary>炸弹的质量（从物品模板读；决定最大距离）。</summary>
	private int _bombMass = 18;
	private readonly Dictionary<ulong, bool> _locomotionMoving = new();
	private bool _ownIntentMoving;
	private long _demoPatrolAt;
	private int _demoSign = 1;

	/// <summary>STARVE_DEMO_PATROL_MS>0 时自动走变成原地往返（每 N 毫秒反向）。</summary>
	private static long DemoPatrolMs =>
		long.TryParse(System.Environment.GetEnvironmentVariable("STARVE_DEMO_PATROL_MS"), out var v) && v > 0
			? v
			: 0;
	private bool _ownPathMoving;

	private StarveClient? _client;
	private TileMap? _tilemap;
	private Node2D? _worldPivot;
	private Node2D? _world;
	private MapView? _mapView;
	private IWorldRenderer? _worldRenderer;
	private World3DView? _world3D;
	private CloudShadowView? _clouds;
	private ParallaxView? _parallax;
	private WeatherView? _weather;
	private FogGrid? _fogGrid;
	private MinimapView? _minimap;
	private LightingPass? _lighting;
	private LutPass? _lut;
	private VolumetricView? _volumetric;
	private GhostNode? _ghost;
	private Control? _uiRoot;
	private Hud? _hud;
	private ToonTunePanel? _toonPanel;
	private ActorTunePanel? _actorPanel;
	private PerfMonitor? _perf;
	private PerfPanel? _perfPanel;
	private float _lastEffectiveSpeed = OwnMovementSim.DefaultTilesPerSec;
	private SfxService? _sfx;
	private DamageFlashOverlay? _damageFlash;
	private MoveController? _moveController;
	private int _lastRevision = -1;
	private int _lastWeatherRevision = -1;
	private long? _captureAt;
	private ulong _ownId;
	private string _ownUid = "";
	private ulong? _selected;
	private (ulong EntityId, int Kind, int W, int H, bool Ok)? _buildPreview;
	private System.Numerics.Vector2? _mouseWorld;
	private long _lastBuildCheckAt;
	private long _lightningAmbientUntil;
	private readonly bool _freeCamera = CameraArg is not null;
	private readonly bool _render3D = Render3DMode;
	private readonly AutoActionInputState _autoActions = new();
	private long _demoNextAt;
	private float _viewRotation;
	private int _blockerSignature = int.MinValue;
	private int _hudSignature = int.MinValue;
	private readonly bool _showMovementDiagnostics =
		System.Environment.GetEnvironmentVariable("STARVE_DEBUG_MOVEMENT") == "1";
	private MovementDiagnosticsSampler? _movementDiagnosticsSampler;
	private string _movementDiagnosticsStatus = "";
	private bool _ownDead;
	private bool _gameplayLocked;
	private Vector2 _uiRootSize;
	private readonly Dictionary<ulong, (float X, float Y)> _lootAt = new();

	/// <summary>道具图标（equipment/ 集）：kind → 资源路径；没有图标的物品继续用色块。</summary>
	private static readonly Dictionary<int, string> ItemIconFiles = new()
	{
		[(int)ItemKind.Axe] = "res://assets/equipment/wood/axe.png",
		[(int)ItemKind.Pickaxe] = "res://assets/equipment/wood/chisel.png", // 凿子充当镐图标
		[(int)ItemKind.WoodArmor] = "res://assets/equipment/wood/armor.png",
		[(int)ItemKind.Helmet] = "res://assets/equipment/wood/helmet.png",
	};
	private static readonly Dictionary<int, Texture2D> ItemIconCache = new();

	private static Texture2D? ItemIcon(int kind)
	{
		if (!ItemIconFiles.TryGetValue(kind, out var path)) return null;
		if (!ItemIconCache.TryGetValue(kind, out var tex))
		{
			tex = GD.Load<Texture2D>(path);
			ItemIconCache[kind] = tex;
		}
		return tex;
	}

	/// <summary>服务端地址：缺省本地网关；用 STARVE_GATE_URL 指向别的端口/机器（与 ProtocolSmoke 一致）。</summary>
	private static string GateUrl =>
		System.Environment.GetEnvironmentVariable("STARVE_GATE_URL") ?? "ws://localhost:8081/ws";

	private static bool SmokeMode => OS.GetCmdlineUserArgs().Contains("--smoke");
	private static string? CapturePath => OS.GetCmdlineUserArgs()
		.SkipWhile(a => a != "--capture")
		.Skip(1)
		.FirstOrDefault();
	private static string? CameraArg => OS.GetCmdlineUserArgs()
		.SkipWhile(a => a != "--cam")
		.Skip(1)
		.FirstOrDefault();
	/// <summary>
	/// 默认走 3D 主场景（玩家为猪人）。加 --render-2d 或 STARVE_RENDER_2D=1 回到 2D 鱼人。
	/// </summary>
	private static bool Render3DMode =>
		!OS.GetCmdlineUserArgs().Contains("--render-2d") &&
		System.Environment.GetEnvironmentVariable("STARVE_RENDER_2D") != "1";
	/// <summary>
	/// 演示/截图辅助：STARVE_DEMO_MOVE="dx,dy" 时按住方向自动走（本地预测 + 服务端命令）。
	///
	/// 再加 STARVE_DEMO_PATROL_MS=3000 就变成**原地往返**：每 3 秒把方向取反。
	/// 为什么需要：直线走会把角色顶到地图边界/障碍上，"贴墙推"那段的位移本来就是 0，
	/// 会把"走路顺不顺"的逐帧统计污染成一半零帧（实测踩过：边界上 599 帧全 0）。
	/// 往返则一直留在开阔地里走，同时天然覆盖"反复转向"这个最容易露出抖动的情形。
	/// </summary>
	private static (int Dx, int Dy)? DemoMove =>
		System.Environment.GetEnvironmentVariable("STARVE_DEMO_MOVE") is { } s &&
		s.Split(',') is { Length: 2 } parts &&
		int.TryParse(parts[0], out var dx) && int.TryParse(parts[1], out var dy)
			? (dx, dy)
			: null;

	public override void _Ready()
	{
		// Godot 内建 Bloom：2D 用全屏 Environment；3D 的 glow 挂在 World3DView 的日夜环境上，避免两套环境抢天空。
		if (!_render3D)
		{
			var env = new Godot.Environment();
			env.GlowEnabled = true;
			env.GlowIntensity = 0.9f;
			env.GlowStrength = 1.1f;
			env.GlowBloom = 0.12f;
			env.GlowHdrThreshold = 0.55f;
			AddChild(new WorldEnvironment { Environment = env });
		}

		_parallax = new ParallaxView { Name = "Parallax" };
		AddChild(_parallax);
		_worldPivot = new Node2D { Name = "WorldPivot" };
		AddChild(_worldPivot);
		_world = new Node2D { Name = "World" };
		_worldPivot.AddChild(_world);
		_mapView = new MapView { Name = "MapView" };
		_world.AddChild(_mapView);
		_clouds = new CloudShadowView { Name = "CloudShadows" };
		_world.AddChild(_clouds);
		_sfx = new SfxService();
		AddChild(_sfx);
		_sfx.SetSpatialRoot(_world);
		if (_render3D)
		{
			_world3D = new World3DView();
			AddChild(_world3D);
			_worldRenderer = _world3D.Entities;
			// 注入同一条投掷跟踪器：ghost 与权威实体必须共用一份进度，
			// 否则自己投掷交接时权威从 elapsed≈0 起步，画面向后跳半格（见字段注释）。
			_world3D.Entities.SetThrowFlights(_throwFlights);
			_worldPivot.Visible = false;
		}
		else
		{
			var entityLayer = new EntityLayer { Name = "EntityLayer" };
			_world.AddChild(entityLayer);
			_worldRenderer = entityLayer;
		}
		_worldRenderer.SetSfx(_sfx);
		_fogGrid = new FogGrid { Name = "FogGrid" };
		_world.AddChild(_fogGrid);
		_ghost = new GhostNode { Name = "Ghost", ZIndex = 4096, Visible = false };
		_world.AddChild(_ghost);

		_weather = new WeatherView { Name = "Weather" };
		_weather.OnLightning += () => _lightningAmbientUntil = NowMs() + 350;
		AddChild(_weather);
		_lighting = new LightingPass { Name = "Lighting" };
		AddChild(_lighting);
		_lut = new LutPass { Name = "Lut" };
		_lut.SetAtlas(LutBuilder.Build().Atlas);
		AddChild(_lut);
		_volumetric = new VolumetricView { Name = "Volumetric" };
		AddChild(_volumetric);
		if (_render3D)
		{
			if (_parallax is not null) _parallax.Visible = false;
			_lighting.Visible = false;
			_volumetric.Visible = false;
			GD.Print("RENDER 3D main scene, player=pigman");
			WarmUpShaders();
		}

		var ui = new CanvasLayer { Layer = 10 };
		AddChild(ui);
		// CanvasLayer 不是 Control：子控件的锚点不会跟窗口走，底栏会算到屏幕外。
		_uiRoot = new Control
		{
			Name = "UiRoot",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		ui.AddChild(_uiRoot);
		// CanvasLayer 不是 Control，子节点 FullRect 不会自动吃到窗口。必须把 UiRoot.Size 写成视口大小。
		GetViewport().SizeChanged += FitUiRoot;
		_minimap = new MinimapView { Name = "Minimap" };
		_uiRoot.AddChild(_minimap);
		_damageFlash = new DamageFlashOverlay { Name = "DamageFlash" };
		_uiRoot.AddChild(_damageFlash);
		try
		{
			_hud = new Hud { Name = "Hud" };
			_uiRoot.AddChild(_hud);
			WireHud(_hud);
			FitUiRoot();
			CallDeferred(MethodName.FitUiRoot);
			if (_render3D) _hud.Log("渲染：3D 主场景 · 玩家=猪人（--render-2d 回 2D）");
			if (_render3D && OS.IsDebugBuild())
			{
				_toonPanel = new ToonTunePanel
				{
					CollectActors = () => _world3D!.Entities.Visuals,
					TerrainRoot = _world3D!.Terrain,
				};
				_uiRoot.AddChild(_toonPanel);
				_toonPanel.Visible = false;
				_actorPanel = new ActorTunePanel { World = _world3D };
				_actorPanel.MoveSpeedChanged = ApplyDebugMoveSpeed;
				_uiRoot.AddChild(_actorPanel);
				_actorPanel.Visible = false;
				_hud.Log("调试面板：F1 Toon，F2 表现，默认关闭（打开才占 draw call）");
			}

			try
			{
				_perf = PerfMonitor.TryStart();
			}
			catch (Exception ex)
			{
				GD.PushWarning("性能采样未启动: " + ex.Message);
				_perf = null;
			}
			_perfPanel = new PerfPanel();
			_perfPanel.TerrainToggled = on =>
			{
				if (_world3D is not null) _world3D.Terrain.Visible = on;
			};
			_perfPanel.CloudsToggled = on => _world3D?.SetCloudsVisible(on);
			_perfPanel.PlantsToggled = on => _world3D?.Entities.SetPlantsVisible(on);
			_perfPanel.GlowToggled = on => _world3D?.SetGlowEnabled(on);
			_uiRoot.AddChild(_perfPanel);
			_perfPanel.Visible = false;
			_perfPanel.Bind(_perf);
			if (_perf?.Url is { } perfUrl)
				_hud.Log($"性能：F3 打开面板 · 网页 {perfUrl} · 日志 {_perf.LogPath}");
			else
				_hud.Log("性能：F3 打开面板（本局未写日志）");
		}
		catch (Exception ex)
		{
			GD.PushError("UI 初始化失败: " + ex.Message);
		}

		AddChild(new CameraController { Camera = _camera });
		var move = new MoveController();
		_moveController = move;
		// 组件模式（默认）：预测/和解/追步全在组件里（序号锚定）；旧实现保留作回退对照。
		_ownSim = ComponentOwnMovementSim.Enabled
			? new ComponentOwnMovementSim(IsWalkable)
			: new OwnMovementSim(IsWalkable);

		// 指标出口：业务实现 INetcodeMetrics，把每次和解接到自己的日志（逐次因果链，见 NetcodeMetrics）。
		if (_ownSim is ComponentOwnMovementSim componentSim && componentSim.Smoother is { } smoother)
		{
			try
			{
				_netcodeMetrics = new NetcodeMetrics(
					System.IO.Path.Combine(OS.GetUserDataDir(), PerfMonitor.DirName));
				smoother.Metrics = _netcodeMetrics;
				// 统一序号：攻击/合成等离散操作也走组件那一条输入流（否则服务端按序号连续消费会卡在缺口上）。
				if (_client is not null)
					_client.Commands.SeqSource = () => smoother.ReserveDiscreteOp(NowMs());
			}
			catch (Exception ex)
			{
				GD.PushWarning("netcode 指标日志未启动: " + ex.Message);
			}
		}
		// 调试：把服务端下发的简化碰撞体画出来（GATE_DEBUG_COLLISION=1 才有数据）
		_debugShapes = new DebugShapeLayer3D();
		AddChild(_debugShapes);
		// 投掷瞄准层挂在世界根下（与世界坐标系一致，便于用 WorldTo3D 直接摆点）
		_blastFx = new BlastFxLayer3D { Name = "BlastFx" };
		_world.AddChild(_blastFx);
		_throwAim = new ThrowAimLayer3D { Name = "ThrowAim" };
		_world.AddChild(_throwAim);
		// 本地预测的 ghost 炸弹（起手期间服务端还没有可渲染的实体）
		_throwFlight = new ThrowFlightLayer3D { Name = "ThrowFlight" };
		_world.AddChild(_throwFlight);
		if (_showMovementDiagnostics)
		{
			_movementDiagnosticsSampler = new MovementDiagnosticsSampler(
				() => _ownSim?.Diagnostics ?? default);
		}
		move.OnMove += dir =>
		{
			// 组件模式：移动上行由"每 tick 发未确认窗口"负责（见 _Process），
			// 这里的"变化时发一条"会让两套 seq 打架，必须关掉。
			if (!ComponentOwnMovementSim.Enabled && !GameplayLocked())
				_client?.Commands.Move(dir.Dx, dir.Dy);
		};
		move.OnIntent += dir =>
		{
			if (GameplayLocked())
			{
				_ownSim?.SetIntent(0, 0);
				_worldRenderer?.SetOwnMoveDir(0, 0);
				_worldRenderer?.SetOwnFacing(0f, 0f);
				_ownIntentMoving = false;
				return;
			}
			_ownSim?.SetIntent(dir.Dx, dir.Dy);
			_worldRenderer?.SetOwnMoveDir(dir.Dx, dir.Dy);
			if (dir.Dx != 0 || dir.Dy != 0)
			{
				_worldRenderer?.CancelActionForMovement(_ownId);
			}
			// 自己的动画严格跟随本地输入，松键立即 idle；服务端位置只负责校正。
			_ownIntentMoving = dir.Dx != 0 || dir.Dy != 0;
		};
		move.OnFacing += face =>
		{
			if (!GameplayLocked())
				_worldRenderer?.SetOwnFacing(face.X, face.Y);
		};
		AddChild(move);
		if (System.Environment.GetEnvironmentVariable("STARVE_DEMO_ROTATE") is { } rr &&
			float.TryParse(rr, out var deg))
		{
			RotateView(deg * Mathf.Pi / 180f);
		}

		_hud?.Log("连接中…");
		_ = StartAsync();
	}

	public override void _ExitTree()
	{
		var vp = GetViewport();
		if (vp is not null) vp.SizeChanged -= FitUiRoot;
		_perf?.Dispose();
		_perf = null;
		_netcodeMetrics?.Dispose();
		_netcodeMetrics = null;
		OwnLocoTrace.Finish();
	}

	private void FitUiRoot()
	{
		if (_uiRoot is null) return;
		var size = GetViewport().GetVisibleRect().Size;
		if (size.X < 2 || size.Y < 2)
		{
			var win = GetWindow();
			if (win is not null) size = win.Size;
		}
		if (size.X < 2 || size.Y < 2)
		{
			CallDeferred(MethodName.FitUiRoot);
			return;
		}
		if (_uiRoot.Size.DistanceSquaredTo(size) < 1f && _uiRoot.Position == Vector2.Zero)
			return;
		_uiRootSize = size;
		_uiRoot.Position = Vector2.Zero;
		_uiRoot.Size = size;
		_hud?.Relayout();
	}

	private void WireHud(Hud hud)
	{
		hud.GatherPressed += () => WithSelected(id => TryAct(id, Intent.Gather));
		hud.AttackPressed += () => WithSelected(id => TryAct(id, Intent.Attack));
		hud.ChopPressed += () => WithSelected(id => TryAct(id, Intent.Chop));
		hud.MinePressed += () => WithSelected(id => TryAct(id, Intent.Mine));
		hud.PickupPressed += () => WithSelected(id => TryAct(id, Intent.Pickup));
		hud.DemolishPressed += () =>
		{
			if (CanSendGameplay()) WithSelected(id => _client?.Commands.Demolish(id));
		};
		hud.BuildPressed += kind =>
		{
			if (CanSendGameplay()) _ = DoBuildAsync(kind);
		};
		hud.BagUsePressed += slot =>
		{
			if (CanSendGameplay()) WithBagSlot(slot, kind => _client?.Commands.Use(kind));
		};
		hud.BagEquipPressed += slot => WithBagSlot(slot, kind =>
		{
			if (!CanSendGameplay()) return;
			// 背包始终按 kind 装备：同槽替换（换斧只换手持）。kind=0 是卸下，不能从背包发出。
			_client?.Commands.Equip(kind);
		});
		hud.WornSlotUnequipPressed += slotId =>
		{
			if (!CanSendGameplay() || WornItem(slotId) is null) return;
			_client?.Commands.UnequipSlot(EquipSlotNumber(slotId));
		};
		hud.BagDropPressed += slot => WithBagSlot(slot, kind =>
		{
			if (!CanSendGameplay()) return;
			var count = OwnItemCount(slot);
			if (count > 0) _client?.Commands.Drop(kind, count);
		});
		hud.BagSplitPressed += slot => WithBagSlot(slot, kind =>
		{
			if (!CanSendGameplay()) return;
			var count = OwnItemCount(slot);
			if (count > 1) _client?.Commands.Split(slot, count / 2);
		});
		hud.CraftPressed += recipeId =>
		{
			if (CanSendGameplay()) _ = DoCraftAsync(recipeId);
		};
		hud.CancelCraftPressed += () =>
		{
			if (CanSendGameplay()) _client?.Commands.CancelCraft();
		};
		hud.SleepPressed += () =>
		{
			if (_client is null || _ownDead || GameplayLocked()) return;
			var command = _client.Commands.Sleep();
			_worldRenderer?.PredictAction(_ownId, ActionKind.Sleep, command);
		};
		hud.CancelSleepPressed += () =>
		{
			if (_client is null || _ownDead || GameplayLocked()) return;
			_client.Commands.CancelSleep();
			_worldRenderer?.CancelActionLocally(_ownId);
		};
		hud.UiClicked += () => _sfx?.Play("sfx.ui.click");
		hud.CraftOpened += () => _sfx?.Play("sfx.ui.craft.open");
	}

	private async Task StartAsync()
	{
		_client = new StarveClient();
		_client.World.ActionOutcomeReceived += outcome => _actionOutcomes.Enqueue(outcome);
		_client.World.WorldEventReceived += worldEvent => _worldEvents.Enqueue(worldEvent);
		try
		{
			var uid = System.Environment.GetEnvironmentVariable("STARVE_UID") ?? "42";
			_ownUid = uid;
			var info = await _client.ConnectAsync(GateUrl, DevTokens.Mint(uid));
			_ownId = info.EntityId;
			_worldRenderer?.SetOwnId(_ownId);
			_worldRenderer?.SetNameProvider(EntityName);
			if (CameraArg is { } cam && cam.Split(',') is { Length: 2 } parts &&
				float.TryParse(parts[0], out var cx) && float.TryParse(parts[1], out var cy))
			{
				_camera.Teleport(cx, cy);
			}
			_hud?.Log($"[已连接] uid={info.UserId} entity={info.EntityId}");
		}
		catch (Exception ex)
		{
			_hud?.Log($"[连接失败] {ex.Message}");
		}
	}

	/// <summary>
	/// 预热自定义着色器：把 8 个 .gdshader 全部编译一遍。
	///
	/// 为什么需要：Godot 的**自定义着色器不进磁盘缓存**
	/// （缓存目录里只有 75 个引擎内置管线），每次启动都要从源码重新编译。
	/// 实测代价：启动后前 **5～28 秒**帧率被锁在 30 FPS
	/// （frameMs 精确等于 33.33ms、Godot CPU 打到 100%），
	/// 编译完成后才恢复到 70+。这段时间正好覆盖"刚进游戏想走两步"的时机，
	/// 玩家感受就是"一进去就卡"。
	///
	/// 做法：建一个离屏节点、把每个着色器挂上去并塞进场景树一帧，
	/// 迫使渲染器编译它 —— 这样编译发生在**进入游戏前**，
	/// 而不是在玩家走动时逐帧触发。
	///
	/// 注：这是把卡顿前移，不是消除（编译总量不变）。
	/// 真正的消除要靠预编译管线缓存，Godot 目前不提供。
	/// </summary>
	private void WarmUpShaders()
	{
		var paths = new[]
		{
			ShaderLibrary.TerrainHeightBlend,
			ShaderLibrary.CloudVolume,
			ShaderLibrary.CloudShadow,
			ShaderLibrary.CloudSky,
			ShaderLibrary.Fire,
			ShaderLibrary.AlchemyBounce,
			ShaderLibrary.LiquidRise,
			ShaderLibrary.PanoramaTint,
		};
		var compiled = 0;
		foreach (var path in paths)
		{
			Shader res;
			try { res = ShaderLibrary.Load(path); }
			catch (Exception ex) { GD.PushWarning($"着色器预热跳过 {path}: {ex.Message}"); continue; }
			// 挂一个最小 mesh 上去：光是 new ShaderMaterial 不会触发编译，
			// 必须有实际绘制才会让渲染器走到 compile 分支。
			var mat = new ShaderMaterial { Shader = res };
			var mi = new MeshInstance3D
			{
				Mesh = new QuadMesh { Size = new Vector2(0.001f, 0.001f) },
				MaterialOverride = mat,
				Visible = false,
				Position = new Vector3(0, -10000, 0),
			};
			AddChild(mi);
			mi.QueueFree();
			compiled++;
		}
		GD.Print($"着色器预热: {compiled}/{paths.Length} 个自定义着色器已提交编译");
	}

	public override void _Process(double delta)
	{
		_perf?.Tick(delta);
		if (_perfPanel is { Visible: true })
			_perfPanel.Render(
				_perf?.Latest ?? default,
				(float)Engine.GetFramesPerSecond(),
				_perf?.FrameTime ?? default);

		if (CapturePath is not null)
		{
			var delay = 3000;
			if (System.Environment.GetEnvironmentVariable("STARVE_CAPTURE_MS") is { } ms &&
				int.TryParse(ms, out var custom)) delay = custom;
			if (_captureAt is null) _captureAt = NowMs() + delay;
			if (NowMs() >= _captureAt)
			{
				var img = GetViewport().GetTexture().GetImage();
				img.SavePng(CapturePath);
				GD.Print($"CAPTURE saved: {CapturePath}");
				GetTree().Quit();
				return;
			}
		}

		var client = _client;
		if (client is null) return;

		while (_actionOutcomes.TryDequeue(out var outcome))
		{
			_worldRenderer?.ApplyActionOutcome(outcome);
			if (outcome.EntityId != _ownId) continue;
			// 投掷被拒/取消：撤掉本地预告的 ghost，否则地面会留下一颗"假炸弹"。
			// 只认自己刚发的那个 request_id；服务端若不给 Throw 的 outcome（匹配不到），
			// 由跟踪器的落地滞留超时兜底（ThrowFlightTracker.GhostLingerSeconds）。
			if (outcome.Kind == ActionKind.Throw &&
				outcome.RequestId != 0 &&
				outcome.RequestId == _throwRequestId &&
				outcome.Result is ActionOutcomeResult.Rejected or ActionOutcomeResult.Canceled)
			{
				_throwFlights.CancelOwnGhost();
				_throwFlight?.Clear();
			}
			if (outcome.Result == ActionOutcomeResult.Completed &&
				outcome.Kind == ActionKind.Craft)
			{
				_sfx?.Play("sfx.ui.craft.done");
			}
			if (outcome.Result is ActionOutcomeResult.Canceled or ActionOutcomeResult.Rejected)
			{
				var result = outcome.Result == ActionOutcomeResult.Canceled ? "动作已取消" : "动作被拒绝";
				_hud?.Log($"{result}：{ActionOutcomeReasonText(outcome.Reason)}");
				if (outcome.Result == ActionOutcomeResult.Rejected) _sfx?.Play("sfx.ui.deny");
			}
		}

		if (client.World.Revision != _lastRevision)
		{
			_lastRevision = client.World.Revision;
			if (MoveTrace.Enabled)
			{
				var wt = client.World.WorldTick;
				MoveTrace.NoteApply(wt, _lastAppliedTick);
				_lastAppliedTick = wt;
			}
			ApplyWorld(client.World);
			try
			{
				RefreshOwnVitals(client.World);
			}
			catch (Exception ex)
			{
				GD.PushError($"HUD vitals: {ex.Message}");
			}
		}
		while (_worldEvents.TryDequeue(out var worldEvent))
		{
			if (worldEvent.Impact is { } impact)
			{
				_worldRenderer?.ApplyCombatImpact(worldEvent, impact);
				_damageFlash?.ApplyImpact(
					impact.Result,
					impact.TargetEntity == _ownId);
			}
			else if (worldEvent.Blast is { } blast)
			{
				// 服务端只给"在哪炸、多大"，表现全在客户端。
				var ground = _tilemap?.HeightAt(blast.X, blast.Y) ?? 0f;
				_blastFx?.Spawn(blast.X, blast.Y, blast.Radius, ground);
				// 自己在爆炸里 → 轻微震屏/提示（伤害本身由 HealthChanged 单独下发）
				if (blast.SourceEntity == _ownId)
					_hud?.Log($"炸弹爆炸：半径 {blast.Radius:0.0} 格");
			}
			else if (worldEvent.HealthChanged is { } healthChanged &&
					 healthChanged.TargetEntity == _ownId &&
					 healthChanged.Delta != 0)
			{
				var sign = healthChanged.Delta > 0 ? "+" : "";
				_hud?.Log(
					$"生命 {sign}{healthChanged.Delta}（{HealthChangeCauseText(healthChanged.Cause)}）");
			}
		}

		RefreshGameplayLock();
		var now = NowMs();
		if (!_gameplayLocked) _autoActions.Tick(now, TriggerAutoAction);
		if (!_gameplayLocked && DemoMove is { } dm && now >= _demoNextAt)
		{
			_demoNextAt = now + 100;
			if (DemoPatrolMs > 0 && now >= _demoPatrolAt)
			{
				_demoPatrolAt = now + DemoPatrolMs;
				_demoSign = -_demoSign;
			}
			var ddx = dm.Dx * _demoSign;
			var ddy = dm.Dy * _demoSign;
			// 组件模式下移动上行只走"每 tick 发未确认窗口"，这里不能再发（两套 seq 会打架）；
			// 演示模式的意图仍然要设置给本地预测。
			if (!ComponentOwnMovementSim.Enabled) _client?.Commands.Move(ddx, ddy);
			_ownSim?.SetIntent(ddx, ddy);
			_worldRenderer?.SetOwnMoveDir(ddx, ddy);
			// 与真实按键一致：置位"本地意图在动"。否则 ApplyWorld 每 20Hz 会用
			// 服务端 Path（演示模式为空）把本地意图覆盖成 (0,0)，表现成一顿一顿。
			_ownIntentMoving = ddx != 0 || ddy != 0;
			if (ddx != 0 || ddy != 0) _worldRenderer?.CancelActionForMovement(_ownId);
		}
		var canPredict = _client is { } predictionClient &&
			predictionClient.Transport.IsConnected &&
			predictionClient.Commands.CanPredictMovement;
		var ownBefore = _ownSim?.Position ?? default;
		if (canPredict)
		{
			_ownSim?.Tick((float)(delta * 1000), now);

			// 上行：每个 tick 把"未确认的操作窗口"整批发出去（冗余：丢包不丢输入，
			// 服务端按 seq 去重排序）。组件模式下这是唯一的移动上行通道 ——
			// 变化事件那条（Commands.Move）要关掉，否则两套序号会打架。
			if (_ownSim is ComponentOwnMovementSim comp && _client is not null
				&& _client.Transport.IsConnected)
			{
				var n = comp.CollectUnackedOps(_ownUnacked, comp.Config.RedundantOps);
				if (n > 0)
				{
					if (n > _ownUnackedWire.Length)
						Array.Resize(ref _ownUnackedWire, n);
					for (var i = 0; i < n; i++)
						_ownUnackedWire[i] = new Starve.Protocol.MoveOp(
							_ownUnacked[i].Seq, _ownUnacked[i].Action.Dx, _ownUnacked[i].Action.Dy);
					_client.Commands.SendMoveOps(_ownUnackedWire.AsSpan(0, n));
				}
			}
		}
		if (_ownSim is ComponentOwnMovementSim clockSim)
		{
			_netcodeMetrics?.NoteStep(clockSim.Smoother.LastStepDistance, clockSim.LastSlope, clockSim.Predictor.EffectiveSpeed);
			// 渲染时基探针：逐帧记下"这一帧用的是 tick 轴上的哪一点"。
			// 如果它每帧的增量不等于 dt/50ms，那渲染位置就会跟着一顿一顿 —— 与校正无关。
			OwnLocoTrace.NoteClock(
				clockSim.Smoother.Clock.TickAt(now),
				clockSim.Smoother.RenderOffsetDistance,
				clockSim.Smoother.CorrectionBlendWeight);
		}
		if (MoveTrace.Enabled && _ownSim is { } traceSim)
		{
			var intent = traceSim.Intent;
			MoveTrace.OwnFrame(
				canPredict,
				canPredict && traceSim.Position != ownBefore,
				_client?.Commands.PendingControlCount ?? 0,
				_client?.Commands.LastAcceptedSeq ?? 0,
				intent.Dx, intent.Dy, traceSim.LastSlope);
		}
		if (_movementDiagnosticsSampler?.TrySample(now, out var diagnostics, out var changed) == true)
		{
			_movementDiagnosticsStatus =
				$"\n预测误差 last={diagnostics.LastReconciliationError:0.000}" +
				$" max={diagnostics.MaxReconciliationError:0.000}" +
				$" soft={diagnostics.SoftCorrections} hard={diagnostics.HardSnaps}" +
				$"\n输入 epoch={_client?.Commands.InputEpoch ?? 0}" +
				$" sent={_client?.Commands.LastSentSeq ?? 0}" +
				$" ack={_client?.Commands.LastAcceptedSeq ?? 0}" +
				$" pending={_client?.Commands.PendingControlCount ?? 0}";
			if (changed)
			{
				GD.Print(
					$"MOVEMENT_DIAGNOSTICS last={diagnostics.LastReconciliationError:0.000} " +
					$"max={diagnostics.MaxReconciliationError:0.000} " +
					$"soft={diagnostics.SoftCorrections} hard={diagnostics.HardSnaps}");
			}
		}
		System.Numerics.Vector2? own = _ownSim is { Has: true } sim
			? new System.Numerics.Vector2(sim.Position.X, sim.Position.Y)
			: null;
		if (!_freeCamera) _camera.Follow(own?.X, own?.Y);
		_camera.Tick((float)(delta * 1000));
		if (MoveTrace.Enabled) MoveTrace.FrameBegin(now, _ownId, _camera.CenterX(), _camera.CenterY());
		if (_render3D)
		{
			var orbit = 0f;
			if (Input.IsPhysicalKeyPressed(Key.Q)) orbit -= 1f;
			if (Input.IsPhysicalKeyPressed(Key.E)) orbit += 1f;
			if (orbit != 0f)
				RotateView(orbit * MathF.PI / 2f * (float)delta);
		}

		var viewport = GetViewport().GetVisibleRect().Size;
		if (!_freeCamera)
		{
			// 相机半径是 [view_radius, view_radius_max]；view_preload 只在服务端多下发。
			var worldCfg = client.World.Config;
			_camera.SetViewRange(
				worldCfg?.ViewRadius ?? Camera.DefaultViewRadius,
				worldCfg?.ViewRadiusMax ?? 0);
			_camera.SyncToViewport(viewport.X, viewport.Y);
		}
		var hCam = _tilemap?.HeightAt(_camera.CenterX(), _camera.CenterY()) ?? 0;
		if (_render3D && _world3D is not null)
		{
			_world3D.SyncView(
				_camera.CenterX(), _camera.CenterY(), hCam,
				_camera.ZoomLevel, _viewRotation, viewport);
		}
		else
		{
			// Pivot 固定在屏幕中心，WorldContent 抵消相机中心投影：
			// Q/E 旋转 Pivot 时，玩家始终留在屏幕中心。
			var camLocal = IsoMath.WorldToLocal(_camera.CenterX(), _camera.CenterY(), hCam);
			_worldPivot!.Position = viewport / 2;
			_worldPivot.Rotation = _viewRotation;
			_worldPivot.Scale = Vector2.One * _camera.ZoomLevel;
			_world!.Position = new Vector2(-camLocal.X, -camLocal.Y);
			_world.Scale = Vector2.One;

			var fx = (_camera.CenterX() - _camera.CenterY()) * IsoMath.Step * _camera.ZoomLevel;
			var fy = ((_camera.CenterX() + _camera.CenterY()) * IsoMath.Step / 2 - hCam * IsoMath.Step) *
					 _camera.ZoomLevel;
			_parallax!.UpdateParallax(fx, fy, viewport);
		}

		if (client.World.Revision != _lastWeatherRevision)
		{
			_lastWeatherRevision = client.World.Revision;
			var w = client.World.Weather;
			_weather!.SetWeather(w?.Rain ?? 0, w?.Fog ?? 0, client.World.Season, viewport);
			if (_tilemap is not null) _fogGrid!.SetFog(client.World.WeatherFrame, _tilemap);
			UpdateLut(client.World.DayLight);
		}

		_weather!.Tick(delta, viewport);
		_lut!.Size = viewport;
		if (!_render3D)
		{
			UpdateLighting(client.World, viewport, _camera.ZoomLevel, own);
			_lighting!.Size = viewport;
			var fires = new List<Vector2>();
			var seeds = new List<long>();
			foreach (var view in client.World.Entities.Values)
			{
				var p = view.Get("Position", Starve.Game.V1.Position.Parser);
				if (p is null) continue;
				var ws = view.Get("Workstation", Workstation.Parser);
				var bld = view.Get("Building", Building.Parser);
				var isFire = (ws is not null && (int)ws.Type == 1) ||
							 (bld is not null && bld.Placed && (int)bld.Kind == 1);
				if (isFire)
				{
					fires.Add(new Vector2(p.X, p.Y));
					seeds.Add((long)view.EntityId);
				}
			}
			_volumetric!.SetView(_camera, fires.ToArray(), seeds.ToArray(), viewport, client.World.DayLight, _camera.ZoomLevel);
		}
		if (_buildPreview is not null && _mouseWorld is not null) UpdateGhost();

		// 投掷预览每帧跟随玩家（起点会随移动变化）
		if (_throwAiming) RefreshThrowPreview();

		// 投掷飞行：先按帧推进本地时间（把 20Hz 权威采样补成 60FPS 连续），
		// 再把样本交给 ghost 层（它只画 IsGhost 的那条）。
		// 权威飞行体不在这里画：EntityLayer3D 用自己的跟踪器推进并摆放实体节点。
		_throwFlights.Tick(delta);
		_throwFlight?.Show(_throwFlights.Samples(), HeightAt);

		_worldRenderer!.UpdatePositions(
			_smoothers,
			id => id == _ownId
				? _ownIntentMoving || _ownPathMoving
				: _locomotionMoving.GetValueOrDefault(id),
			now,
			own);
		if (OwnLocoTrace.Expired(now))
		{
			OwnLocoTrace.Finish();
			GD.Print("LOCO 采集结束，自动退出（避免测试进程一直挂在 gate 上）");
			GetTree().Quit();
			return;
		}
		if (MoveTrace.Enabled)
		{
			MoveTrace.FrameEnd(now, delta * 1000.0);
			if (MoveTrace.ShouldQuit(now))
			{
				MoveTrace.Finish();
				OwnLocoTrace.Finish();
				GetTree().Quit();
				return;
			}
		}
		_debugShapes?.UpdatePositions(_world3D?.Entities);
		_worldRenderer.SetDayLight(client.World.DayLight);
		if (_render3D && _world3D is not null)
		{
			var rain = client.World.Weather?.Rain ?? 0f;
			_world3D.SetDayCycle(
				client.World.DayLight,
				client.World.Season,
				rain,
				NowMs() < _lightningAmbientUntil);
			var fires = new List<(float X, float Y, float H)>();
			foreach (var view in client.World.Entities.Values)
			{
				var style = EntityVisual.StyleFor(view);
				// 熄灭的火堆不该再往地上打点光：火焰与光照都跟随 HeatSource（IsLit）。
				if (!style.IsFire || !style.IsLit) continue;
				var p = view.Get("Position", Starve.Game.V1.Position.Parser);
				if (p is null) continue;
				fires.Add((p.X, p.Y, _tilemap?.HeightAt(p.X, p.Y) ?? 0f));
			}
			var ox = own?.X ?? _camera.CenterX();
			var oy = own?.Y ?? _camera.CenterY();
			_world3D.SyncPointLights(fires, ox, oy, _tilemap?.HeightAt(ox, oy) ?? 0f);
		}
		_minimap!.SetView(
			client.World.Entities,
			new Vector2(_camera.CenterX(), _camera.CenterY()),
			_camera.ZoomLevel,
			viewport);
		try
		{
			UpdateHud();
		}
		catch (Exception ex)
		{
			GD.PushError($"HUD: {ex.Message}");
		}
	}

	private void UpdateLighting(WorldService world, Vector2 viewport, float zoom, System.Numerics.Vector2? own)
	{
		var dayLight = world.DayLight;
		var dark = Mathf.Max(0, 1 - dayLight * 2);
		var sunT = 1 - dark;
		var ambient = 0.92f - dark * 0.3f;
		if (NowMs() < _lightningAmbientUntil) ambient += 0.5f;
		if (world.Weather is { Rain: > 0.15f }) ambient *= 0.93f;
		var sunColor = new Color(
			0.3f * (0.33f + 0.67f * sunT),
			0.29f * (0.41f + 0.59f * sunT),
			0.26f * (0.62f + 0.38f * sunT));
		var fogColor = new Color(
			0.62f * (0.14f + 0.86f * sunT),
			0.7f * (0.15f + 0.85f * sunT),
			0.78f * (0.2f + 0.8f * sunT));

		var lightPos = new List<Vector2>();
		var lightColor = new List<Color>();
		var lightRadius = new List<float>();
		foreach (var view in world.Entities.Values)
		{
			var p = view.Get("Position", Starve.Game.V1.Position.Parser);
			if (p is null) continue;
			var ws = view.Get("Workstation", Workstation.Parser);
			var bld = view.Get("Building", Building.Parser);
			var isFire = (ws is not null && (int)ws.Type == 1) ||
						 (bld is not null && bld.Placed && (int)bld.Kind == 1);
			if (isFire)
			{
				lightPos.Add(new Vector2(p.X, p.Y));
				lightColor.Add(new Color(1.65f, 0.95f, 0.45f));
				lightRadius.Add(9f);
			}
		}
		if (own is { } ownPos)
		{
			lightPos.Add(new Vector2(ownPos.X, ownPos.Y));
			lightColor.Add(new Color(1f, 0.85f, 0.6f));
			lightRadius.Add(3.5f);
		}
		while (lightPos.Count > 8) lightPos.RemoveAt(lightPos.Count - 1);

		_lighting!.SetLights(
			viewport,
			zoom,
			ambient,
			new Vector2(0.707f, -0.707f),
			sunColor,
			fogColor,
			0.012f,
			lightPos.ToArray(),
			lightColor.ToArray(),
			lightRadius.ToArray());
	}

	private void UpdateLut(float dayLight)
	{
		var dark = Mathf.Max(0, 1 - dayLight * 2);
		var day = Mathf.Clamp((0.35f - dark) / 0.35f, 0, 1);
		var night = Mathf.Clamp((dark - 0.35f) / 0.65f, 0, 1);
		var dusk = Mathf.Max(0, 1 - day - night);
		_lut!.SetWeights(day, dusk, night);
	}

	private void ApplyWorld(WorldService world)
	{
		var map = world.Map;
		if (map is not null && _tilemap is null)
		{
			_tilemap = new TileMap(map) { SmoothSlopes = _render3D };
			_camera.HeightAt = _tilemap.HeightAt;
			if (_ownSim is not null) _ownSim.HeightAt = _tilemap.LogicalHeightAt;
			if (_render3D)
				_world3D!.SetMap(_tilemap);
			else
				_mapView!.SetMap(_tilemap);
			_worldRenderer!.SetTilemap(_tilemap);
			_worldRenderer.SetViewRotation(_viewRotation);
			_minimap!.SetMap(_tilemap);
			_lighting!.SetNormalMap(BakeNormalTexture(_tilemap));
			_lighting!.SetMapSize(new Vector2(_tilemap.Width, _tilemap.Height));
			if (SmokeMode)
			{
				var chunks = _render3D
					? _world3D!.Terrain.GetChildCount()
					: _mapView!.GetChildCount();
				GD.Print(
					$"SMOKE map={_tilemap.Width}x{_tilemap.Height} " +
					$"chunks={chunks} entities={world.Count}");
				GetTree().Quit();
			}
		}

		// 放置成功 → 自动退出建造预览
		if (_buildPreview is { } bp &&
			world.Entities.TryGetValue(bp.EntityId, out var placedView) &&
			placedView.Get("Building", Building.Parser) is { Placed: true })
		{
			_hud?.Log($"建筑已放置（#{bp.EntityId}）");
			ExitBuildPreview();
		}

		var now = NowMs();
		var tick = world.WorldTick;
		RebuildBlockers(world.Entities);
		_debugShapes?.Sync(world.Entities);
		// 本份快照里带 Thrown 的实体；与上一份对比即可发现"刚落地"的那些。
		_thrownNow.Clear();
		foreach (var (id, view) in world.Entities)
		{
			// 投掷飞行：服务端每 tick 标脏下发 Thrown，elapsed 是新鲜权威值。
			// ownThrow 用投掷者字段判断，命中时跟踪器会把本地 ghost 交接给权威（见 ThrowFlightTracker）。
			if (view.Get("Thrown", Thrown.Parser) is { } thrown)
			{
				_thrownNow.Add(id);
				_throwFlights.Observe(
					id,
					new System.Numerics.Vector2(thrown.FromX, thrown.FromY),
					new System.Numerics.Vector2(thrown.ToX, thrown.ToY),
					thrown.FlightTicks,
					thrown.Elapsed,
					thrown.Gravity,
					ownThrow: thrown.Thrower == _ownId);
			}
			else if (_thrownSeen.Contains(id))
			{
				// Thrown 被移除（落地/爆炸销毁）：停止跟踪。爆炸表现由 BlastFxLayer3D 负责，这里不碰。
				_throwFlights.Forget(id);
			}

			var pos = view.Get("Position", Starve.Game.V1.Position.Parser);
			if (pos is null) continue;
			// M7 连续速度：真实位置 = Position(整格) + sub（sub∈[0,1) 分数偏移，Moveable 携带）
			var mv = view.Get("Moveable", Moveable.Parser);
			var fx = pos.X + (float)(mv?.SubX ?? 0);
			var fy = pos.Y + (float)(mv?.SubY ?? 0);
			if (mv is not null)
				_world3D?.Entities.SetMoveSpeed(id, (float)mv.EffectiveSpeed);
			if (id == _ownId)
			{
				// 自己的位置走本地预测 + 服务端校正，不进插值缓冲。
				//
				// ⚠️ 这一段必须**原子读**：位置 / 组件 tick / ack 是"同一条快照消息"的三个字段，
				//    而推送在网络线程上直接改世界（Session.OnPush → World.HandleMessage），
				//    主线程分几次读就会配出「位置来自消息 m、ack 来自消息 m+1」。和解拿它做
				//    序号锚定比较时整体偏一个 tick ⇒ 每份快照都误判成需要校正（恒定 0.5 格，
				//    转向处翻倍），表现就是"走着走着时不时卡一下"。
				var own = world.ReadAtomic(() => (
					Pos: view.Get("Position", Starve.Game.V1.Position.Parser),
					Mv: view.Get("Moveable", Moveable.Parser),
					Col: view.Get("Collide", Collide.Parser),
					PosTick: view.ComponentTick("Position"),
					MvTick: view.ComponentTick("Moveable"),
					Ack: _client?.Commands.LastAcceptedSeq ?? 0,
					Epoch: _client?.Commands.InputEpoch ?? 0));
				var mv2 = own.Mv;
				var ownFx = own.Pos.X + (float)(mv2?.SubX ?? 0);
				var ownFy = own.Pos.Y + (float)(mv2?.SubY ?? 0);
				if (mv2 is not null)
				{
					_lastEffectiveSpeed = (float)mv2.EffectiveSpeed;
					ApplyDebugMoveSpeed();
					// 服务端权威身体半径：现在来自独立的 Collide 组件（由客户端模型推导）。
					// 本地预测必须用同一个值，否则贴着树/墙会来回校正。
					if (own.Col is { } ownCol)
					{
						_ownSim?.SetBodyRadius((float)ownCol.Radius);
						// ORCA 的输入维度：有效速度上限 + 胶囊半长（与服务端一致）
						_ownSim?.SetSpeedProfile((float)mv2.EffectiveSpeed, (float)ownCol.HalfLength);
					}
				}
				// 自己的投掷力量（决定可达距离；预览与本地校验都要用）
			if (view.Get("Thrower", Starve.Game.V1.Thrower.Parser) is { } thr)
				_ownThrowStrength = thr.Strength;
			_ownPathMoving = mv2 is { Path.Count: > 0 };
				if (!_ownIntentMoving && !GameplayLocked())
				{
					var pathDir = _ownPathMoving ? mv2!.Path[0] : null;
					var pdx = pathDir?.Dx ?? 0;
					var pdy = pathDir?.Dy ?? 0;
					_ownSim?.SetIntent(pdx, pdy);
					if (_ownPathMoving)
						_worldRenderer?.SetOwnMoveDir(pdx, pdy);
				}
				// 服务端确认停止 = Dir 清空 + 无路径；连续移动保留最终 sub，不吸附整数格。
				var serverStopped = mv2 is { DirX: 0, DirY: 0 } &&
									mv2.Path.Count == 0;
				// 位置/移动组件最后下发的世界 tick：停下后服务端不再标脏这两个组件，
				// 快照仍是旧的。把它交给校正逻辑，才能避免拿冻结值反复回拉（停下抖动）。
				var freshTick = Math.Max(own.PosTick, own.MvTick);
				if (mv2 is not null)
					_ownSim?.FeedServerMotion(
						(float)mv2.VelX, (float)mv2.VelY, (float)mv2.EffectiveSpeed,
						mv2.DirX, mv2.DirY, mv2.Path.Count);
				_ownSim?.Reconcile(ownFx, ownFy, serverStopped, freshTick, own.Ack, own.Epoch, now);
				if (MoveTrace.Enabled && _ownSim is { } reconSim)
					MoveTrace.OwnReconcile(reconSim.LastReconcile);
			}
			else if (!_smoothers.TryGetValue(id, out var smoother))
			{
				smoother = new PositionSmoother();
				_smoothers[id] = smoother;
				FeedSmoother(smoother, mv, fx, fy, tick, now);
			}
			else
			{
				FeedSmoother(smoother, mv, fx, fy, tick, now);
			}
			if (id != _ownId)
			{
				_locomotionMoving[id] = mv is not null &&
					(mv.DirX != 0 || mv.DirY != 0 || mv.Path.Count > 0);
			}
		}

		// 交换缓冲：本份成为"上一份"，旧的那份留到下一份快照开头清空复用。
		(_thrownSeen, _thrownNow) = (_thrownNow, _thrownSeen);

		NoticeLootPicked(world);
		_worldRenderer!.SyncEntities(world.Entities);
		UpdateBagAndCraft(world);
	}

	// 位置插值健康度（每秒由 PerfMonitor 汇总打印后清零）：
	// 外推帧 = 该帧没有可用样本、只能按速度推测的帧数。
	// 服务端每 tick 下发子格偏移后，这个比例应接近 0；偏高即说明下发有缺口。
	public static long SmootherExtrapolating;
	public static long SmootherSamples;

	public static void ResetSmootherStats()
	{
		SmootherExtrapolating = 0;
		SmootherSamples = 0;
	}

	/// <summary>
	/// 喂一份服务端位置给插值器，并同步权威速度。
	/// 权威速度（Moveable.VelX/VelY，格/秒）用于样本用尽时的外推：
	/// 它已含坡度因子与避让结果，比"末段位移"更准。停止时服务端会把它清零，
	/// 外推因此自动停住，不会出现"停下后还在滑"。
	/// </summary>
	private static void FeedSmoother(
		PositionSmoother smoother, Moveable? mv, float fx, float fy, long tick, long now)
	{
		if (mv is not null)
			smoother.SetServerVelocity((float)mv.VelX, (float)mv.VelY);
		smoother.Update(fx, fy, tick, now);
	}

	/// <summary>
	/// 从快照重建占位物形状：Block.radius &gt; 0 是格心圆（树/岩，走不满一格），
	/// 否则是 width×height 的占格盒（建筑/工作站）。占位不等于不可走——
	/// 这些形状只进本地移动预测（扫掠 + 沿接触切面滑动），可行走网格只看地形。
	/// </summary>
	private void RebuildBlockers(IReadOnlyDictionary<ulong, EntityView> entities)
	{
		var signature = 17;
		unchecked
		{
			foreach (var view in entities.Values.OrderBy(v => v.EntityId))
			{
				// 形状现在来自独立的 Collide 组件（Block 只管占位，不再有 Radius）。
				var c = view.Get("Collide", Collide.Parser);
				var p = view.Get("Position", Position.Parser);
				if (c is null || p is null) continue;
				if (view.Get("Moveable", Moveable.Parser) is not null) continue;   // 只算静态形状（见下）
				signature = signature * 31 + view.EntityId.GetHashCode();
				signature = signature * 31 + p.X;
				signature = signature * 31 + p.Y;
				signature = signature * 31 + (int)c.Shape;
				signature = signature * 31 + c.Width;
				signature = signature * 31 + c.Height;
				signature = signature * 31 + c.Radius.GetHashCode();
				signature = signature * 31 + c.HalfLength.GetHashCode();
			}
		}
		if (signature == _blockerSignature) return;
		_blockerSignature = signature;

		_blockers.Clear();
		foreach (var view in entities.Values)
		{
			var c = view.Get("Collide", Collide.Parser);
			var p = view.Get("Position", Position.Parser);
			if (c is null || p is null) continue;
			// ⚠️ **只喂静态形状**：会自己动的实体（有 Moveable）在服务端走**动态层 + ORCA 避让**，
			//    不在它自己的静态扫掠里。而本地的 MovementSlide 把胶囊当**硬障碍**处理、且不看 Owner：
			//    只要把玩家**自己的胶囊**喂进去，本地每一步都在"撞自己" ⇒ 步长被截断
			//    （端上实测 0.073 格 vs 服务端 0.5 格）⇒ 每份快照都要校正。
			//    端到端 A/B（同一地形、自动行走 16s）：
			//      · 障碍+邻居全喂        校正占快照 29%，err 中位 0.257
			//      · 只喂胶囊（移动体）   校正 47%，err 中位 0.399   ← 就是它
			//      · 只喂圆（树/岩）      校正  6%，err 中位 0.050
			//      · 不喂任何障碍         校正  6%
			//    判据与服务端一致：CanSelfMove = 有 Moveable ⇒ 这些实体不进静态层。
			if (view.Get("Moveable", Moveable.Parser) is not null) continue;
			switch (c.Shape)
			{
				case CollideShape.Circle:
					// 格心圆：Position 是格子坐标，圆心在格心
					_blockers.Add(BlockerShape.Circle(p.X + 0.5f, p.Y + 0.5f, (float)c.Radius));
					break;
				case CollideShape.Box:
					_blockers.Add(BlockerShape.Box(p.X, p.Y, Math.Max(1, c.Width), Math.Max(1, c.Height)));
					break;
				case CollideShape.Capsule:
					// 静态胶囊（不自己动，但推得动，比如船）：仍然是静态硬碰撞，保留。
					_blockers.Add(BlockerShape.Capsule(p.X, p.Y, (float)c.Radius, (float)c.HalfLength,
						c.FaceX, c.FaceZ, view.EntityId));
					break;
			}
		}
		// 诊断对照开关：分别关掉"占位物喂料 / ORCA 邻居喂料"，看残差是不是它们造成的。
		// STARVE_OWN_NO_BLOCKERS=1 / STARVE_OWN_NO_NEIGHBORS=1
		var noBlockers = System.Environment.GetEnvironmentVariable("STARVE_OWN_NO_BLOCKERS") == "1";
		var noNeighbors = System.Environment.GetEnvironmentVariable("STARVE_OWN_NO_NEIGHBORS") == "1";
		// 细分：只喂圆（树/岩）、只喂盒（建筑/工作站）、只喂胶囊（移动体）
		var only = System.Environment.GetEnvironmentVariable("STARVE_OWN_ONLY_SHAPE");
		var fed = _blockers;
		if (!string.IsNullOrEmpty(only))
		{
			fed = new System.Collections.Generic.List<Starve.Core.BlockerShape>();
			foreach (var b in _blockers)
			{
				var k = b.Kind.ToString().ToLowerInvariant();
				if (k.StartsWith(only)) fed.Add(b);
			}
		}
		_ownSim?.SetBlockers(noBlockers ? System.Array.Empty<Starve.Core.BlockerShape>() : fed);
		if (noNeighbors)
			_ownSim?.SetNeighbors(System.Array.Empty<Starve.Core.OrcaNeighbor>());
		else
			SyncOrcaNeighbors(entities);
	}

	/// <summary>
	/// 刷新 ORCA 邻居（除自己以外的动态体：其他玩家/动物）。
	///
	/// 与服务端一致：静态障碍走硬碰撞（阶段②），动态体之间走 ORCA 软避让（阶段③）。
	/// 邻居的速度取快照里的实际速度 vel_x/vel_y；半径按"外接圆"处理（半径 + 半长），
	/// 因为服务端 collectNeighbors 也是这么算的——两边必须一样，否则互惠避让不对称。
	/// </summary>
	private void SyncOrcaNeighbors(IReadOnlyDictionary<ulong, EntityView> entities)
	{
		if (_ownSim is null) return;
		_ownSim.SetSelfKey(_ownId);
		_orcaNeighbors.Clear();
		foreach (var (id, view) in entities)
		{
			if (id == _ownId) continue; // 自己不进邻居表
			var col = view.Get("Collide", Collide.Parser);
			var pos = view.Get("Position", Position.Parser);
			if (col is null || pos is null) continue;
			// 只有会自己动的实体参与 ORCA（静态体已在 _blockers 里做硬碰撞）。
			//
			// ⚠️ 判据必须是"有 Moveable"（服务端 CanSelfMove 的唯一判据，见 pushable.go）。
			//    这里曾经写的是 view.Has("Dynamic")，而服务端早已把 Static/Dynamic 两个 tag
			//    删掉、换成 Pushable/Moveable 的正交组合 ⇒ 该判据**恒为假**，邻居表永远是空的：
			//    本地预测完全不做动态避让，贴着别人/动物走就会被反复校正（"靠近别人时发卡"）。
			var mv = view.Get("Moveable", Moveable.Parser);
			if (mv is null) continue;
			var wx = pos.X + (float)(mv.SubX);
			var wy = pos.Y + (float)(mv.SubY);
			_orcaNeighbors.Add(new OrcaNeighbor
			{
				X = wx, Y = wy,
				VX = (float)mv.VelX,
				VY = (float)mv.VelY,
				Radius = (float)col.Radius,
				HalfLength = (float)col.HalfLength,
				MaxSpeed = (float)(mv.EffectiveSpeed > 0 ? mv.EffectiveSpeed : mv.Speed),
			});
		}
		// 确定性：按位置排序，与服务端的邻居排序规则一致（LP 对顺序敏感）。
		_orcaNeighbors.Sort((a, b) =>
		{
			var c = a.X.CompareTo(b.X);
			if (c != 0) return c;
			c = a.Y.CompareTo(b.Y);
			if (c != 0) return c;
			return a.Radius.CompareTo(b.Radius);
		});
		_ownSim.SetNeighbors(_orcaNeighbors);
	}

	/// <summary>与服务端 Walkable 一致：只看地形（水/悬崖）；占位物不是墙，走形状碰撞。</summary>
	private bool IsWalkable(int x, int y)
	{
		if (_tilemap is null) return true;
		// 地图边界：越界不可走（否则本地预测会走出地图到负坐标，角色跑到角外“消失”）
		if (x < 0 || y < 0 || x >= _tilemap.Width || y >= _tilemap.Height) return false;
		return _tilemap.CornerType(x, y) != (int)TerrainType.Water;
	}

	/// <summary>取目标受激能力组件（Choppable/Minable/Pickable 共用 WorkTarget 载荷）。</summary>
	private static WorkTarget? WorkTargetOf(EntityView view) =>
		view.Get("Choppable", WorkTarget.Parser)
		?? view.Get("Minable", WorkTarget.Parser)
		?? view.Get("Pickable", WorkTarget.Parser);

	/// <summary>玩家是否持有指定主动能力（服务端把工具能力复制到玩家身上）。</summary>
	private bool HasOwnCapability(string component) =>
		OwnComponent<Capability>(component, Capability.Parser) is not null;

	/// <summary>当前手持工具的物品 kind（0 = 徒手）。</summary>
	private int EquippedKind()
	{
		if (HasOwnCapability("Chopper")) return (int)ItemKind.Axe;
		if (HasOwnCapability("Miner")) return (int)ItemKind.Pickaxe;
		return 0;
	}

	/// <summary>
	/// 手持物的显示名。
	///
	/// 优先按**装备实体**反查（武器与工具都在手持槽，名字来自服务端模板表），
	/// 只有在拿不到实体时才退回能力推断 —— 否则装了长矛这类没有专属能力组件
	/// 的手持物会显示成"徒手"，玩家以为没装上。
	/// </summary>
	private string EquippedName()
	{
		if (_client is not null &&
			_client.World.Entities.TryGetValue(_ownId, out var own) &&
			own.Get("Equip", Equip.Parser) is { } eq &&
			ItemFromEquipEntity(eq.Hand) is { } hand)
		{
			return hand.Name;
		}
		return EquippedKind() switch
		{
			(int)ItemKind.Axe => "斧头",
			(int)ItemKind.Pickaxe => "镐",
			_ => "徒手",
		};
	}

	/// <summary>
	/// 已穿戴护甲：从 Equip.head/body 反查护甲实体（服务端 Defense 只挂护甲实体，
	/// 穿戴者身上不存防御），返回 (槽位名, 物品 kind, 减免百分比)。
	/// </summary>
	private List<(string Slot, int Kind, int Percent)> WornArmor()
	{
		var result = new List<(string, int, int)>();
		if (_client is null ||
			!_client.World.Entities.TryGetValue(_ownId, out var own) ||
			own.Get("Equip", Equip.Parser) is not { } eq)
		{
			return result;
		}
		var world = _client.World;
		AddArmor(eq.Head, "头戴");
		AddArmor(eq.Body, "身穿");
		return result;

		void AddArmor(ulong id, string slot)
		{
			if (id == 0 || !world.Entities.TryGetValue(id, out var item)) return;
			var def = item.Get("Defense", Defense.Parser);
			if (def is null) return;
			var kind = item.Get("Equipment", ItemStack.Parser) is { } eq ? (int)eq.Kind : 0;
			result.Add((slot, kind, def.Percent));
		}
	}

	/// <summary>总防御减免 = 头/身护甲之和（与服务端 Attackable 受击口径一致）。</summary>
	private int DefensePercent() => WornArmor().Sum(a => a.Percent);

	/// <summary>已装备物品的展示文本（手持 + 头戴/身穿护甲名）。</summary>
	private string EquipText()
	{
		var wear = string.Concat(WornArmor().Select(a =>
			$" {a.Slot} {ItemName(_client?.World.Config, a.Kind)}"));
		return $"手持 {EquippedName()}{wear}";
	}

	private static int EquipSlotNumber(string slotId) => slotId switch
	{
		"head" => 1,
		"hand" => 2,
		"body" => 3,
		_ => 0,
	};

	private ItemView? WornItem(string slotId) =>
		WornSlots().FirstOrDefault(s => s.Id == slotId)?.Item;

	/// <summary>头/手/身三格：优先 Equip 实体，手持工具无实体时用能力组件兜底。</summary>
	private List<EquipSlotView> WornSlots()
	{
		Equip? eq = null;
		if (_client is not null &&
			_client.World.Entities.TryGetValue(_ownId, out var own))
		{
			eq = own.Get("Equip", Equip.Parser);
		}
		return
		[
			new EquipSlotView("head", "头", ItemFromEquipEntity(eq?.Head ?? 0)),
			new EquipSlotView("hand", "手", ItemFromEquipEntity(eq?.Hand ?? 0) ?? ItemFromKind(EquippedKind())),
			new EquipSlotView("body", "身", ItemFromEquipEntity(eq?.Body ?? 0)),
		];
	}

	private ItemView? ItemFromEquipEntity(ulong entityId)
	{
		if (entityId == 0 || _client is null ||
			!_client.World.Entities.TryGetValue(entityId, out var item))
		{
			return null;
		}
		var stack = item.Get("Equipment", ItemStack.Parser);
		var kind = stack is { Kind: > 0 } ? (int)stack.Kind : 0;
		var cap = item.Get("Chopper", Capability.Parser) ?? item.Get("Miner", Capability.Parser);
		var durability = cap is { Durability: > 0 } ? cap.Durability
			: stack is { Durability: > 0 } ? stack.Durability
			: 0;
		return ItemViewOf(kind, 1, durability);
	}

	private ItemView? ItemFromKind(int kind)
	{
		if (kind <= 0) return null;
		var cfg = _client?.World.Config;
		return new ItemView(kind, ItemName(cfg, kind), 1, ItemColor(cfg, kind), ItemIcon(kind));
	}

	private ItemView? ItemViewOf(int kind, int count, int durability)
	{
		if (kind <= 0 || count <= 0) return null;
		var cfg = _client?.World.Config;
		var max = (int)(cfg?.Templates.FirstOrDefault(x => (int)x.Kind == kind)?.Tool?.Durability ?? 0);
		return new ItemView(
			kind,
			ItemName(cfg, kind),
			count,
			ItemColor(cfg, kind),
			ItemIcon(kind),
			durability,
			max);
	}

	private void NoticeLootPicked(WorldService world)
	{
		var nowLoot = new Dictionary<ulong, (float X, float Y)>();
		foreach (var (id, view) in world.Entities)
		{
			if (view.LootOf() is null) continue;
			if (view.Get("Position", Position.Parser) is not { } pos) continue;
			nowLoot[id] = (pos.X, pos.Y);
		}
		(float X, float Y)? own = null;
		if (world.Entities.TryGetValue(_ownId, out var me) &&
			me.Get("Position", Position.Parser) is { } mePos)
		{
			own = (mePos.X, mePos.Y);
		}
		if (own is { } at)
		{
			foreach (var (id, pos) in _lootAt)
			{
				if (nowLoot.ContainsKey(id)) continue;
				if (Math.Abs(pos.X - at.X) + Math.Abs(pos.Y - at.Y) > 3) continue;
				_sfx?.Play("sfx.gather.pickup");
				break;
			}
		}
		_lootAt.Clear();
		foreach (var (id, pos) in nowLoot) _lootAt[id] = pos;
	}

	/// <summary>一次交互：按新组件校验 + 距离检查，再发命令。</summary>
	private void TryAct(ulong id, Intent intent)
	{
		if (_ownDead || GameplayLocked()) return;
		if (_client is null || !_client.World.Entities.TryGetValue(id, out var view))
		{
			Deny("目标已消失");
			return;
		}
		if (!_client.World.Entities.TryGetValue(_ownId, out var own) ||
			own.Get("Position", Position.Parser) is not { } mePos ||
			view.Get("Position", Position.Parser) is not { } tPos)
		{
			Deny("目标不可达");
			return;
		}
		var dx = mePos.X - tPos.X;
		var dy = mePos.Y - tPos.Y;
		// 裸手采集能力范围为 1；其他交互当前范围为 2。
		var actionRange = intent == Intent.Gather ? 1 : 2;
		if (Math.Abs(dx) + Math.Abs(dy) > actionRange)
		{
			Deny("距离不够，请靠近后再操作");
			return;
		}

		ActionKind? predictedKind = null;
		InputCommandRef? commandRef = null;
		switch (intent)
		{
			case Intent.Gather:
				if (view.Get("Pickable", WorkTarget.Parser) is not { WorkLeft: > 0 })
				{
					Deny("目标不可采集");
					return;
				}
				commandRef = _client.Commands.Gather(id);
				predictedKind = ActionKind.Pick;
				break;
			case Intent.Chop:
				if (view.Get("Choppable", WorkTarget.Parser) is null)
				{
					Deny("目标不可砍伐（不是树木）");
					return;
				}
				if (!HasOwnCapability("Chopper"))
				{
					Deny("徒手无法砍伐，请先装备斧头");
					return;
				}
				commandRef = _client.Commands.Chop(id);
				predictedKind = ActionKind.Chop;
				break;
			case Intent.Mine:
				if (view.Get("Minable", WorkTarget.Parser) is null)
				{
					Deny("目标不可挖掘（不是矿脉）");
					return;
				}
				if (!HasOwnCapability("Miner"))
				{
					Deny("徒手无法挖掘，请先装备镐");
					return;
				}
				commandRef = _client.Commands.Mine(id);
				predictedKind = ActionKind.Mine;
				break;
			case Intent.Pickup:
				if (view.LootOf() is null)
				{
					Deny("目标没有掉落物");
					return;
				}
				_client.Commands.Pickup(id);
				_sfx?.Play("sfx.gather.pickup");
				_worldRenderer?.PlayLocalAction(_ownId, ActionKind.Pick);
				break;
			case Intent.Attack:
				if (view.Get("Health", Health.Parser) is null ||
					view.Get("Dead", Dead.Parser) is not null)
				{
					Deny("目标不可攻击");
					return;
				}
				commandRef = _client.Commands.Attack(id);
				predictedKind = ActionKind.Attack;
				break;
		}
		if (predictedKind is { } kind && commandRef is { } command)
		{
			_worldRenderer?.PredictAction(_ownId, kind, command);
		}
	}

	private void TryHaunt(ulong id)
	{
		if (_client is null || GameplayLocked()) return;
		if (!_client.World.Entities.TryGetValue(_ownId, out var own) ||
			!_client.World.Entities.TryGetValue(id, out var target) ||
			own.Get("Position", Position.Parser) is not { } actorPos ||
			target.Get("Position", Position.Parser) is not { } targetPos)
		{
			_hud?.Log("复活雕像已消失或不可达");
			return;
		}

		var hauntable = target.Get("Hauntable", Hauntable.Parser);
		var block = target.Get("Block", Block.Parser);
		var validation = HauntInteractionPolicy.Validate(
			_ownDead,
			hauntable is not null,
			hauntable?.RemainingUses ?? 0,
			actorPos.X,
			actorPos.Y,
			targetPos.X,
			targetPos.Y,
			block?.Width ?? 1,
			block?.Height ?? 1);
		if (validation != HauntValidation.Allowed)
		{
			_hud?.Log(validation switch
			{
				HauntValidation.ActorAlive => "存活时只能查看复活雕像",
				HauntValidation.Depleted => "这座复活雕像已耗尽",
				HauntValidation.OutOfRange => "距离复活雕像太远，请靠近到 2 格内",
				_ => "目标不是可作祟的复活雕像",
			});
			return;
		}

		var command = _client.Commands.Haunt(id);
		_worldRenderer?.PredictAction(_ownId, ActionKind.Haunt, command);
		RefreshGameplayLock();
	}

	/// <summary>选中实体的可读描述（名称/血量/工作量/可用动作）。</summary>
	private string DescribeSelected()
	{
		if (_selected is not { } id || _client is null ||
			!_client.World.Entities.TryGetValue(id, out var view))
		{
			return "无";
		}
		var cfg = _client.World.Config;
		if (view.Get("Hauntable", Hauntable.Parser) is { } hauntable)
			return $"复活雕像 #{id} 剩余次数 {hauntable.RemainingUses} " +
				   $"作祟时长 {hauntable.DurationTicks} ticks" +
				   (_ownDead ? " [点击作祟]" : " [灵魂可用]");
		if (view.Get("Player", Player.Parser) is not null)
			return $"玩家 #{id}";
		if (view.Get("Dead", Dead.Parser) is not null)
			return $"尸体 #{id}";
		var loot = view.LootOf();
		if (loot is not null)
		{
			var names = loot.Items.Select(i => $"{ItemName(cfg, (int)i.Kind)}×{i.Count}");
			return $"掉落物 #{id}：{string.Join("、", names)} [拾取]";
		}
		var wt = WorkTargetOf(view);
		if (wt is not null)
		{
			var action = view.Get("Choppable", WorkTarget.Parser) is not null ? "砍伐"
				: view.Get("Minable", WorkTarget.Parser) is not null ? "挖掘"
				: "采集";
			return $"{ItemName(cfg, (int)wt.Kind)} #{id} 工作量 {wt.WorkLeft}/{wt.MaxWork} [{action}]";
		}
		if (view.Get("Scenery", Scenery.Parser) is { } scenery)
			return $"{ItemName(cfg, (int)scenery.Kind)} #{id}";
		var ws = view.Get("Workstation", Workstation.Parser);
		if (ws is not null)
			return $"工作站#{ws.Type} #{id}";
		var bld = view.Get("Building", Building.Parser);
		if (bld is not null)
			return $"{((int)bld.Kind == 1 ? "火堆" : "木墙")} #{id}" + (bld.Placed ? "" : " [未放置]");
		var cr = view.Get("Creature", Creature.Parser);
		if (cr is not null)
		{
			var hp = view.Get("Health", Health.Parser);
			var hpTxt = hp is null ? "" : $" hp={hp.Cur}/{hp.Max}";
			var name = cr.Kind switch
			{
				CreatureKind.Rabbit => "兔子",
				CreatureKind.Wolf => "狼",
				CreatureKind.Boar => "野猪",
				CreatureKind.Deer => "鹿",
				CreatureKind.Spider => "蜘蛛",
				CreatureKind.Fishman => "鱼人",
				CreatureKind.Lizard => "蜥蜴",
				_ => "生物",
			};
			return $"{name} #{id}{hpTxt} [攻击]";
		}
		return $"实体 #{id}";
	}

	private void UpdateBagAndCraft(WorldService world)
	{
		if (_hud is null || !world.Entities.TryGetValue(_ownId, out var own)) return;
		var inv = own.Get("Inventory", Inventory.Parser);
		var crafting = own.Get("Crafting", Crafting.Parser);
		var cfg = world.Config;
		var signature = ComputeHudSignature(world, own);
		if (signature == _hudSignature) return;
		_hudSignature = signature;

		var items = (inv?.Items ?? new()).Select(it =>
			ItemViewOf((int)it.Kind, it.Count, it.Durability)
			?? new ItemView(0, "", 0, Colors.Transparent)).ToList();
		// 已穿戴的在头/手/身格里看；背包同 kind 不再标「装」（装备已从背包扣走）。
		var worn = WornSlots();
		_hud.RenderInventory(items, new HashSet<int>(), cfg?.InventorySlots ?? 12, worn);

		if (cfg is null) return;
		var ownPos = own.Get("Position", Position.Parser);
		var near = StationNear(world, ownPos);
		var materials = (inv?.Items ?? new())
			.Where(i => (int)i.Kind > 0)
			.GroupBy(i => (int)i.Kind)
			.ToDictionary(g => g.Key, g => g.Sum(i => i.Count)); // 同种多堆合并，否则重复键抛异常
		var recipes = cfg.Recipes.Select(r =>
		{
			var stationOk = (int)r.Workstation == 0 || near.Contains((int)r.Workstation);
			var can = stationOk && r.Ingredients.All(i => materials.GetValueOrDefault((int)i.Kind) >= i.Count);
			return new RecipeView(
				r.Id,
				ItemName(cfg, (int)r.Output.Kind),
				r.Ticks,
				(int)r.Workstation == 0
					? "徒手可做"
					: stationOk
						? $"{WorkstationName((int)r.Workstation)}附近 ✓"
						: $"需要靠近{WorkstationName((int)r.Workstation)}",
				can,
				r.Ingredients.Select(i =>
					new IngredientView(
						ItemName(cfg, (int)i.Kind),
						materials.GetValueOrDefault((int)i.Kind),
						i.Count,
						ItemIcon((int)i.Kind),
						ItemColor(cfg, (int)i.Kind))).ToList(),
				ItemIcon((int)r.Output.Kind));
		}).ToList();
		var total = crafting is null
			? 0
			: (long)(cfg.Recipes.FirstOrDefault(r => r.Id == crafting.RecipeId)?.Ticks ?? 0);
		_hud.RenderCraft(
			recipes,
			crafting is null ? null : new CraftingView(crafting.RecipeId, (long)crafting.TicksLeft, total));
	}

	private static int ComputeHudSignature(WorldService world, EntityView own)
	{
		var hash = new HashCode();
		hash.Add(world.Config?.GetHashCode() ?? 0);
		// 不把原始坐标打进签名：走动时 Position 每拍都变，会把制作/背包整棵拆掉重建。
		foreach (var name in new[] { "Inventory", "Equip", "Chopper", "Miner", "Health" })
		{
			if (own.Components.TryGetValue(name, out var data)) AddBytes(ref hash, data);
		}
		foreach (var type in StationNear(world, own.Get("Position", Position.Parser)).OrderBy(t => t))
			hash.Add(type);
		var health = own.Get("Health", Health.Parser);
		hash.Add(HudVitalsViewModel.Create(
			health?.Cur ?? 0,
			health?.Max ?? 0,
			own.Components.ContainsKey("Dead")).Signature);
		if (own.Get("Crafting", Crafting.Parser) is { } crafting)
		{
			hash.Add(crafting.RecipeId);
			var total = world.Config?.Recipes.FirstOrDefault(r => r.Id == crafting.RecipeId)?.Ticks ?? 0;
			hash.Add(total > 0 ? crafting.TicksLeft * 20 / total : crafting.TicksLeft);
		}
		foreach (var view in world.Entities.Values.OrderBy(v => v.EntityId))
		{
			if (view.Components.ContainsKey("Workstation"))
			{
				hash.Add(view.EntityId);
				if (view.Components.TryGetValue("Workstation", out var ws)) AddBytes(ref hash, ws);
				if (view.Components.TryGetValue("Position", out var pos)) AddBytes(ref hash, pos);
			}
			if (view.Components.ContainsKey("Equipment") || view.Components.ContainsKey("Defense"))
			{
				hash.Add(view.EntityId);
				if (view.Components.TryGetValue("Equipment", out var eq)) AddBytes(ref hash, eq);
				if (view.Components.TryGetValue("Defense", out var def)) AddBytes(ref hash, def);
				if (view.Components.TryGetValue("Chopper", out var chop)) AddBytes(ref hash, chop);
				if (view.Components.TryGetValue("Miner", out var mine)) AddBytes(ref hash, mine);
			}
		}
		return hash.ToHashCode();
	}

	private static void AddBytes(ref HashCode hash, byte[] data)
	{
		foreach (var b in data) hash.Add(b);
	}

	private static string WorkstationName(int type) => type switch
	{
		1 => "火堆",
		2 => "工作台",
		_ => $"工作站#{type}",
	};

	private static HashSet<int> StationNear(WorldService world, Position? ownPos)
	{
		var set = new HashSet<int>();
		if (ownPos is null) return set;
		foreach (var view in world.Entities.Values)
		{
			var ws = view.Get("Workstation", Workstation.Parser);
			var p = view.Get("Position", Position.Parser);
			if (ws is null || p is null) continue;
			if (Math.Abs(p.X - ownPos.X) + Math.Abs(p.Y - ownPos.Y) <= 3) set.Add((int)ws.Type);
		}
		return set;
	}

	private static string ItemName(GameConfig? cfg, int kind)
	{
		var t = cfg?.Templates.FirstOrDefault(x => (int)x.Kind == kind);
		return t?.Name ?? kind.ToString();
	}

	/// <summary>世界实体标签：资源带动作/掉落/工具提示，掉落物带数量，生物/建筑带中文名。</summary>
	private string? EntityName(EntityView view)
	{
		var pl = view.Get("Player", Player.Parser);
		if (pl is not null)
			return pl.Uid == _ownUid ? "我" : $"玩家 {pl.Uid}";
		if (view.Get("Hauntable", Hauntable.Parser) is { } hauntable)
			return $"复活雕像·剩余 {hauntable.RemainingUses}";
		if (view.LootOf() is { } lt)
			return string.Join("、", lt.Items.Select(i => $"{ItemName(_client?.World.Config, (int)i.Kind)}×{i.Count}"));
		if (view.Get("Choppable", WorkTarget.Parser) is not null)
			return HasOwnCapability("Chopper") ? "树·砍伐→木头" : "树·需斧头";
		if (view.Get("Minable", WorkTarget.Parser) is not null)
			return HasOwnCapability("Miner") ? "矿石·挖掘→燧石" : "矿石·需镐";
		if (view.Get("Pickable", WorkTarget.Parser) is { } pickable)
		{
			var cfg = _client?.World.Config;
			var template = cfg?.Templates.FirstOrDefault(x => x.Kind == pickable.Kind);
			var yieldKind = template is not null && (int)template.PickYield != 0
				? (int)template.PickYield
				: (int)pickable.Kind;
			return $"{ItemName(cfg, (int)pickable.Kind)}·采集→{ItemName(cfg, yieldKind)}";
		}
		if (view.Get("Scenery", Scenery.Parser) is { } scenery)
			return ItemName(_client?.World.Config, (int)scenery.Kind);
		if (view.Get("Creature", Creature.Parser) is { } cr)
		{
			var name = cr.Kind switch
			{
				CreatureKind.Rabbit => "兔子",
				CreatureKind.Wolf => "狼",
				CreatureKind.Boar => "野猪",
				CreatureKind.Deer => "鹿",
				CreatureKind.Spider => "蜘蛛",
				CreatureKind.Fishman => "鱼人",
				CreatureKind.Lizard => "蜥蜴",
				_ => "生物",
			};
			return view.Get("Dead", Dead.Parser) is not null ? name + "尸体" : name;
		}
		if (view.Get("Workstation", Workstation.Parser) is { } ws)
			return (int)ws.Type == 1 ? "火堆工作站" : "工作台";
		if (view.Get("Building", Building.Parser) is { } bld)
			return (int)bld.Kind == 1 ? "火堆" : "木墙";
		return null;
	}

	private static Color ItemColor(GameConfig? cfg, int kind)
	{
		var t = cfg?.Templates.FirstOrDefault(x => (int)x.Kind == kind);
		if (t is not null && t.Color.StartsWith("#") && int.TryParse(t.Color.AsSpan(1), NumberStyles.HexNumber, null, out var v))
		{
			return new Color(((v >> 16) & 0xff) / 255f, ((v >> 8) & 0xff) / 255f, (v & 0xff) / 255f);
		}
		return Colors.White;
	}

	private void WithBagSlot(int slot, Action<int> act)
	{
		var inv = OwnComponent("Inventory", Inventory.Parser);
		if (inv is null || slot < 0 || slot >= inv.Items.Count) return;
		var kind = (int)inv.Items[slot].Kind;
		if (kind > 0) act(kind);
	}

	private int OwnItemCount(int slot)
	{
		var inv = OwnComponent("Inventory", Inventory.Parser);
		return inv is not null && slot >= 0 && slot < inv.Items.Count ? inv.Items[slot].Count : 0;
	}

	private T? OwnComponent<T>(string name, MessageParser<T> parser) where T : class, IMessage<T> =>
		_client is not null && _client.World.Entities.TryGetValue(_ownId, out var view)
			? view.Get(name, parser)
			: null;

	private async Task DoCraftAsync(string recipeId)
	{
		if (_client is null || _ownDead || GameplayLocked()) return;
		var submission = _client.Commands.BeginCraft(recipeId);
		_worldRenderer?.PredictAction(_ownId, ActionKind.Craft, submission.CommandRef);
		var resp = await submission.ResponseTask;
		if (resp is { Started: true })
		{
			_sfx?.Play("sfx.ui.craft.start");
		}
		else
		{
			_worldRenderer?.CancelPredictedAction(_ownId, submission.CommandRef.RequestId);
			_sfx?.Play("sfx.ui.craft.fail");
		}
		_hud?.Log(resp is { Started: true }
			? $"开始制作 {recipeId}（{resp.Ticks} ticks）"
			: $"制作失败: {CraftFailureText(resp?.Message)}");
	}

	private void Deny(string message)
	{
		_hud?.Log(message);
		_sfx?.Play("sfx.ui.deny");
	}

	private static string CraftFailureText(string? code) => code switch
	{
		null or "" => "请求超时，请检查连接",
		"insufficient materials" => "材料不足，请查看配方中的持有数量",
		"need workstation nearby" => "需要靠近配方指定的工作站（曼哈顿距离不超过 3 格）",
		"output stack full" => "背包没有足够空间",
		"already crafting" => "已有物品正在制作",
		"player dead" => "死亡状态无法制作",
		"player not found" => "玩家状态尚未就绪",
		"unknown recipe" => "配方不存在或客户端配置已过期",
		"world_unavailable" => "世界服务暂不可用",
		_ => code,
	};

	private static string ActionOutcomeReasonText(ActionOutcomeReason reason) => reason switch
	{
		ActionOutcomeReason.Moved => "开始移动",
		ActionOutcomeReason.Damaged => "受到攻击",
		ActionOutcomeReason.Dead => "角色死亡",
		ActionOutcomeReason.Explicit => "主动取消",
		ActionOutcomeReason.Busy => "正在执行其他动作",
		ActionOutcomeReason.InvalidTarget => "目标无效",
		ActionOutcomeReason.Unsupported => "动作不受支持",
		ActionOutcomeReason.InvalidActor => "当前角色无效",
		_ => "状态已变化",
	};

	private static string HealthChangeCauseText(HealthChangeCause cause) => cause switch
	{
		HealthChangeCause.Attack => "攻击",
		HealthChangeCause.Poison => "中毒",
		HealthChangeCause.Starvation => "饥饿",
		HealthChangeCause.Weather => "天气",
		HealthChangeCause.Healing => "治疗",
		_ => "状态变化",
	};

	private static Texture2D BakeNormalTexture(TileMap tm)
	{
		var buf = new byte[tm.Width * tm.Height * 4];
		NormalMapBaker.Bake(tm, buf);
		var img = Image.CreateFromData(tm.Width, tm.Height, false, Image.Format.Rgba8, buf);
		return ImageTexture.CreateFromImage(img);
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is not InputEventKey key || key.Echo) return;
		var name = OS.GetKeycodeString(key.Keycode);
		if (key.Pressed)
		{
			if (_render3D && name == "F1" && _toonPanel is not null)
				_toonPanel.Toggle();
			if (_render3D && name == "F2" && _actorPanel is not null)
				_actorPanel.Toggle();
			if (name == "F3" && _perfPanel is not null)
				_perfPanel.Toggle();
			if (name == "T") HandleThrowKey();
			if (!_render3D)
			{
				if (name == "Q") RotateView(-Mathf.Pi / 4);
				else if (name == "E") RotateView(Mathf.Pi / 4);
			}
		}
		var intent = name switch
		{
			"Space" => AutoActionIntent.Any,
			"F" => AutoActionIntent.AttackOnly,
			_ => (AutoActionIntent?)null,
		};
		if (intent is not { } autoIntent) return;
		if (GameplayLocked())
		{
			_autoActions.Release(autoIntent);
			return;
		}
		if (key.Pressed)
		{
			_autoActions.Press(autoIntent, NowMs(), TriggerAutoAction);
		}
		else
		{
			_autoActions.Release(autoIntent);
		}
	}

	private void TriggerAutoAction(AutoActionIntent intent)
	{
		if (_ownDead || GameplayLocked()) return;
		// 服务端有 ActionState 时再发会被 BUSY 拒绝；攻击 16 tick / 采集 8 tick，150ms 连发只会砍动画。
		if (_worldRenderer?.ActionStatusOf(_ownId) is not null) return;
		if (intent == AutoActionIntent.AttackOnly) _client?.Commands.AttackNearest();
		else _client?.Commands.Automate();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseMotion mm)
		{
			_mouseWorld = ScreenToWorld(mm.Position);
		}
		else if (@event is InputEventMouseButton rb && rb.Pressed && rb.ButtonIndex == MouseButton.Right)
		{
			// 右键：取消投掷瞄准（不穿透到其他逻辑）
			if (_throwAiming)
			{
				CancelThrowAim();
				_hud?.Log("已取消投掷瞄准");
			}
		}
		else if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
		{
			if (PointerOnHud(mb.Position)) return;
			if ((_toonPanel is { Visible: true, PickMode: true } ||
				 _actorPanel is { Visible: true, PickMode: true }) &&
				_world3D is not null)
			{
				if (_world3D.TryPickVisual(mb.Position, out var pickId, out var visual))
				{
					_toonPanel?.BindSelected(pickId, visual);
					_actorPanel?.BindSelected(pickId, visual);
					_world3D.ShowToonMark(visual);
					_hud?.Log($"已选 {visual.Name}");
				}
				else
				{
					_hud?.Log("没点到模型，对准角色身体再点");
				}
				return;
			}
			if (GameplayLocked()) return;
			// 投掷瞄准模式：点地图选落点（不执行常规的选中/动作）
			if (_throwAiming)
			{
				_throwTarget = ScreenToWorld(mb.Position);
				_throwHasTarget = true;
				RefreshThrowPreview();
				return;
			}
			if (_ownDead)
			{
				var deadPicked = ScreenToWorld(mb.Position);
				_selected = FindNearest(deadPicked);
				if (_selected is { } deadSelection &&
					deadSelection != _ownId &&
					_client is not null &&
					_client.World.Entities.TryGetValue(deadSelection, out var deadTarget) &&
					deadTarget.Get("Hauntable", Hauntable.Parser) is not null)
				{
					TryHaunt(deadSelection);
				}
				return;
			}
			if (_buildPreview is { } bp && _mouseWorld is { } mw)
			{
				_client?.Commands.Place(bp.EntityId, (int)MathF.Round(mw.X), (int)MathF.Round(mw.Y));
				_hud?.Log($"已请求放置 #{bp.EntityId} 到 ({mw.X:0},{mw.Y:0})");
				ExitBuildPreview();
				return;
			}
			var picked = ScreenToWorld(mb.Position);
			_selected = FindNearest(picked);
			// 点击实体 = 选中并直接执行对应动作（掉落物→拾取、浆果→采集、树→砍伐、矿→挖掘、生物→攻击）。
			if (_selected is { } sel &&
				sel != _ownId &&
				_client is not null &&
				_client.World.Entities.TryGetValue(sel, out var selView))
			{
				if (selView.Get("Hauntable", Hauntable.Parser) is not null) return;
				if (selView.LootOf() is not null)
					TryAct(sel, Intent.Pickup);
				else if (selView.Get("Pickable", WorkTarget.Parser) is not null)
					TryAct(sel, Intent.Gather);
				else if (selView.Get("Choppable", WorkTarget.Parser) is not null)
					TryAct(sel, Intent.Chop);
				else if (selView.Get("Minable", WorkTarget.Parser) is not null)
					TryAct(sel, Intent.Mine);
				else if (selView.Get("Health", Health.Parser) is not null &&
						 selView.Get("Dead", Dead.Parser) is null)
					TryAct(sel, Intent.Attack);
			}
		}
	}

	private void UpdateGhost()
	{
		if (_buildPreview is not { } bp || _ghost is null || _mouseWorld is not { } mw) return;
		var local = IsoMath.WorldToLocal(mw.X, mw.Y);
		_ghost.SetLocal(new Vector2(local.X, local.Y));
		var now = NowMs();
		if (now - _lastBuildCheckAt < 100) return;
		_lastBuildCheckAt = now;
		var x = (int)MathF.Round(mw.X);
		var y = (int)MathF.Round(mw.Y);
		_ = CheckPlaceAsync(bp.EntityId, x, y);
	}

	private async Task CheckPlaceAsync(ulong entity, int x, int y)
	{
		if (_client is null || _buildPreview is null || GameplayLocked()) return;
		var resp = await _client.Commands.BuildCheckAsync(entity, x, y);
		if (_buildPreview is not { } bp) return;
		_buildPreview = (bp.EntityId, bp.Kind, bp.W, bp.H, resp?.Ok ?? false);
		_ghost?.SetOk(resp?.Ok ?? false);
	}

	private void ExitBuildPreview()
	{
		_buildPreview = null;
		if (_ghost is not null) _ghost.Visible = false;
	}

	private ulong? FindNearest(System.Numerics.Vector2 world)
	{
		if (_client is null) return null;
		ulong? best = null;
		var bestDist = 0.6f;
		foreach (var (id, view) in _client.World.Entities)
		{
			if (view.Get("Scenery", Scenery.Parser) is not null ||
				EntityVisual.IsDepletedFlower(view))
			{
				continue;
			}
			var pos = view.Get("Position", Starve.Game.V1.Position.Parser);
			if (pos is null) continue;
			var dx = pos.X - world.X;
			var dy = pos.Y - world.Y;
			var d = MathF.Sqrt(dx * dx + dy * dy);
			if (d < bestDist)
			{
				best = id;
				bestDist = d;
			}
		}
		return best;
	}

	private void WithSelected(Action<ulong> act)
	{
		if (_selected is null)
		{
			Deny("先点击选中目标");
			return;
		}
		act(_selected.Value);
	}

	private async Task DoBuildAsync(int kind)
	{
		if (_client is null || !CanSendGameplay()) return;
		var resp = await _client.Commands.BuildAsync(kind);
		if (resp is null || !resp.Ok)
		{
			_hud?.Log($"建造失败: {resp?.Message ?? "超时"}");
			return;
		}
		if (!CanSendGameplay()) return;
		var cfg = _client.World.Config;
		var b = cfg?.Buildings.FirstOrDefault(x => (int)x.Kind == kind);
		var w = b?.Width ?? 1;
		var h = b?.Height ?? 1;
		_buildPreview = (resp.Entity, kind, w, h, true);
		_ghost!.Configure(w, h);
		_ghost.Visible = true;
		if (_mouseWorld is not null) UpdateGhost();
		_hud?.Log($"已创建蓝图 #{resp.Entity}，移动鼠标选位置，点击放置");
	}

	private void RefreshOwnVitals(WorldService world)
	{
		if (_hud is null || !world.Entities.TryGetValue(_ownId, out var own)) return;
		var health = own.Get("Health", Health.Parser);
		var hunger = own.Get("Hunger", Hunger.Parser);
		var dead = own.Components.ContainsKey("Dead");
		_hud.SetVitals(HudVitalsViewModel.Create(
			health?.Cur ?? 0, health?.Max ?? 0, dead, hunger?.Level ?? 0));
		_hud.SetInteractionsDisabled(dead || GameplayLocked());
		if (dead && !_ownDead)
		{
			ExitBuildPreview();
			_hud.Log("灵魂状态：靠近复活雕像并点击作祟");
		}
		_ownDead = dead;
	}

	private void UpdateHud()
	{
		if (_hud is null || _client is null) return;
		var w = _client.World;
		RefreshOwnVitals(w);
		var hauntStatus = _worldRenderer?.ActionStatusOf(_ownId);
		var actionState = w.Entities.TryGetValue(_ownId, out var own)
			? own.Get("ActionState", ActionState.Parser)
			: null;
		var hauntText = HauntInteractionPolicy.IsGameplayLocked(hauntStatus)
			? HauntProgressText(w.WorldTick, hauntStatus, actionState)
			: _ownDead
				? "灵魂状态：靠近复活雕像并点击作祟"
				: "";
		var defense = DefensePercent();
		var selected = DescribeSelected();
		var status = hauntText.Length > 0 ? hauntText : selected;
		if (defense > 0) status = $"{status}  防御{defense}%";
		if (_movementDiagnosticsStatus.Length > 0) status += _movementDiagnosticsStatus;
		_hud.SetStatus(status);
		_hud.SetToolState(HasOwnCapability("Chopper"), HasOwnCapability("Miner"));
	}

	private bool PointerOnHud(Vector2 screen)
	{
		if (_hud is null) return false;
		if (_hud.HitsInteractive(screen)) return true;
		if (_toonPanel is { Visible: true } && _toonPanel.Hits(screen)) return true;
		if (_actorPanel is { Visible: true } && _actorPanel.Hits(screen)) return true;
		if (_perfPanel is { Visible: true } && _perfPanel.Hits(screen)) return true;
		var hovered = GetViewport()?.GuiGetHoveredControl();
		if (hovered is null) return false;
		if (hovered == _hud || _hud.IsAncestorOf(hovered)) return true;
		if (_toonPanel is not null && (hovered == _toonPanel || _toonPanel.IsAncestorOf(hovered))) return true;
		if (_actorPanel is not null && (hovered == _actorPanel || _actorPanel.IsAncestorOf(hovered))) return true;
		return _perfPanel is not null && (hovered == _perfPanel || _perfPanel.IsAncestorOf(hovered));
	}

	private bool GameplayLocked() =>
		HauntInteractionPolicy.IsGameplayLocked(_worldRenderer?.ActionStatusOf(_ownId));

	private bool CanSendGameplay() => !_ownDead && !GameplayLocked();

	private void RefreshGameplayLock()
	{
		var locked = GameplayLocked();
		if (_gameplayLocked == locked)
		{
			_hud?.SetInteractionsDisabled(_ownDead || locked);
			return;
		}

		_gameplayLocked = locked;
		_moveController?.SetBlocked(locked);
		if (locked)
		{
			_ownSim?.SetIntent(0, 0);
			_worldRenderer?.SetOwnMoveDir(0, 0);
			_worldRenderer?.SetOwnFacing(0f, 0f);
			_ownIntentMoving = false;
			_ownPathMoving = false;
			_autoActions.Release(AutoActionIntent.Any);
			_autoActions.Release(AutoActionIntent.AttackOnly);
			ExitBuildPreview();
		}
		try
		{
			_hud?.SetInteractionsDisabled(_ownDead || locked);
		}
		catch (Exception ex)
		{
			GD.PushError($"HUD disable: {ex.Message}");
		}
	}

	private static string HauntProgressText(
		long worldTick,
		ActionPresentationStatus? status,
		ActionState? state)
	{
		if (status is { Predicted: true }) return "作祟中：等待服务器确认（输入已锁定，不可取消）";
		if (state is null) return "作祟中：等待复活快照（输入已锁定，不可取消）";
		var start = state.PhaseStartTick;
		var end = state.EndTick > start ? state.EndTick : state.PhaseEndTick;
		if (end <= start) return "作祟中（输入已锁定，不可取消）";
		var pct = Math.Clamp((worldTick - start) * 100 / (end - start), 0, 100);
		return $"作祟中：{pct}%（输入已锁定，不可取消）";
	}

	private static string SeasonName(int season) => season switch
	{
		1 => "春",
		2 => "夏",
		3 => "秋",
		4 => "冬",
		_ => "?",
	};

	private void ApplyDebugMoveSpeed()
	{
		var speed = _lastEffectiveSpeed * (_actorPanel?.MoveSpeedMul ?? 1f);
		_ownSim?.SetSpeed(speed);
		_world3D?.Entities.SetOwnMoveSpeed(speed);
	}

	/// <summary>Q/E：2D 为 45° 步进转菱形；3D 为按住绕玩家水平环绕。</summary>
	private void RotateView(float delta)
	{
		_viewRotation += delta;
		_worldRenderer?.SetViewRotation(_viewRotation);
		_moveController?.SetViewYaw(_viewRotation);
	}

	/// <summary>屏幕坐标经场景变换逆投影为世界坐标，覆盖 2D 旋转/缩放或 3D 正交射线。</summary>
	// ── 投掷瞄准 ─────────────────────────────────────────────
	//
	// 交互：按 T 进入瞄准 → 点地图选落点（实时预览抛物线）→ 再按 T 投出。
	// 再按一次 Esc/T 之外的取消路径：右键取消（见 _UnhandledInput）。
	//
	// 为什么做成"模式"而不是点一下就扔：投掷落点需要先看清抛物线再确认，
	// 且服务端有两段动作（windup 蓄力），直接扔会频繁误触。

	private void HandleThrowKey()
	{
		if (GameplayLocked() || _ownDead) return;
		if (!_throwAiming)
		{
			// 检查能不能投掷（没有 Thrower / 没有炸弹就别进模式，避免白按）
			if (_ownThrowStrength <= 0)
			{
				_hud?.Log("不能投掷：没有投掷能力");
				return;
			}
			_throwAiming = true;
			_throwHasTarget = false;
			_hud?.Log("投掷瞄准：点击地面选落点，再按 T 投出（右键取消）");
			RefreshThrowPreview();
			return;
		}
		// 已在瞄准模式：确认投出
		if (!_throwHasTarget)
		{
			_hud?.Log("先点一下地面选落点");
			return;
		}
		ThrowNow();
	}

	private void CancelThrowAim()
	{
		if (!_throwAiming) return;
		_throwAiming = false;
		_throwHasTarget = false;
		_throwAim?.Hide();
	}

	/// <summary>刷新抛物线预览（每次改变落点或玩家移动后调用）。</summary>
	private void RefreshThrowPreview()
	{
		if (!_throwAiming || _throwAim is null) return;
		if (!_throwHasTarget)
		{
			// 还没选落点：只显示可达范围（用玩家位置当起点）
			var selfOnly = OwnPosition();
			if (selfOnly is { } sp)
			{
				_throwAim.Show(sp, sp, MaxThrowDistance(), true, HeightAt);
			}
			return;
		}
		var self = OwnPosition();
		if (self is not { } from) return;
		var to = _throwTarget;
		var dist = System.Numerics.Vector2.Distance(from, to);
		var max = MaxThrowDistance();
		// 本地预览就按"能否投掷"上色；服务端仍会权威校验（不一致时以服务端为准）。
		var ok = max > 0 && dist <= max;
		_throwAim.Show(from, to, max, ok, HeightAt);
	}

	private void ThrowNow()
	{
		var self = OwnPosition();
		if (self is not { } from) return;
		var to = _throwTarget;
		var dist = System.Numerics.Vector2.Distance(from, to);
		var max = MaxThrowDistance();
		if (max <= 0)
		{
			_hud?.Log("不能投掷：力量或物品不对");
			return;
		}
		if (dist > max)
		{
			_hud?.Log($"超出投掷距离：{dist:0.0} > {max} 格");
			return;
		}
		// thrown=0：由服务端从背包取一个炸弹实体化（客户端没有世界实体可指）
		var command = _client?.Commands.Throw(0, from.X, from.Y, to.X, to.Y);
		var traj = ThrowPhysics.Solve(from, to);
		if (command is { } cmd)
		{
			// 本地即时反馈（服务端 Commit 前 600ms 的起手期，画面上只可能有这两样）：
			//   1. 投掷动作的起手表现（与 Attack 同族）；
			//   2. 抛物线 ghost —— 起手结束后才离手，随后由权威实体交接。
			_throwRequestId = cmd.RequestId;
			_worldRenderer?.PredictAction(_ownId, ActionKind.Throw, cmd);
			_throwFlights.PredictOwn(from, to);
		}
		_hud?.Log($"投掷 → ({to.X:0},{to.Y:0}) 距离 {dist:0.0} 格 · 飞行 {traj.FlightTicks} tick");
		CancelThrowAim();
	}

	private int MaxThrowDistance() =>
		ThrowPhysics.MaxThrowDistance(_ownThrowStrength, _bombMass);

	private System.Numerics.Vector2? OwnPosition() =>
		_ownSim is { Has: true } sim
			? new System.Numerics.Vector2(sim.Position.X, sim.Position.Y)
			: null;

	private float HeightAt(float x, float y) => _tilemap?.HeightAt(x, y) ?? 0f;

	private System.Numerics.Vector2 ScreenToWorld(Vector2 screen)
	{
		Func<float, float, float>? heightAt = _tilemap is null ? null : _tilemap.HeightAt;
		if (_render3D && _world3D is not null)
			return _world3D.ScreenToWorld(screen, heightAt);
		if (_world is null) return System.Numerics.Vector2.Zero;
		var local = _world.ToLocal(screen);
		return IsoMath.LocalToWorld(local.X, local.Y, heightAt);
	}

	private long _lastAppliedTick = -1;

	private static long NowMs() => checked((long)Time.GetTicksMsec());
}
