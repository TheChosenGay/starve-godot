using System.Collections.Concurrent;
using Google.Protobuf;
using Starve.Proto.V1;
using Starve.Protocol.Pomelo;

namespace Starve.Protocol;

public readonly record struct SessionInfo(string UserId, ulong EntityId);

/// <summary>会话：握手后的 mid 关联 + 推送/踢线分发 + 登录鉴权。</summary>
public sealed class Session
{
    private readonly Transport _transport;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> _pending = new();
    private int _mid;

    public event Action<PomeloMessage>? OnPush;

    public Session(Transport transport)
    {
        _transport = transport;
        transport.OnPacket += OnPacket;
    }

    private void OnPacket(PomeloPacket pkt)
    {
        if (pkt.Type != PacketType.Data) return;
        var msg = Codec.DecodeMessage(pkt.Data);
        if (msg.Type == MsgType.Response)
        {
            if (_pending.TryRemove(msg.Id, out var tcs))
                tcs.TrySetResult(msg.Data.Length > 0 ? msg.Data : null);
        }
        else if (msg.Type == MsgType.Push)
        {
            OnPush?.Invoke(msg);
        }
    }

    /// <summary>request/response：mid 自动分配，超时返回 null。</summary>
    public Task<byte[]?> RequestAsync(string route, byte[] data, int timeoutMs = 5000, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _mid);
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var timer = new CancellationTokenSource(timeoutMs);
        timer.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetResult(null);
        });
        _ = _transport.SendMessageAsync(MsgType.Request, id, route, data);
        return tcs.Task.WaitAsync(ct);
    }

    /// <summary>notify：fire-and-forget。</summary>
    public void Notify(string route, byte[] data) =>
        _ = _transport.SendMessageAsync(MsgType.Notify, 0, route, data);

    /// <summary>登录鉴权：gate.login request/response，返回会话信息。</summary>
    public async Task<SessionInfo> LoginAsync(string token, CancellationToken ct = default)
    {
        var req = new LoginRequest { Token = token };
        var respBytes = await RequestAsync(Routes.Login, req.ToByteArray(), 5000, ct);
        if (respBytes is null) throw new InvalidOperationException("登录超时");
        var resp = LoginResponse.Parser.ParseFrom(respBytes);
        if (!resp.Success) throw new InvalidOperationException($"登录失败: {resp.Message}");
        return new SessionInfo(resp.UserId, resp.EntityId);
    }
}
