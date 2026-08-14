using System.Security.Cryptography;
using System.Text;

namespace Starve.Protocol;

/// <summary>
/// 开发用 JWT 签发：与服务端 feeds/pkg/auth 的默认密钥（feeds-dev-secret）对应，
/// 用于本地联调（正式环境 token 由账号服务签发，不走这里）。
/// </summary>
public static class DevTokens
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("feeds-dev-secret");

    public static string Mint(string userId)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = Base64Url(Encoding.UTF8.GetBytes($"{{\"user_id\":\"{userId}\"}}"));
        var signingInput = $"{header}.{payload}";
        using var hmac = new HMACSHA256(Secret);
        var sig = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        return $"{signingInput}.{sig}";
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
