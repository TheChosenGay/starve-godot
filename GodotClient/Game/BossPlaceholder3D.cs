using System.Collections.Generic;
using Godot;
using Starve.Game.V1;

namespace GodotClient.Game;

/// <summary>
/// Boss 美术资源缺位时的占位演员：暗紫身体 + 骨白头 + 红眼 + 头顶"首领"标记，
/// 体型明显比玩家大——保证现在就能在场景里认出一个 Boss，而不是空引用或一行字。
///
/// 为什么实现 <see cref="IAnimatedActor3D"/>（而不是直接返回一个裸 Node3D）：
/// ① <c>EntityLayer3D.ApplyStyle</c> 对 <c>IAnimatedActor3D</c> 直接 early-return，
///    否则 <c>ActorMesh3D.ApplyStyle</c> 会按 <c>EntityStyle</c> 每帧把颜色刷成单色、
///    还会重设缩放，Boss 特有的配色和体型会被抹掉；
/// ② 四个技能（投弹/闪现/锤地/嚎叫）至少能看出"在放招"（前倾 + 起手），
///    不会因为没 clip 而完全没有反馈。
///
/// 真模型放到 <see cref="ActorCatalog3D.BossModelPath"/> 后，
/// <see cref="ActorCatalog3D.CreateBoss"/> 会改走 RiggedActor3D 分支，这个类就不再被创建；
/// 它只做"没有资源也能跑"，不做动画状态机。
/// </summary>
public partial class BossPlaceholder3D : Node3D, IAnimatedActor3D
{
    // 占位体本地尺寸（格）。整体缩放统一交给 Visual 节点的 BossModelScale，
    // 所以真模型就位后"体型大小感"可以由同一个常量控制。
    private const float BodyHeight = 2.2f;
    private const float BodyRadius = 0.5f;
    private const float HeadRadius = 0.42f;

    private static readonly Color BossBodyColor = new(0.33f, 0.16f, 0.42f);
    private static readonly Color BossBoneColor = new(0.86f, 0.82f, 0.66f);

    private readonly List<StandardMaterial3D> _flashMats = new();
    private float _modelScale = ActorCatalog3D.BossModelScale;
    private Node3D? _visual;
    private bool _moving;
    private bool _dead;
    private float _phase;
    private float _pulse;
    private float _deathTilt;

    public float AnimSpeedMul { get; set; } = 1f;
    public bool ApplyToon { get; set; }

    [Export(PropertyHint.Range, "0.2,3,0.05")]
    public float ModelScale
    {
        get => _modelScale;
        set
        {
            _modelScale = Mathf.Max(0.05f, value);
            if (_visual is not null)
                _visual.Scale = Vector3.One * _modelScale;
        }
    }

    public override void _Ready() => Build();

    public void SetLocomotion(bool moving, float tilesPerSec = 10f) => _moving = moving;

    public void PlayAction(ActionKind kind)
    {
        // 没有专属 clip 也要有区别：重招（锤地/嚎叫）幅度更大，轻招（投弹/闪现）小一点。
        _pulse = kind switch
        {
            ActionKind.BossSlam => 1.4f,
            ActionKind.BossRoar => 1.2f,
            ActionKind.BossLeap => 1.0f,
            ActionKind.BossThrow => 0.9f,
            _ => 0.7f,
        };
    }

    public void FinishAction() => _pulse = 0f;

    public void CancelAction()
    {
        _pulse = 0f;
        _dead = false;
        _deathTilt = 0f;
    }

    public void PlayHit()
    {
        _pulse = Mathf.Max(_pulse, 0.6f);
        // 闪白由 EntityLayer3D 的 _flashUntil 统一收尾（到期会回调 SetFlash(false)）。
        SetFlash(true);
    }

    public void PlayDeath()
    {
        _dead = true;
        _pulse = 0f;
        SetFlash(false);
    }

    public void SetFlash(bool on)
    {
        foreach (var mat in _flashMats)
        {
            mat.EmissionEnabled = on;
            mat.Emission = Colors.White;
            mat.EmissionEnergyMultiplier = on ? 0.6f : 0f;
        }
    }

    public override void _Process(double delta)
    {
        if (_visual is null) return;
        var dt = (float)delta;
        if (_dead)
        {
            // 死亡：向侧面倒下并停住，和 RiggedActor3D 死亡 clip 定格同一语义。
            _deathTilt = Mathf.MoveToward(_deathTilt, 82f, dt * 160f);
            _visual.Position = Vector3.Zero;
            _visual.RotationDegrees = new Vector3(0, 0, _deathTilt);
            return;
        }

        _phase += dt * (_moving ? 6.5f : 2.2f) * Mathf.Max(0.05f, AnimSpeedMul);
        _pulse = Mathf.Max(0f, _pulse - dt * 2.2f);
        var bob = Mathf.Sin(_phase) * (_moving ? 0.1f : 0.04f);
        _visual.Position = new Vector3(0, bob, 0);
        // 起手前倾：纯程序化，占位阶段用来看"技能确实触发了"。
        _visual.RotationDegrees = new Vector3(-_pulse * 16f, 0, 0);
    }

    private void Build()
    {
        if (_visual is not null) return;
        _visual = new Node3D
        {
            Name = "Visual",
            Scale = Vector3.One * _modelScale,
        };
        AddChild(_visual);

        var bodyMat = MakeMat(BossBodyColor);
        var headMat = MakeMat(BossBoneColor);
        var eyeMat = MakeMat(new Color(0.95f, 0.25f, 0.18f));
        eyeMat.EmissionEnabled = true;
        eyeMat.Emission = new Color(0.95f, 0.25f, 0.18f);
        eyeMat.EmissionEnergyMultiplier = 0.5f;
        // 身体/头参与受击闪白；眼睛保持红亮，闪白时不跟着变。
        _flashMats.Add(bodyMat);
        _flashMats.Add(headMat);

        // 网格用"本地尺寸"建，缩放统一交给 Visual 节点的 BossModelScale，
        // 避免和 _visual.Scale 叠加造成平方放大。高度 = 顶到脚，比玩家（约 1 格）高一截。
        _visual.AddChild(new MeshInstance3D
        {
            Name = "Body",
            Position = new Vector3(0, BodyHeight * 0.5f, 0),
            Mesh = new CapsuleMesh { Radius = BodyRadius, Height = BodyHeight },
            MaterialOverride = bodyMat,
        });
        _visual.AddChild(new MeshInstance3D
        {
            Name = "Head",
            Position = new Vector3(0, BodyHeight + HeadRadius * 0.7f, 0),
            Mesh = new SphereMesh { Radius = HeadRadius, Height = HeadRadius * 2f },
            MaterialOverride = headMat,
        });
        // 朝向约定与 RiggedActor3D 一致：Godot 里模型正面朝 -Z。
        for (var i = 0; i < 2; i++)
        {
            _visual.AddChild(new MeshInstance3D
            {
                Name = i == 0 ? "EyeL" : "EyeR",
                Position = new Vector3(
                    (i == 0 ? -1f : 1f) * BodyRadius * 0.42f,
                    BodyHeight + HeadRadius * 0.8f,
                    -HeadRadius * 0.82f),
                Mesh = new SphereMesh { Radius = BodyRadius * 0.16f, Height = BodyRadius * 0.32f },
                MaterialOverride = eyeMat,
            });
        }

        _visual.AddChild(new Label3D
        {
            Name = "Marker",
            Text = "首领",
            Position = new Vector3(0, BodyHeight + HeadRadius * 2.2f, 0),
            PixelSize = 0.012f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            OutlineSize = 8,
            Modulate = Colors.White,
        });
    }

    private static StandardMaterial3D MakeMat(Color color) => new()
    {
        AlbedoColor = color,
        Roughness = 0.8f,
        Metallic = 0f,
    };
}
