using System;
using Godot;

namespace GodotClient.Game;

/// <summary>项目 shader 目录。动画类在 <c>shaders/anim</c>。</summary>
public static class ShaderLibrary
{
    public const string Fire = "res://shaders/anim/fire.gdshader";
    public const string AlchemyBounce = "res://shaders/anim/alchemy-bounce.gdshader";
    public const string LiquidRise = "res://shaders/anim/liquid-rise.gdshader";
    public const string CloudSky = "res://shaders/anim/cloud-sky.gdshader";
    public const string CloudVolume = "res://shaders/anim/cloud-volume.gdshader";
    public const string CloudShadow = "res://shaders/anim/cloud-shadow.gdshader";
    public const string TerrainHeightBlend = "res://shaders/terrain/height-blend.gdshader";
    public const string PanoramaTint = "res://shaders/env/panorama-tint.gdshader";

    public static Shader Load(string path)
    {
        if (FileAccess.FileExists(path))
        {
            var code = FileAccess.GetFileAsString(path);
            if (!string.IsNullOrWhiteSpace(code) && !code.Contains("#include"))
                return new Shader { Code = code };
        }

        if (GD.Load(path) is Shader imported)
            return imported;

        throw new InvalidOperationException("缺少 shader " + path);
    }
}
