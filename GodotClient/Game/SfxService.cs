using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 按 catalog id 播放音效：读 manifest、抽变体、冷却、UI / 2D 空间分流。
/// 事件层只报 id，不碰文件名。
/// </summary>
public partial class SfxService : Node
{
    public const string ManifestPath = "res://assets/audio/manifest.json";
    private const string AudioRoot = "res://assets/audio/";
    private const int UiVoiceLimit = 10;
    private const int WorldVoiceLimit = 16;

    private readonly Dictionary<string, SfxClip> _clips = new();
    private readonly Dictionary<string, long> _cooldownUntil = new();
    private readonly Dictionary<string, int> _lastVariant = new();
    private readonly List<AudioStreamPlayer> _uiVoices = new();
    private readonly List<AudioStreamPlayer2D> _worldVoices = new();
    private readonly RandomNumberGenerator _rng = new();
    private Node2D? _spatialRoot;

    public override void _Ready()
    {
        Name = "SfxService";
        _rng.Randomize();
        EnsureBus("SFX");
        EnsureBus("Ambient");
        LoadManifest();
    }

    public void SetSpatialRoot(Node2D root) => _spatialRoot = root;

    public bool Play(string id, Vector2? worldLocal = null)
    {
        if (!_clips.TryGetValue(id, out var clip) || clip.Files.Length == 0) return false;
        var now = (long)Time.GetTicksMsec();
        if (_cooldownUntil.TryGetValue(id, out var until) && now < until) return false;
        var stream = LoadVariant(clip);
        if (stream is null) return false;
        _cooldownUntil[id] = now + clip.CooldownMs;
        if (clip.Spatial && worldLocal is { } pos && _spatialRoot is not null)
        {
            PlayWorld(stream, clip.Bus, clip.VolumeDb, pos);
        }
        else
        {
            PlayUi(stream, clip.Bus, clip.VolumeDb);
        }
        return true;
    }

    private AudioStream? LoadVariant(SfxClip clip)
    {
        var index = 0;
        if (clip.Files.Length > 1)
        {
            index = (int)_rng.RandiRange(0, clip.Files.Length - 1);
            if (_lastVariant.TryGetValue(clip.Id, out var last) && index == last)
            {
                index = (index + 1) % clip.Files.Length;
            }
            _lastVariant[clip.Id] = index;
        }
        var path = AudioRoot + clip.Files[index];
        return GD.Load<AudioStream>(path);
    }

    private void PlayUi(AudioStream stream, string bus, float volumeDb)
    {
        var player = TakeUiVoice();
        player.Bus = bus;
        player.VolumeDb = volumeDb;
        player.Stream = stream;
        player.Play();
    }

    private void PlayWorld(AudioStream stream, string bus, float volumeDb, Vector2 pos)
    {
        var player = TakeWorldVoice();
        player.Bus = bus;
        player.VolumeDb = volumeDb;
        player.Stream = stream;
        player.Position = pos;
        player.Play();
    }

    private AudioStreamPlayer TakeUiVoice()
    {
        foreach (var voice in _uiVoices)
        {
            if (!voice.Playing) return voice;
        }
        if (_uiVoices.Count >= UiVoiceLimit) return _uiVoices[0];
        var created = new AudioStreamPlayer { Name = $"SfxUi{_uiVoices.Count}" };
        AddChild(created);
        _uiVoices.Add(created);
        return created;
    }

    private AudioStreamPlayer2D TakeWorldVoice()
    {
        foreach (var voice in _worldVoices)
        {
            if (!voice.Playing) return voice;
        }
        if (_worldVoices.Count >= WorldVoiceLimit) return _worldVoices[0];
        var created = new AudioStreamPlayer2D
        {
            Name = $"SfxWorld{_worldVoices.Count}",
            MaxDistance = 900,
            Attenuation = 1.4f,
        };
        if (_spatialRoot is { } root) root.AddChild(created);
        else AddChild(created);
        _worldVoices.Add(created);
        return created;
    }

    private void LoadManifest()
    {
        if (!FileAccess.FileExists(ManifestPath))
        {
            GD.PushWarning($"SFX manifest missing: {ManifestPath}");
            return;
        }
        using var file = FileAccess.Open(ManifestPath, FileAccess.ModeFlags.Read);
        var parsed = Json.ParseString(file.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) return;
        var root = parsed.AsGodotDictionary();
        if (root["sounds"].AsGodotArray() is not { Count: > 0 } sounds) return;
        foreach (var item in sounds)
        {
            if (item.VariantType != Variant.Type.Dictionary) continue;
            var entry = item.AsGodotDictionary();
            var id = entry["id"].AsString();
            if (string.IsNullOrEmpty(id)) continue;
            var files = new List<string>();
            foreach (var fileName in entry["files"].AsGodotArray())
            {
                var name = fileName.AsString();
                if (!string.IsNullOrEmpty(name)) files.Add(name);
            }
            _clips[id] = new SfxClip(
                id,
                entry["bus"].AsString(),
                entry["spatial"].AsBool(),
                entry["loop"].AsBool(),
                (int)entry["cooldown_ms"].AsInt32(),
                entry.ContainsKey("volume_db") ? (float)entry["volume_db"].AsDouble() : 0f,
                files.ToArray());
        }
    }

    private static void EnsureBus(string name)
    {
        if (AudioServer.GetBusIndex(name) >= 0) return;
        AudioServer.AddBus();
        var index = AudioServer.BusCount - 1;
        AudioServer.SetBusName(index, name);
        AudioServer.SetBusSend(index, "Master");
    }

    private readonly record struct SfxClip(
        string Id,
        string Bus,
        bool Spatial,
        bool Loop,
        int CooldownMs,
        float VolumeDb,
        string[] Files);
}
