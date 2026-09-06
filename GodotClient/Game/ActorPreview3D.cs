using Godot;
using Starve.Game.V1;

namespace GodotClient.Game;

public enum CharacterPreviewKind
{
	Fishman,
	Lizard,
	Spider,
}

public enum CharacterPreviewDisplay
{
	SpriteArt,
	ToonMesh,
}

/// <summary>
/// 可在编辑器 3D 视口里预览、用检查器改属性的角色。
/// 选中节点后改 Kind / PixelSize / Toon 即可，不必运行游戏。
/// </summary>
[Tool]
public partial class ActorPreview3D : Node3D, IAnimatedActor3D
{
	private CharacterPreviewKind _kind = CharacterPreviewKind.Fishman;
	private CharacterPreviewDisplay _display = CharacterPreviewDisplay.SpriteArt;
	private float _pixelSize = 0.003f;
	private float _extraScale = 1f;
	private bool _billboard = true;
	private Color _toonAlbedo = new(0.31f, 0.75f, 0.37f);
	private float _toonBands = 3f;
	private float _outlineWidth = 0.028f;
	private float _rim = 0.35f;
	private PackedScene? _modelOverride;
	private bool _rebuildQueued;
	private bool _moving;
	private bool _actionActive;
	private bool _dead;
	private float _animSpeedMul = 1f;

	[Export]
	public CharacterPreviewKind Kind
	{
		get => _kind;
		set { _kind = value; RequestRebuild(); }
	}

	[Export]
	public CharacterPreviewDisplay Display
	{
		get => _display;
		set { _display = value; RequestRebuild(); }
	}

	[Export(PropertyHint.Range, "0.0005,0.02,0.0001")]
	public float PixelSize
	{
		get => _pixelSize;
		set { _pixelSize = Mathf.Max(0.0001f, value); RequestRebuild(); }
	}

	[Export(PropertyHint.Range, "0.2,3,0.05")]
	public float ExtraScale
	{
		get => _extraScale;
		set { _extraScale = Mathf.Max(0.05f, value); RequestRebuild(); }
	}

	public float ModelScale
	{
		get => ExtraScale;
		set => ExtraScale = value;
	}

	public float AnimSpeedMul
	{
		get => _animSpeedMul;
		set => _animSpeedMul = Mathf.Max(0.05f, value);
	}

	public bool ApplyToon { get; set; }

	[Export]
	public bool Billboard
	{
		get => _billboard;
		set { _billboard = value; RequestRebuild(); }
	}

	[Export]
	public Color ToonAlbedo
	{
		get => _toonAlbedo;
		set { _toonAlbedo = value; RequestRebuild(); }
	}

	[Export(PropertyHint.Range, "2,6,1")]
	public float ToonBands
	{
		get => _toonBands;
		set { _toonBands = value; RequestRebuild(); }
	}

	[Export(PropertyHint.Range, "0.005,0.08,0.001")]
	public float OutlineWidth
	{
		get => _outlineWidth;
		set { _outlineWidth = value; RequestRebuild(); }
	}

	[Export(PropertyHint.Range, "0,1,0.01")]
	public float Rim
	{
		get => _rim;
		set { _rim = value; RequestRebuild(); }
	}

	[Export]
	public PackedScene? ModelOverride
	{
		get => _modelOverride;
		set { _modelOverride = value; RequestRebuild(); }
	}

	public override void _Ready() => Rebuild();

	public override void _EnterTree()
	{
		if (Engine.IsEditorHint()) RequestRebuild();
	}

	public void SetFlash(bool on)
	{
		var sprite = GetNodeOrNull<AnimatedSprite3D>("Visual/Sprite");
		if (sprite is not null)
		{
			sprite.Modulate = on ? new Color(1.6f, 1.6f, 1.6f) : Colors.White;
			return;
		}
		var mat = ActorMesh3D.MaterialOf(GetNodeOrNull<Node3D>("Visual") ?? this);
		if (mat is not null) ToonMaterials.SetFlash(mat, on);
	}

	public void SetLocomotion(bool moving, float tilesPerSec = 10f)
	{
		_moving = moving;
		if (_actionActive || _dead) return;
		PlaySprite(moving && HasSpriteAnim("walk") ? "walk" : "idle", true,
			moving ? Mathf.Clamp(tilesPerSec / 6f, 0.7f, 2.2f) * _animSpeedMul : _animSpeedMul);
	}

	public void PlayAction(ActionKind kind)
	{
		if (_dead) return;
		_actionActive = true;
		var clip = kind is ActionKind.Attack or ActionKind.Chop or ActionKind.Mine or ActionKind.Pick
			? "attack"
			: "idle";
		PlaySprite(HasSpriteAnim(clip) ? clip : "idle", false, _animSpeedMul);
	}

	public void FinishAction()
	{
		_actionActive = false;
		if (!_dead) SetLocomotion(_moving);
	}

	public void CancelAction()
	{
		_actionActive = false;
		if (!_dead) SetLocomotion(_moving);
	}

	public void PlayHit()
	{
		if (_dead) return;
		if (HasSpriteAnim("hit"))
			PlaySprite("hit", false, 1.2f * _animSpeedMul);
		else
			SetFlash(true);
	}

	public void PlayDeath()
	{
		_dead = true;
		_actionActive = false;
		PlaySprite("idle", false, 0f);
	}

	private void RequestRebuild()
	{
		if (!IsInsideTree() || _rebuildQueued) return;
		_rebuildQueued = true;
		CallDeferred(MethodName.Rebuild);
	}

	private void Rebuild()
	{
		_rebuildQueued = false;
		var old = GetNodeOrNull<Node>("Visual");
		if (old is not null)
		{
			RemoveChild(old);
			old.Free();
		}

		var visual = new Node3D { Name = "Visual" };
		AddChild(visual);

		if (_modelOverride is not null)
		{
			var model = _modelOverride.Instantiate<Node3D>();
			visual.AddChild(model);
			visual.Scale = Vector3.One * _extraScale;
			return;
		}

		if (_display == CharacterPreviewDisplay.ToonMesh)
		{
			BuildToon(visual);
			return;
		}

		BuildSprite(visual);
	}

	private void BuildSprite(Node3D visual)
	{
		var rig = Spec();
		var sprite = new AnimatedSprite3D
		{
			Name = "Sprite",
			SpriteFrames = RigNode.SpriteFramesOf(rig),
			PixelSize = _pixelSize,
			Centered = true,
			Shaded = false,
			Transparent = true,
			AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
			TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
			Billboard = _billboard
				? BaseMaterial3D.BillboardModeEnum.FixedY
				: BaseMaterial3D.BillboardModeEnum.Disabled,
			Offset = new Vector2(0, rig.FrameH * (0.5f - rig.FootY)),
			Scale = Vector3.One * _extraScale,
		};
		visual.AddChild(sprite);
		if (sprite.SpriteFrames is not null && sprite.SpriteFrames.HasAnimation("idle"))
		{
			sprite.Animation = "idle";
			sprite.Play();
		}
		sprite.AnimationFinished += () =>
		{
			if (_dead || _actionActive) return;
			SetLocomotion(_moving);
		};
	}

	private void BuildToon(Node3D visual)
	{
		var style = new EntityStyle(_toonAlbedo, RadiusOf(Kind), false);
		var mesh = ActorMesh3D.Create(style);
		var mat = ActorMesh3D.MaterialOf(mesh);
		if (mat is not null)
		{
			ToonMaterials.SetAlbedo(mat, _toonAlbedo);
			mat.SetShaderParameter("bands", _toonBands);
			mat.SetShaderParameter("rim", _rim);
			if (mat.NextPass is ShaderMaterial outline)
				outline.SetShaderParameter("outline_width", _outlineWidth);
		}
		mesh.Scale = Vector3.One * _extraScale;
		visual.AddChild(mesh);
	}

	private RigSpec Spec() => Kind switch
	{
		CharacterPreviewKind.Lizard => RigRegistry.Named("lizard"),
		CharacterPreviewKind.Spider => RigRegistry.Named("spider"),
		_ => RigRegistry.Player,
	};

	private static float RadiusOf(CharacterPreviewKind kind) => kind switch
	{
		CharacterPreviewKind.Spider => 9f,
		CharacterPreviewKind.Lizard => 9f,
		_ => 10f,
	};

	private bool HasSpriteAnim(string name)
	{
		var sprite = GetNodeOrNull<AnimatedSprite3D>("Visual/Sprite");
		return sprite?.SpriteFrames is not null && sprite.SpriteFrames.HasAnimation(name);
	}

	private void PlaySprite(string name, bool loop, float speed)
	{
		var sprite = GetNodeOrNull<AnimatedSprite3D>("Visual/Sprite");
		if (sprite?.SpriteFrames is null || !sprite.SpriteFrames.HasAnimation(name)) return;
		sprite.SpriteFrames.SetAnimationLoopMode(name, loop ? SpriteFrames.LoopMode.Linear : SpriteFrames.LoopMode.None);
		if (sprite.Animation != name) sprite.Play(name);
		else if (!sprite.IsPlaying() && speed > 0.01f) sprite.Play();
		sprite.SpeedScale = speed;
	}
}
