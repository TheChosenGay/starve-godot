using Google.Protobuf;
using Starve.Proto.V1;

namespace Starve.Protocol;

/// <summary>
/// 用户操作 service：把玩家意图翻译成 pomelo 消息。
/// 纯协议层（无渲染依赖），渲染/输入层只调用这里。
/// </summary>
public sealed class CommandService
{
    private readonly Session _session;

    public CommandService(Session session) => _session = session;

    public void Move(int dx, int dy) =>
        Notify(Routes.Move, new PlayerMove { Dx = dx, Dy = dy });

    public void Gather(ulong target) =>
        Notify(Routes.Gather, new PlayerGather { TargetEntity = target });

    public void Attack(ulong target) =>
        Notify(Routes.Attack, new PlayerAttack { TargetEntity = target });

    public void Pickup(ulong lootEntity) =>
        Notify(Routes.Pickup, new PlayerPickup { LootEntity = lootEntity });

    public void Use(int kind) =>
        Notify(Routes.Use, new PlayerUse { Kind = kind });

    public void Equip(int kind) =>
        Notify(Routes.Equip, new PlayerEquip { Kind = kind });

    public void Chop(ulong target) =>
        Notify(Routes.Chop, new PlayerChop { TargetEntity = target });

    public void Mine(ulong target) =>
        Notify(Routes.Mine, new PlayerMine { TargetEntity = target });

    public void Drop(int kind, int count) =>
        Notify(Routes.Drop, new PlayerDrop { Kind = kind, Count = count });

    public void CancelCraft() =>
        Notify(Routes.CancelCraft, new PlayerCancelCraft());

    public void Split(int fromSlot, int count) =>
        Notify(Routes.Split, new PlayerSplit { FromSlot = fromSlot, Count = count });

    public void Place(ulong entity, int x, int y) =>
        Notify(Routes.Place, new Place { Entity = entity, X = x, Y = y });

    public void Demolish(ulong targetEntity) =>
        Notify(Routes.Demolish, new Demolish { TargetEntity = targetEntity });

    /// <summary>建造（request/response）：只创建未放置建筑，返回实体 id。</summary>
    public async Task<BuildResponse?> BuildAsync(int kind, CancellationToken ct = default)
    {
        var resp = await _session.RequestAsync(Routes.Build, new Build { Kind = kind }.ToByteArray(), 5000, ct);
        return resp is null ? null : BuildResponse.Parser.ParseFrom(resp);
    }

    /// <summary>可放置查询（request/response）。</summary>
    public async Task<BuildCheckResponse?> BuildCheckAsync(ulong entity, int x, int y, CancellationToken ct = default)
    {
        var resp = await _session.RequestAsync(
            Routes.BuildCheck,
            new BuildCheck { Entity = entity, X = x, Y = y }.ToByteArray(),
            2000,
            ct);
        return resp is null ? null : BuildCheckResponse.Parser.ParseFrom(resp);
    }

    private void Notify(string route, IMessage msg) =>
        _session.Notify(route, msg.ToByteArray());
}
