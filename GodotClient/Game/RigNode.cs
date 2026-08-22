using System;
using System.Collections.Generic;
using Godot;
using Starve.Game.V1;

namespace GodotClient.Game;

public enum CharacterFacing
{
    Front,
    Back,
    SideLeft,
    SideRight,
}

/// <summary>动画帧序列规格：目录 pattern（{0} 帧号）+ 帧数 + fps + 是否循环。</summary>
public sealed record AnimSpec(string Pattern, int Frames, float Fps, bool Loop, int FirstIndex);

/// <summary>角色骨架规格：预烘焙帧 + 缩放/脚底位置；缺 hit 时回退 idle。</summary>
public sealed record RigSpec(
    string Id,
    int FrameW,
    int FrameH,
    float Scale,
    float FootY,
    IReadOnlyDictionary<string, AnimSpec> Anims);

/// <summary>
/// 生物骨架注册表：CreatureKind → 动画角色。
/// 主角用鱼人（Player）；蜥蜴为服务端预留 kind（7），资源已就位，服务端下发即生效。
/// </summary>
public static class RigRegistry
{
    /// <summary>主角规格：鱼人（人鱼）。</summary>
    public static RigSpec Player => Fishman;

    public static RigSpec? RigOf(int creatureKind) => creatureKind switch
    {
        (int)CreatureKind.Lizard => Lizard,
        _ => null,
    };

    // 鱼人（人鱼）：1024² 透明帧，主体统一为 360px 高并按脚底线对齐。
    // idle/walk/attack/hit 各 15 帧（1 起始编号）；主体视觉高统一为 64px。
    private static readonly RigSpec Fishman = new(
        "fishman", 1024, 1024, RigPresentationMetrics.FishmanVisualHeight / 360f, 456f / 1024f,
        new Dictionary<string, AnimSpec>
        {
            ["idle"] = new("res://assets/fishman/anim/idle/cutout/frame_{0:000}.png", 15, 6, true, 1),
            ["walk"] = new("res://assets/fishman/anim/walk/cutout/frame_{0:000}.png", 15, 7.5f, true, 1),
            ["attack"] = new("res://assets/fishman/anim/attack/cutout/frame_{0:000}.png", 15, RigPresentationMetrics.FishmanAttackFps, false, 1),
            ["hit"] = new("res://assets/fishman/anim/hit/cutout/frame_{0:000}.png", 15, 15, false, 1),
        });

    // 蜥蜴：256×512 透明抠图帧，idle/walk/attack 各 8 帧（0 起始编号；无 hit，受击回退 idle + 闪白）
    private static readonly RigSpec Lizard = new(
        "lizard", 256, 512, 0.13f, 0.9f,
        new Dictionary<string, AnimSpec>
        {
            ["idle"] = new("res://assets/lizard/anim/idle/cutout/idle_{0}.png", 8, 8, true, 0),
            ["walk"] = new("res://assets/lizard/anim/walk/cutout/walk_{0}.png", 8, 10, true, 0),
            ["attack"] = new("res://assets/lizard/anim/attack/cutout/attack_{0}.png", 8, RigPresentationMetrics.LizardAttackFps, false, 0),
        });
}

/// <summary>
/// 生物帧动画节点：AnimatedSprite2D 播放预烘焙帧序列，带投影/血条/名字/受击闪白。
/// 无对应动画（如蜥蜴 hit）时回退 idle。
/// </summary>
public partial class RigNode : Node2D
{
    private static readonly Dictionary<string, SpriteFrames> FramesCache = new();

    private readonly Node2D _visualRoot = new();
    private readonly AnimatedSprite2D _sprite = new();
    private readonly RigSpec _rig;
    private readonly SideRig? _sideRig;
    private readonly BackRig? _backRig;
    private Label? _nameLabel;
    private float _sunT = 1f;
    private int _healthCur;
    private int _healthMax;
    private bool _showBar;
    private long _flashUntil;
    private bool _hitQueued;
    private float _facing = 1f;
    private float _turnT;
    private CharacterFacing _characterFacing = CharacterFacing.Front;
    private bool _actionActive;
    private bool _actionFinishing;
    private string _actionAnimation = "idle";
    private bool _haunting;
    private bool _moving;
    private readonly SpiritPresentationState _spirit = new();

    public RigNode(RigSpec rig)
    {
        _rig = rig;
        _sprite.SpriteFrames = SpriteFramesOf(rig);
        _sprite.Scale = Vector2.One * rig.Scale;
        // 帧图脚底（FootY 比例处，0=帧顶 1=帧底）对齐世界坐标原点：
        // 精灵中心在 P，帧内 (FootY*H - H/2) 处的世界 y = P + (FootY-0.5)*H*S = 0 → P = H*S*(0.5-FootY)
        _sprite.Position = new Vector2(0, rig.FrameH * rig.Scale * (0.5f - rig.FootY));
        _sprite.Animation = "idle";
        _sprite.Play();
        AddChild(_visualRoot);
        _visualRoot.AddChild(_sprite);
        if (rig.Id == "fishman")
        {
            _sideRig = new SideRig { Visible = false };
            _backRig = new BackRig { Visible = false };
            _visualRoot.AddChild(_sideRig);
            _visualRoot.AddChild(_backRig);
        }
    }

    /// <summary>组装 SpriteFrames：按 AnimSpec 逐个加载帧图（缓存，多个同类角色共享）。</summary>
    private static SpriteFrames SpriteFramesOf(RigSpec rig)
    {
        if (FramesCache.TryGetValue(rig.Id, out var cached)) return cached;
        var sf = new SpriteFrames();
        foreach (var (name, spec) in rig.Anims)
        {
            sf.AddAnimation(name);
            sf.SetAnimationLoopMode(name, spec.Loop ? SpriteFrames.LoopMode.Linear : SpriteFrames.LoopMode.None);
            sf.SetAnimationSpeed(name, spec.Fps);
            for (var i = 0; i < spec.Frames; i++)
            {
                var path = string.Format(spec.Pattern, spec.FirstIndex + i);
                sf.AddFrame(name, GD.Load<Texture2D>(path));
            }
        }
        // 无 idle 的规格兜底：至少保证能播放
        if (sf.GetAnimationNames().Length == 0) sf.AddAnimation("idle");
        FramesCache[rig.Id] = sf;
        return sf;
    }

    public void Configure(int healthCur, int healthMax, bool showBar, string name = "")
    {
        _healthCur = healthCur;
        _healthMax = healthMax;
        _showBar = showBar;
        UpdateNameLabel(name);
    }

    public void PlayHit()
    {
        if (_spirit.IsDead) return;
        _flashUntil = (long)Time.GetTicksMsec() + 300;
        _hitQueued = true;
    }

    public void ClearPredictedHit()
    {
        _flashUntil = 0;
        _hitQueued = false;
        if (_sprite.Animation == "hit")
        {
            PlayAnimFromStart(_actionActive ? _actionAnimation : _moving ? "walk" : "idle");
        }
    }

    public bool SetDead(bool dead)
    {
        if (!_spirit.SetDead(dead)) return false;
        _visualRoot.Position = Vector2.Zero;
        _sprite.Modulate = Colors.White;
        if (!dead) return true;

        CancelAction();
        _hitQueued = false;
        _flashUntil = 0;
        _sprite.Visible = true;
        if (_sideRig is not null) _sideRig.Visible = false;
        if (_backRig is not null) _backRig.Visible = false;
        PlayAnimFromStart("idle");
        return true;
    }

    public void SetSunT(float sunT)
    {
        var next = Mathf.Clamp(sunT, 0f, 1f);
        if (Mathf.IsEqualApprox(_sunT, next)) return;
        _sunT = next;
        QueueRedraw();
    }

    /// <summary>应用动作视觉。动作类型到素材的映射只存在于 Rig 适配层。</summary>
    public void Apply(ActionKind kind)
    {
        if (_spirit.IsDead && kind != ActionKind.Haunt) return;
        _actionActive = true;
        _actionFinishing = false;
        _haunting = kind == ActionKind.Haunt;
        _actionAnimation = kind switch
        {
            ActionKind.Attack or ActionKind.Chop or ActionKind.Mine or ActionKind.Pick => "attack",
            // Craft/Sleep/Haunt 暂无专用素材：保持 idle，但仍锁住 walk。
            ActionKind.Craft or ActionKind.Sleep or ActionKind.Haunt => "idle",
            _ => "idle",
        };
        PlayAnimFromStart(_actionAnimation);
    }

    /// <summary>权威完成：解除动作锁，但让当前非循环 clip 自然播放到尾帧。</summary>
    public void FinishAction()
    {
        _actionActive = false;
        _haunting = false;
        _actionFinishing =
            _sprite.Animation == _actionAnimation &&
            _sprite.IsPlaying() &&
            _rig.Anims.TryGetValue(_actionAnimation, out var spec) &&
            !spec.Loop;
        if (!_actionFinishing)
        {
            _actionAnimation = "idle";
            PlayAnimFromStart(_moving ? "walk" : "idle");
        }
    }

    /// <summary>预测超时、移动或取消：立即停止动作并恢复 walk/idle。</summary>
    public void CancelAction()
    {
        _actionActive = false;
        _haunting = false;
        _actionFinishing = false;
        _actionAnimation = "idle";
        PlayAnimFromStart(_moving ? "walk" : "idle");
    }

    /// <summary>按移动方向水平翻转（-1/1；静止时保持上一朝向）。</summary>
    public void SetFacing(float dir)
    {
        if (dir == 0 || dir == _facing) return;
        _facing = dir;
        _sprite.Scale = new Vector2(Mathf.Abs(_sprite.Scale.X) * dir, _sprite.Scale.Y);
        _turnT = 1f; // 转身瞬间给一点旋转，让方向变化在正面帧上也看得出来
    }

    public void SetMovementDirection(float worldDX, float worldDY, float viewSin, float viewCos)
    {
        var isoX = worldDX - worldDY;
        var isoY = (worldDX + worldDY) * 0.5f;
        var screenX = isoX * viewCos - isoY * viewSin;
        var screenY = isoX * viewSin + isoY * viewCos;
        if (_sideRig is null || _backRig is null)
        {
            SetFacing(MathF.Sign(screenX));
            return;
        }
        _characterFacing = MathF.Abs(screenY) >= MathF.Abs(screenX)
            ? screenY < 0 ? CharacterFacing.Back : CharacterFacing.Front
            : screenX < 0 ? CharacterFacing.SideLeft : CharacterFacing.SideRight;
    }

    public void Update(double deltaMs, bool moving)
    {
        _moving = moving;
        var spirit = _spirit.Advance(deltaMs);
        if (_spirit.IsDead)
        {
            _visualRoot.Position = new Vector2(0, spirit.BobOffset);
            _sprite.Visible = true;
            if (_sideRig is not null) _sideRig.Visible = false;
            if (_backRig is not null) _backRig.Visible = false;
            PlayAnim("idle", true);
            _sprite.Modulate = new Color(
                (_haunting ? 0.58f : 0.72f) * spirit.Brightness,
                (_haunting ? 1.02f : 0.9f) * spirit.Brightness,
                (_haunting ? 1.3f : 1.15f) * spirit.Brightness,
                spirit.Alpha);
            QueueRedraw();
            return;
        }
        _visualRoot.Position = Vector2.Zero;
        if (_turnT > 0)
        {
            _turnT = Mathf.Max(0, _turnT - (float)(deltaMs / 120));
            _sprite.Rotation = _turnT * 0.10f * _facing;
        }
        else
        {
            _sprite.Rotation = 0;
        }

        if (_hitQueued)
        {
            PlayAnim("hit", false);
            _hitQueued = false;
        }
        else if (_sprite.Animation == "hit" && _sprite.IsPlaying())
        {
            // 受击视觉短暂优先；播完后恢复仍活跃的权威动作。
        }
        else if (_actionActive)
        {
            // 权威阶段更新不重启 clip；动画提前播完时停在尾帧等待 Outcome。
            if (_sprite.Animation != _actionAnimation) PlayAnimFromStart(_actionAnimation);
        }
        else if (_actionFinishing &&
                 _sprite.Animation == _actionAnimation &&
                 _sprite.IsPlaying())
        {
            // 完成后仅等待当前非循环 clip 自然收尾。
        }
        else
        {
            _actionFinishing = false;
            _actionAnimation = "idle";
            PlayAnim(moving ? "walk" : "idle", true);
        }

        var flash = (long)Time.GetTicksMsec() < _flashUntil;
        var color = flash
            ? new Color(1.6f, 1.6f, 1.6f)
            : Colors.White;
        _sprite.Modulate = color;
        UpdateDirectionalView(deltaMs, moving && !_actionActive && !_actionFinishing, color);
        if (flash) QueueRedraw();
    }

    private void UpdateDirectionalView(double deltaMs, bool moving, Color color)
    {
        if (_sideRig is null || _backRig is null) return;
        var actionPlaying = (_sprite.Animation == "attack" || _sprite.Animation == "hit") &&
                            _sprite.IsPlaying();
        var side = !actionPlaying &&
                   _characterFacing is CharacterFacing.SideLeft or CharacterFacing.SideRight;
        var back = !actionPlaying && _characterFacing == CharacterFacing.Back;
        _sprite.Visible = !side && !back;
        _sideRig.Visible = side;
        _backRig.Visible = back;
        if (side)
        {
            _sideRig.SetFacing(_characterFacing == CharacterFacing.SideLeft ? -1 : 1);
            _sideRig.Update(deltaMs, moving);
            _sideRig.Modulate = color;
        }
        if (back)
        {
            _backRig.Update(deltaMs, moving);
            _backRig.Modulate = color;
        }
    }

    private void PlayAnim(string name, bool loop)
    {
        if (!_rig.Anims.ContainsKey(name)) name = "idle";
        if (!_rig.Anims.ContainsKey(name)) return;
        if (_sprite.Animation == name && _sprite.IsPlaying()) return;
        _sprite.Play(name);
    }

    private void PlayAnimFromStart(string name)
    {
        if (!_rig.Anims.ContainsKey(name)) name = "idle";
        if (!_rig.Anims.ContainsKey(name)) return;
        _sprite.Stop();
        _sprite.Play(name);
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
        _nameLabel.Position = new Vector2(-70, -_rig.FrameH * _rig.Scale - 10);
        _nameLabel.Visible = true;
        if (_nameLabel.GetParent() is null) AddChild(_nameLabel);
    }

    public override void _Draw()
    {
        // 方向投影：太阳越高越短、越淡（与 EntityNode 同风格）
        var sun = Mathf.Clamp(_sunT, 0f, 1f);
        var stretch = Mathf.Lerp(1.15f, 0.85f, sun);
        const float foot = 15f;
        DrawEllipsePoly(
            new Vector2(-2f * stretch, 2),
            foot * stretch,
            foot * 0.32f,
            new Color(0, 0, 0, 0.18f + 0.12f * sun));

        if (_showBar && _healthMax > 0)
        {
            var ratio = Mathf.Clamp(_healthCur / (float)_healthMax, 0, 1);
            DrawRect(new Rect2(-11, -_rig.FrameH * _rig.Scale - 6, 22, 4), new Color(0.2f, 0.2f, 0.2f, 0.9f));
            DrawRect(new Rect2(-11, -_rig.FrameH * _rig.Scale - 6, 22 * ratio, 4),
                ratio > 0.3f ? new Color(0.31f, 0.75f, 0.37f) : new Color(0.89f, 0.34f, 0.30f));
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
