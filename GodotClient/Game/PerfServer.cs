using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>本机只读 HTTP：看实时指标、历史曲线和 jsonl 日志。</summary>
public sealed class PerfServer : IDisposable
{
    private readonly PerfMonitor _monitor;
    private readonly HttpListener _http = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public string Url { get; }

    public PerfServer(int preferredPort, PerfMonitor monitor)
    {
        _monitor = monitor;
        var port = Bind(preferredPort);
        Url = $"http://127.0.0.1:{port}/";
        _ = Task.Run(Loop);
    }

    private int Bind(int preferred)
    {
        for (var port = preferred; port < preferred + 12; port++)
        {
            try
            {
                _http.Prefixes.Clear();
                _http.Prefixes.Add($"http://127.0.0.1:{port}/");
                _http.Start();
                return port;
            }
            catch (HttpListenerException)
            {
                // 端口占用则顺延。
            }
        }

        throw new InvalidOperationException("无法绑定性能查看端口");
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested && _http.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _http.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch (Exception)
            {
                if (_cts.IsCancellationRequested) return;
                continue;
            }

            try
            {
                Handle(ctx);
            }
            catch (Exception ex)
            {
                try
                {
                    Write(ctx.Response, 500, "text/plain; charset=utf-8", ex.Message);
                }
                catch
                {
                    // 客户端已断开。
                }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path == "/" || path == "/index.html")
        {
            Write(ctx.Response, 200, "text/html; charset=utf-8", DashboardHtml);
            return;
        }

        if (path == "/api/live")
        {
            WriteJson(ctx.Response, new
            {
                url = Url,
                logPath = _monitor.LogPath,
                latest = _monitor.Latest,
            });
            return;
        }

        if (path == "/api/frametime")
        {
            // 帧节拍单独一个端点：抓"平均 FPS 正常但手感卡"这类问题。
            WriteJson(ctx.Response, _monitor.FrameTime);
            return;
        }

        if (path == "/api/history")
        {
            WriteJson(ctx.Response, _monitor.CopyHistory());
            return;
        }

        if (path == "/api/logs")
        {
            WriteJson(ctx.Response, Array.ConvertAll(_monitor.ListLogs(), f => new
            {
                name = Path.GetFileName(f),
                path = f,
                bytes = new FileInfo(f).Length,
            }));
            return;
        }

        var logMatch = Regex.Match(path, "^/api/logs/([A-Za-z0-9._-]+)$");
        if (logMatch.Success)
        {
            var name = logMatch.Groups[1].Value;
            var root = Path.GetFullPath(_monitor.LogDir);
            var full = Path.GetFullPath(Path.Combine(root, name));
            if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full))
            {
                Write(ctx.Response, 404, "text/plain; charset=utf-8", "not found");
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            ctx.Response.AddHeader("Content-Disposition", $"inline; filename=\"{name}\"");
            using var fs = File.OpenRead(full);
            fs.CopyTo(ctx.Response.OutputStream);
            ctx.Response.OutputStream.Close();
            return;
        }

        Write(ctx.Response, 404, "text/plain; charset=utf-8", "not found");
    }

    private static void WriteJson(HttpListenerResponse res, object body) =>
        Write(res, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(body, PerfSnapshotJson.Options));

    private static void Write(HttpListenerResponse res, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = type;
        res.ContentEncoding = Encoding.UTF8;
        res.ContentLength64 = bytes.Length;
        res.AddHeader("Cache-Control", "no-store");
        res.OutputStream.Write(bytes);
        res.OutputStream.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _http.Stop(); } catch { /* ignore */ }
        _http.Close();
        _cts.Dispose();
    }

    private const string DashboardHtml =
        """
        <!doctype html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8"/>
        <meta name="viewport" content="width=device-width,initial-scale=1"/>
        <title>Starve 性能日志</title>
        <style>
        :root { color-scheme: dark; }
        body { margin: 0; font: 14px/1.45 ui-sans-serif, system-ui, sans-serif; background: #101214; color: #f2eee6; }
        header { padding: 16px 20px; background: #16181c; border-bottom: 1px solid #2a2e34; }
        h1 { margin: 0 0 6px; font-size: 18px; }
        .sub { color: #a8a49c; font-size: 12px; word-break: break-all; }
        main { padding: 16px 20px 32px; max-width: 1100px; }
        .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 10px; }
        .card { background: #16181ce8; border: 1px solid #2a2e34; border-radius: 8px; padding: 10px 12px; }
        .k { color: #a8a49c; font-size: 11px; }
        .v { font-size: 22px; font-variant-numeric: tabular-nums; margin-top: 2px; }
        canvas { width: 100%; height: 140px; background: #14181e; border: 1px solid #2a2e34; border-radius: 8px; margin: 14px 0 6px; }
        a { color: #7ec8e8; }
        table { width: 100%; border-collapse: collapse; margin-top: 8px; }
        td, th { text-align: left; padding: 6px 8px; border-bottom: 1px solid #2a2e34; }
        </style>
        </head>
        <body>
        <header>
          <h1>Starve 运行时性能</h1>
          <div class="sub" id="meta">连接中…</div>
        </header>
        <main>
          <div class="grid" id="cards"></div>
          <canvas id="chart"></canvas>
          <div class="sub">曲线：FPS（亮） / CPU%（琥珀）。每秒一点，约 10 分钟窗口。</div>
          <h2 style="font-size:15px;margin:22px 0 0">落地日志</h2>
          <div class="sub">jsonl，一行一条采样，可用 Excel / jq / 本页下载。</div>
          <table><thead><tr><th>文件</th><th>大小</th></tr></thead><tbody id="logs"></tbody></table>
        </main>
        <script>
        const cards = document.getElementById('cards');
        const meta = document.getElementById('meta');
        const logs = document.getElementById('logs');
        const canvas = document.getElementById('chart');
        const ctx = canvas.getContext('2d');
        function mb(n){ return (n/1048576).toFixed(1)+' MB'; }
        function card(k,v){ return `<div class="card"><div class="k">${k}</div><div class="v">${v}</div></div>`; }
        function draw(hist){
          const dpr = window.devicePixelRatio||1;
          canvas.width = canvas.clientWidth*dpr;
          canvas.height = canvas.clientHeight*dpr;
          ctx.setTransform(dpr,0,0,dpr,0,0);
          const w = canvas.clientWidth, h = canvas.clientHeight;
          ctx.clearRect(0,0,w,h);
          if(!hist.length) return;
          const plot = (key, color, maxHint) => {
            const vals = hist.map(s => s[key]||0);
            const max = Math.max(maxHint, ...vals, 1);
            ctx.beginPath();
            ctx.strokeStyle = color;
            ctx.lineWidth = 1.5;
            vals.forEach((v,i) => {
              const x = vals.length===1 ? 0 : i/(vals.length-1)*w;
              const y = h - (v/max)* (h-8) - 4;
              i? ctx.lineTo(x,y) : ctx.moveTo(x,y);
            });
            ctx.stroke();
          };
          plot('fps', '#7ec8e8', 60);
          plot('cpuPercent', '#e8a033', 100);
        }
        async function tick(){
          try {
            const live = await (await fetch('/api/live')).json();
            const s = live.latest || {};
            meta.textContent = (live.url||'') + '  ·  ' + (live.logPath||'');
            cards.innerHTML = [
              card('FPS', (s.fps||0).toFixed(1)),
              card('帧时间', (s.frameMs||0).toFixed(1)+' ms'),
              card('进程帧', (s.processMs||0).toFixed(1)+' ms'),
              card('物理', (s.physicsMs||0).toFixed(1)+' ms'),
              card('CPU', (s.cpuPercent||0).toFixed(1)+' %'),
              card('工作集', mb(s.workingSetBytes||0)),
              card('托管堆', mb(s.managedBytes||0)),
              card('显存', mb(s.videoMemBytes||0)),
              card('贴图', mb(s.textureMemBytes||0)),
              card('DrawCall', s.drawCalls||0),
              card('图元', s.primitives||0),
              card('节点', s.nodeCount||0),
              // 帧节拍：平均 FPS 正常但这里差 = 手感卡（frame pacing）
              card('帧中位', (s.frameMedianMs||0).toFixed(1)+' ms'),
              card('帧 P95', (s.frameP95Ms||0).toFixed(1)+' ms'),
              card('帧最坏', (s.frameWorstMs||0).toFixed(1)+' ms'),
              card('尖峰占比', ((s.frameSpikeRatio||0)*100).toFixed(1)+' %'),
            ].join('');
            const hist = await (await fetch('/api/history')).json();
            draw(hist);
            const list = await (await fetch('/api/logs')).json();
            logs.innerHTML = list.map(f => `<tr><td><a href="/api/logs/${f.name}">${f.name}</a></td><td>${mb(f.bytes)}</td></tr>`).join('');
          } catch(e) { meta.textContent = '等待游戏采样…'; }
        }
        tick();
        setInterval(tick, 1000);
        </script>
        </body>
        </html>
        """;
}
