using System;
using Godot;

namespace GodotClient.Game;

/// <summary>项目 shader 目录。动画类在 <c>shaders/anim</c>。</summary>
public static class ShaderLibrary
{
    public const string Fire = "res://shaders/anim/fire.gdshader";
    public const string AlchemyBounce = "res://shaders/anim/alchemy-bounce.gdshader";
    public const string WillowFluff = "res://shaders/anim/willow-fluff.gdshader";
    public const string CloudSky = "res://shaders/anim/cloud-sky.gdshader";
    public const string CloudVolume = "res://shaders/anim/cloud-volume.gdshader";
    public const string CloudShadow = "res://shaders/anim/cloud-shadow.gdshader";

    public static Shader Load(string path)
    {
        if (GD.Load(path) is Shader imported)
            return imported;
        if (FileAccess.FileExists(path))
        {
            var code = FileAccess.GetFileAsString(path);
            if (!string.IsNullOrWhiteSpace(code))
                return new Shader { Code = code };
        }

        throw new InvalidOperationException("缺少 shader " + path);
    }
}
