using Godot;

namespace GodotClient.Game;

/// <summary>Freestylized 地面层：草 / 土 / 岩 / 雪的 albedo + height。</summary>
public static class TerrainHaven
{
    public const string GrassAlbedo = "res://assets/terrain_haven/aerial_grass_rock_diff.jpg";
    public const string GrassHeight = "res://assets/terrain_haven/aerial_grass_rock_disp.png";
    public const string DirtAlbedo = "res://assets/terrain_haven/park_dirt_diff.jpg";
    public const string DirtHeight = "res://assets/terrain_haven/park_dirt_disp.png";
    public const string RockAlbedo = "res://assets/terrain_haven/marble_rock_03_diff.jpg";
    public const string RockHeight = "res://assets/terrain_haven/marble_rock_03_disp.png";
    public const string SnowAlbedo = "res://assets/terrain_haven/snow_01_diff.jpg";
    public const string SnowHeight = "res://assets/terrain_haven/snow_01_disp.png";
    public const string SkyPanorama = "res://assets/env/sky_112_2k.png";

    public const float DefaultHeightSharpness = 0.22f;
    public const float DefaultSlopeRock = 0.72f;

    public static Texture2D Load(string path, Color fallback)
    {
        if (GD.Load<Texture2D>(path) is { } tex)
            return tex;
        var img = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        img.Fill(fallback);
        return ImageTexture.CreateFromImage(img);
    }
}
