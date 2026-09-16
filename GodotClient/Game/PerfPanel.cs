using System;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>主场景性能条。F3 显隐。可打开本机网页和日志目录。</summary>
public partial class PerfPanel : Control
{
	private DebugPanelChrome? _chrome;
	private Label _fps = null!;
	private Label _cpu = null!;
	private Label _ram = null!;
	private Label _vram = null!;
	private Label _draw = null!;
	private Label _pacing = null!;
	private Label _frameMs = null!;
	private Label _url = null!;
	private PerfMonitor? _monitor;

	public Action<bool>? TerrainToggled { get; set; }
	public Action<bool>? CloudsToggled { get; set; }
	public Action<bool>? PlantsToggled { get; set; }
	public Action<bool>? GlowToggled { get; set; }

	public override void _Ready()
	{
		Name = "PerfPanel";
		_chrome = new DebugPanelChrome(this, "性能  F3", DebugPanelChrome.Corner.BottomLeft, startCollapsed: true);
		var box = _chrome.Body;
		box.CustomMinimumSize = new Vector2(320, 0);

		_fps = AddLine(box, "FPS");
		_frameMs = AddLine(box, "帧");
		_cpu = AddLine(box, "CPU");
		_ram = AddLine(box, "内存");
		_vram = AddLine(box, "显存");
		_draw = AddLine(box, "绘制");
		_pacing = AddLine(box, "帧节拍");
		_url = new Label
		{
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			Text = "网页未启动",
		};
		box.AddChild(_url);

		var row = new HBoxContainer();
		var openWeb = new Button { Text = "打开网页" };
		openWeb.Pressed += () =>
		{
			if (_monitor?.Url is { } url) OS.ShellOpen(url);
		};
		var openDir = new Button { Text = "日志目录" };
		openDir.Pressed += () =>
		{
			if (_monitor is not null) OS.ShellOpen(_monitor.LogDir);
		};
		row.AddChild(openWeb);
		row.AddChild(openDir);
		box.AddChild(row);

		box.AddChild(new Label
		{
			Text = "隔离：关一项看帧时。折叠/关闭面板本身也能对比。",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		});
		AddToggle(box, "地形", on => TerrainToggled?.Invoke(on));
		AddToggle(box, "体积云", on => CloudsToggled?.Invoke(on));
		AddToggle(box, "植物", on => PlantsToggled?.Invoke(on));
		AddToggle(box, "Glow", on => GlowToggled?.Invoke(on));
		_chrome.Fit();
	}

	public void Toggle() => _chrome?.ToggleVisible();

	public void Bind(PerfMonitor? monitor)
	{
		_monitor = monitor;
		if (_url is null) return;
		_url.Text = monitor?.Url is { } url
			? url + "\n" + monitor.LogPath
			: "未采样（--smoke 或 STARVE_PERF=0）";
		_chrome?.Fit();
		Callable.From(() => _chrome?.Fit()).CallDeferred();
	}

	public void Render(in PerfSnapshot snap, float liveFps, in FrameTimeReport pacing = default)
	{
		if (snap.UnixMs == 0 && liveFps <= 0) return;
		var fps = liveFps > 0.01f ? liveFps : snap.Fps;
		_fps.Text = $"FPS  {fps:0.0}";
		_frameMs.Text = $"帧  {snap.FrameMs:0.0} ms  逻辑 {snap.ProcessMs:0.0}  物理 {snap.PhysicsMs:0.0}";
		_cpu.Text = $"CPU  {snap.CpuPercent:0.0}%";
		_ram.Text = $"内存  {PerfSnapshotJson.FormatBytes(snap.WorkingSetBytes)}  托管 {PerfSnapshotJson.FormatBytes(snap.ManagedBytes)}";
		_vram.Text = $"显存  {PerfSnapshotJson.FormatBytes(snap.VideoMemBytes)}  贴图 {PerfSnapshotJson.FormatBytes(snap.TextureMemBytes)}";
		_draw.Text = $"绘制  {snap.DrawCalls} calls  {snap.Primitives} tris  节点 {snap.NodeCount}";
		if (pacing.Samples > 0)
		{
			// 帧节拍：中位/P95/最坏 + 尖峰占比。平均 FPS 正常但这里很差 = 手感卡。
			_pacing.Text = $"帧节拍  中位 {pacing.MedianMs:0.0}  P95 {pacing.P95Ms:0.0}  " +
						   $"最坏 {pacing.WorstMs:0.0} ms  尖峰 {pacing.SpikeCount}/{pacing.Samples}" +
						   $"（{pacing.SpikeRatio * 100f:0.0}%）";
		}
	}

	public bool Hits(Vector2 screen) =>
		Visible && _chrome is { } chrome && chrome.Frame.GetGlobalRect().HasPoint(screen);

	private static Label AddLine(VBoxContainer box, string text)
	{
		var label = new Label { Text = text };
		box.AddChild(label);
		return label;
	}

	private static void AddToggle(VBoxContainer box, string title, Action<bool> onToggle)
	{
		var toggle = new CheckButton
		{
			Text = title + "（已开）",
			ButtonPressed = true,
		};
		toggle.Toggled += on =>
		{
			toggle.Text = on ? title + "（已开）" : title + "（已关）";
			onToggle(on);
		};
		box.AddChild(toggle);
	}
}
