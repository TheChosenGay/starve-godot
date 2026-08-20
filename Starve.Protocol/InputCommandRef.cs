using Starve.Proto.V1;

namespace Starve.Protocol;

/// <summary>一次 Control 输入的稳定身份；request_id=0 表示该命令没有动作请求身份。</summary>
public readonly record struct InputCommandRef(
    ulong InputEpoch,
    ulong Seq,
    ulong RequestId);

/// <summary>制作命令的即时提交结果；命令身份在网络响应完成前即可用于本地预测。</summary>
public readonly record struct CraftCommandSubmission(
    InputCommandRef CommandRef,
    Task<CraftResponse?> ResponseTask);

/// <summary>制作请求的完整结果，关联服务端响应与发令时的稳定身份。</summary>
public readonly record struct CraftCommandResult(
    CraftResponse? Response,
    InputCommandRef CommandRef);
