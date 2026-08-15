using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>云影：6 朵世界锚定的大软暗影，缓慢漂移，贴地压暗。</summary>
public partial class CloudShadowView : Node2D
{
	private static readonly (float X, float Y, float R)[] Bases =
	{
		(30, 30, 14), (90, 42, 17), (52, 95, 15), (108, 88, 19), (70, 20, 13), (20, 100, 16),
	};

	private readonly Sprite2D[] _sprites = new Sprite2D[Bases.Length];

	public CloudShadowView()
	{
		var soft = MakeSoft();
		for (var i = 0; i < Bases.Length; i++)
		{
			_sprites[i] = new Sprite2D
			{
				Texture = soft,
				Centered = true,
				Modulate = new Color(0, 0, 0, 0.13f + (i % 3) * 0.04f),
			};
			AddChild(_sprites[i]);
		}
	}

	public override void _Process(double delta)
	{
		var t = (float)Time.GetTicksMsec() / 1000f;
		for (var i = 0; i < Bases.Length; i++)
		{
			var (bx, by, r) = Bases[i];
			var x = bx + Mathf.Sin(t * 0.03f + i * 2.1f) * 18f;
			var y = by + Mathf.Cos(t * 0.026f + i * 1.4f) * 14f;
			var local = IsoMath.WorldToLocal(x, y);
			_sprites[i].Position = new Vector2(local.X, local.Y);
			_sprites[i].Scale = new Vector2(r * 1.6f, r * 0.7f);
		}
	}

	private static Texture2D MakeSoft()
	{
		const int s = 128;
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
