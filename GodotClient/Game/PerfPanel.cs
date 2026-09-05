using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>主场景性能条。F3 显隐。可打开本机网页和日志目录。</summary>
public partial class PerfPanel : Control
{
	private PanelContainer? _box;
	private Label _fps = null!;
	private Label _cpu = null!;
	private Label _ram = null!;
	private Label _vram = null!;
	private Label _draw = null!;
	private Label _frameMs = null!;
	private Label _url = null!;
	private PerfMonitor? _monitor;

	public override void _Ready()
	{
		Name = "PerfPanel";
		SetAnchorsPreset(LayoutPreset.BottomLeft);
		OffsetLeft = 12;
		OffsetTop = -228;
		OffsetRight = 360;
		OffsetBottom = -12;
		MouseFilter = MouseFilterEnum.Ignore;
		Theme = HudTheme.Create();

		_box = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
		_box.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(_box);

		var box = new VBoxContainer();
		box.AddThemeConstantOverride("separation", 4);
		var margin = new MarginContainer();
		foreach (var side in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" })
			margin.AddThemeConstantOverride(side, 10);
		margin.AddChild(box);
		_box.AddChild(margin);

		box.AddChild(new Label { Text = "性能  F3 显隐" });
		_fps = AddLine(box, "FPS");
		_frameMs = AddLine(box, "帧");
		_cpu = AddLine(box, "CPU");
		_ram = AddLine(box, "内存");
		_vram = AddLine(box, "显存");
		_draw = AddLine(box, "绘制");
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
	}

	public void Bind(PerfMonitor? monitor)
	{
		_monitor = monitor;
		if (_url is null) return;
		_url.Text = monitor?.Url is { } url
			? url + "\n" + monitor.LogPath
			: "未采样（--smoke 或 STARVE_PERF=0）";
	}

	public void Render(in PerfSnapshot snap, float liveFps)
	{
		if (snap.UnixMs == 0 && liveFps <= 0) return;
		var fps = liveFps > 0.01f ? liveFps : snap.Fps;
		_fps.Text = $"FPS  {fps:0.0}";
		_frameMs.Text = $"帧  {snap.FrameMs:0.0} ms  逻辑 {snap.ProcessMs:0.0}  物理 {snap.PhysicsMs:0.0}";
		_cpu.Text = $"CPU  {snap.CpuPercent:0.0}%";
		_ram.Text = $"内存  {PerfSnapshotJson.FormatBytes(snap.WorkingSetBytes)}  托管 {PerfSnapshotJson.FormatBytes(snap.ManagedBytes)}";
		_vram.Text = $"显存  {PerfSnapshotJson.FormatBytes(snap.VideoMemBytes)}  贴图 {PerfSnapshotJson.FormatBytes(snap.TextureMemBytes)}";
		_draw.Text = $"绘制  {snap.DrawCalls} calls  {snap.Primitives} tris  节点 {snap.NodeCount}";
	}

	public bool Hits(Vector2 screen) =>
		Visible && _box is { } frame && frame.GetGlobalRect().HasPoint(screen);

	private static Label AddLine(VBoxContainer box, string text)
	{
		var label = new Label { Text = text };
		box.AddChild(label);
		return label;
	}
}
