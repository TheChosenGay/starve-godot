using Starve.Protocol.World;

namespace Starve.Protocol;

/// <summary>
/// 协议层唯一入口：transport（WS/心跳）→ session（握手/鉴权）→ world（快照/增量）。
/// ConnectAsync 返回时保证：登录成功 + 全量快照已应用（World 已就绪）。
/// </summary>
public sealed class StarveClient : IDisposable
{
    public Transport Transport { get; }
    public Session Session { get; }
    public CommandService Commands { get; }
    public WorldService World { get; }
    public SessionInfo? Info { get; private set; }

    public StarveClient()
    {
        Transport = new Transport();
        Session = new Session(Transport);
        Commands = new CommandService(Session);
        World = new WorldService();
        World.InputAcknowledged += Commands.Acknowledge;
        Session.OnPush += World.HandleMessage;
    }

    public async Task<SessionInfo> ConnectAsync(string url, string token, CancellationToken ct = default)
    {
        await Transport.ConnectAsync(url, ct);
        if (!Transport.Capabilities.Contains("input_epoch_ack") ||
            !Transport.Capabilities.Contains("snapshot_tick") ||
            !Transport.Capabilities.Contains("effective_move_speed"))
        {
            throw new NotSupportedException(
                $"服务端协议能力不兼容: version={Transport.ProtocolVersion}");
        }
        var info = await Session.LoginAsync(token, ct);
        Commands.BeginInputEpoch(info.InputEpoch);
        Info = info;
        await World.WaitForSnapshotAsync(ct);
        return info;
    }

    public void Dispose() => Transport.Dispose();
}
