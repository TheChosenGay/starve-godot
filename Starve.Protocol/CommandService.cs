using Google.Protobuf;
using Starve.Proto.V1;

namespace Starve.Protocol;

/// <summary>
/// 用户操作 service：把玩家意图翻译成 pomelo 消息。
/// 纯协议层（无渲染依赖），渲染/输入层只调用这里。
/// </summary>
public sealed class CommandService
{
    private static long _lastRequestId;

    private readonly ICommandSession _session;
    private readonly InputSequenceTracker _inputs = new();

    public CommandService(ICommandSession session) => _session = session;

    public ulong InputEpoch => _inputs.Epoch;
    public ulong LastSentSeq => _inputs.LastSent;
    public ulong LastAcceptedSeq => _inputs.LastAccepted;
    public ulong PendingControlCount => _inputs.Pending;
    [Obsolete("Use PendingControlCount; the sequence now covers all control commands.")]
    public ulong PendingMoveCount => PendingControlCount;
    public bool CanPredictMovement => _inputs.CanPredict;

    public void BeginInputEpoch(ulong epoch)
    {
        _inputs.Begin(epoch);
    }

    public void Acknowledge(ulong epoch, ulong seq, long _)
    {
        _inputs.Acknowledge(epoch, seq);
    }

    public InputCommandRef Move(int dx, int dy)
    {
        var command = NextControlCommand(withRequestId: false);
        Notify(Routes.Move, new PlayerMove
        {
            Dx = dx,
            Dy = dy,
            Seq = command.Seq,
            InputEpoch = command.InputEpoch,
        });
        return command;
    }

    public InputCommandRef Gather(ulong target)
    {
        var command = NextControlCommand();
        Notify(Routes.Gather, new PlayerGather
        {
            TargetEntity = target,
            Seq = command.Seq,
            InputEpoch = command.InputEpoch,
            RequestId = command.RequestId,
        });
        return command;
    }

    public InputCommandRef Attack(ulong target)
    {
        var command = NextControlCommand();
        Notify(Routes.Attack, new PlayerAttack
        {
            TargetEntity = target,
            Seq = command.Seq,
            InputEpoch = command.InputEpoch,
            RequestId = command.RequestId,
        });
        return command;
    }

    public void Pickup(ulong lootEntity) =>
        Notify(Routes.Pickup, new PlayerPickup { LootEntity = lootEntity });

    public void Use(int kind) =>
        Notify(Routes.Use, new PlayerUse { Kind = kind });

    public void Equip(int kind) =>
        Notify(Routes.Equip, new PlayerEquip { Kind = kind });

    public InputCommandRef Chop(ulong target)
    {
        var command = NextControlCommand();
        Notify(Routes.Chop, new PlayerChop
        {
            TargetEntity = target,
            Seq = command.Seq,
            InputEpoch = command.InputEpoch,
            RequestId = command.RequestId,
        });
        return command;
    }

    public InputCommandRef Mine(ulong target)
    {
        var command = NextControlCommand();
        Notify(Routes.Mine, new PlayerMine
        {
            TargetEntity = target,
            Seq = command.Seq,
            InputEpoch = command.InputEpoch,
            RequestId = command.RequestId,
        });
        return command;
    }

    /// <summary>空格自动行为：服务端在 AOI 内就近匹配目标执行（或寻路走过去）。</summary>
    public InputCommandRef Automate() => Automate(AutomateMode.Any);

    /// <summary>F 自动攻击：仅选择 AOI 内可攻击目标，超距时由服务端寻路。</summary>
    public InputCommandRef AttackNearest() => Automate(AutomateMode.AttackOnly);

    public void Drop(int kind, int count) =>
        Notify(Routes.Drop, new PlayerDrop { Kind = kind, Count = count });

    public InputCommandRef CancelCraft()
    {
        var command = NextControlCommand(withRequestId: false);
        Notify(Routes.CancelCraft, new PlayerCancelCraft
        {
            Seq = command.Seq,
            InputEpoch = command.InputEpoch,
        });
        return command;
    }

    /// <summary>提交制作并立即公开命令身份；响应任务不会阻塞本地动作预测。</summary>
    public CraftCommandSubmission BeginCraft(string recipeId, CancellationToken ct = default)
    {
        var command = NextControlCommand();
        var responseTask = RequestCraftAsync(
            new PlayerCraft
            {
                RecipeId = recipeId,
                Seq = command.Seq,
                InputEpoch = command.InputEpoch,
                RequestId = command.RequestId,
            },
            ct);
        return new CraftCommandSubmission(command, responseTask);
    }

    /// <summary>制作（request/response）：完整结果保留发令时的命令身份。</summary>
    public async Task<CraftCommandResult> CraftAsync(string recipeId, CancellationToken ct = default)
    {
        var submission = BeginCraft(recipeId, ct);
        return new CraftCommandResult(await submission.ResponseTask, submission.CommandRef);
    }

    private async Task<CraftResponse?> RequestCraftAsync(PlayerCraft request, CancellationToken ct)
    {
        var resp = await _session.RequestAsync(
            Routes.Craft,
            request.ToByteArray(),
            5000,
            ct);
        return resp is null ? null : CraftResponse.Parser.ParseFrom(resp);
    }

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

    private InputCommandRef Automate(AutomateMode mode)
    {
        var command = NextControlCommand();
        Notify(Routes.Automate, new PlayerAutomate
        {
            Mode = mode,
            Seq = command.Seq,
            InputEpoch = command.InputEpoch,
            RequestId = command.RequestId,
        });
        return command;
    }

    private InputCommandRef NextControlCommand(bool withRequestId = true) => new(
        InputEpoch,
        _inputs.Next(),
        withRequestId ? NextRequestId() : 0);

    private static ulong NextRequestId()
    {
        var next = Interlocked.Increment(ref _lastRequestId);
        if (next <= 0) throw new OverflowException("request_id exhausted");
        return checked((ulong)next);
    }
}
