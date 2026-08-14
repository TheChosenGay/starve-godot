using System.Net.WebSockets;
using System.Text.Json;
using Starve.Protocol.Pomelo;

namespace Starve.Protocol;

/// <summary>
/// WS 传输层：连接 + pomelo packet 编解码 + 心跳。
/// 事件在接收线程触发；上层通过 TCS/Concurrent 结构保证线程安全。
/// </summary>
public sealed class Transport : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly PacketBuffer _buffer = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Timer? _heartbeat;

    public event Action<PomeloPacket>? OnPacket;
    public event Action<string>? OnKick;

    /// <summary>连接 + 握手：发握手包，等服务端握手响应并回 ack 后返回。</summary>
    public async Task ConnectAsync(string url, CancellationToken ct = default)
    {
        await _ws.ConnectAsync(new Uri(url), ct);
        var handshakeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = linked;
        _receiveTask = Task.Run(() => ReceiveLoopAsync(linked.Token, handshakeTcs), CancellationToken.None);

        await SendPacketAsync(PacketType.Handshake, JsonSerializer.SerializeToUtf8Bytes(new { version = "0.0.1" }), ct);
        await handshakeTcs.Task.WaitAsync(ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct, TaskCompletionSource handshakeTcs)
    {
        var buf = new byte[65536];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Binary) continue;

                var chunk = new byte[result.Count];
                Buffer.BlockCopy(buf, 0, chunk, 0, result.Count);
                _buffer.Push(chunk);

                foreach (var pkt in _buffer.Take())
                {
                    switch (pkt.Type)
                    {
                        case PacketType.Handshake:
                            await SendPacketAsync(PacketType.HandshakeAck, Array.Empty<byte>(), ct);
                            StartHeartbeat(ParseHeartbeat(pkt.Data));
                            handshakeTcs.TrySetResult();
                            break;
                        case PacketType.Heartbeat:
                            break; // 服务器回心跳，客户端无需处理
                        case PacketType.Kick:
                            OnKick?.Invoke(System.Text.Encoding.UTF8.GetString(pkt.Data));
                            break;
                        case PacketType.Data:
                            OnPacket?.Invoke(pkt);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static int ParseHeartbeat(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("sys", out var sys) &&
                   sys.TryGetProperty("heartbeat", out var hb)
                ? hb.GetInt32()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void StartHeartbeat(int ms)
    {
        _heartbeat?.Dispose();
        if (ms <= 0) return;
        _heartbeat = new Timer(
            _ => _ = SendPacketAsync(PacketType.Heartbeat, Array.Empty<byte>()),
            null,
            ms,
            ms);
    }

    public async Task SendPacketAsync(byte type, byte[] data, CancellationToken ct = default)
    {
        var frame = Codec.EncodePacket(type, data);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendMessageAsync(byte type, int id, string route, byte[] data, CancellationToken ct = default)
        => SendPacketAsync(PacketType.Data, Codec.EncodeMessage(type, id, route, data), ct);

    public void Dispose()
    {
        _cts?.Cancel();
        _heartbeat?.Dispose();
        _ws.Dispose();
    }
}
