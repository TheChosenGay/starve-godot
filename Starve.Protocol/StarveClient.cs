using Starve.Protocol.World;

namespace Starve.Protocol;

/// <summary>
/// 协议层唯一入口：transport（WS/心跳）→ session（握手/鉴权）→ world（快照/增量）。
/// ConnectAsync 返回时保证：登录成功 + 全量快照已应用（World 已就绪）。
/// </summary>
public sealed class StarveClient : IDisposable
{
    private static readonly string[] RequiredCapabilities =
    [
        "input_epoch_ack",
        "snapshot_tick",
        "effective_move_speed",
        "action_state_snapshot",
        "action_outcome",
        "world_events",
        "sleep_action",
    ];

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
        var missingCapabilities = RequiredCapabilities
            .Where(capability => !Transport.Capabilities.Contains(capability))
            .ToArray();
        if (missingCapabilities.Length > 0)
        {
            throw new NotSupportedException(
                $"服务端协议能力不兼容: version={Transport.ProtocolVersion}, " +
                $"missing={string.Join(',', missingCapabilities)}");
        }
        var info = await Session.LoginAsync(token, ct);
        Commands.BeginInputEpoch(info.InputEpoch);
        Info = info;
        await World.WaitForSnapshotAsync(ct);
        return info;
    }

    public void Dispose() => Transport.Dispose();
}
