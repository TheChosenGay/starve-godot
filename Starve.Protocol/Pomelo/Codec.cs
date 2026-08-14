using System.Text;

namespace Starve.Protocol.Pomelo;

/// <summary>pomelo 协议编解码：packet 信封 + message 业务消息（移植自 TS 实现）。</summary>
public static class PacketType
{
    public const byte Handshake = 0x01;
    public const byte HandshakeAck = 0x02;
    public const byte Heartbeat = 0x03;
    public const byte Data = 0x04;
    public const byte Kick = 0x05;
}

public static class MsgType
{
    public const byte Request = 0x00;
    public const byte Notify = 0x01;
    public const byte Response = 0x02;
    public const byte Push = 0x03;
}

public readonly record struct PomeloPacket(byte Type, byte[] Data);

public sealed class PomeloMessage
{
    public byte Type { get; init; }
    public int Id { get; init; }
    public string Route { get; init; } = "";
    public byte[] Data { get; init; } = Array.Empty<byte>();
}

public static class Codec
{
    private static bool IsRoutable(byte type) =>
        type is MsgType.Request or MsgType.Notify or MsgType.Push;

    /// <summary>组包：1B type + 3B 大端长度 + body。</summary>
    public static byte[] EncodePacket(byte type, byte[] data)
    {
        var len = data.Length;
        var buf = new byte[4 + len];
        buf[0] = type;
        buf[1] = (byte)((len >> 16) & 0xff);
        buf[2] = (byte)((len >> 8) & 0xff);
        buf[3] = (byte)(len & 0xff);
        Buffer.BlockCopy(data, 0, buf, 4, len);
        return buf;
    }

    /// <summary>编码业务消息：flag(1B) [+ mid varint] [+ route(1B 长度 + 字符串)] + data。</summary>
    public static byte[] EncodeMessage(byte type, int id, string route, byte[] data)
    {
        var parts = new List<byte[]>();
        if (type is MsgType.Request or MsgType.Response) parts.Add(Varint(id));
        if (IsRoutable(type))
        {
            var rb = Encoding.UTF8.GetBytes(route);
            parts.Add(new[] { (byte)rb.Length });
            parts.Add(rb);
        }

        var total = 1 + data.Length;
        foreach (var p in parts) total += p.Length;

        var buf = new byte[total];
        buf[0] = (byte)(type << 1);
        var off = 1;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, buf, off, p.Length);
            off += p.Length;
        }
        Buffer.BlockCopy(data, 0, buf, off, data.Length);
        return buf;
    }

    /// <summary>解析业务消息。</summary>
    public static PomeloMessage DecodeMessage(byte[] bytes)
    {
        if (bytes.Length < 1) throw new InvalidOperationException("pomelo: message too short");
        var flag = bytes[0];
        var type = (byte)((flag >> 1) & 0x07);
        var off = 1;
        var id = 0;
        if (type is MsgType.Request or MsgType.Response) (id, off) = ReadVarint(bytes, off);
        var route = "";
        if (IsRoutable(type))
        {
            if (off >= bytes.Length) throw new InvalidOperationException("pomelo: message too short");
            var rl = bytes[off++];
            route = Encoding.UTF8.GetString(bytes, off, rl);
            off += rl;
        }

        var data = new byte[bytes.Length - off];
        Buffer.BlockCopy(bytes, off, data, 0, data.Length);
        return new PomeloMessage { Type = type, Id = id, Route = route, Data = data };
    }

    private static byte[] Varint(int n)
    {
        var list = new List<byte>();
        while (n >= 128)
        {
            list.Add((byte)((n % 128) | 0x80));
            n /= 128;
        }
        list.Add((byte)n);
        return list.ToArray();
    }

    private static (int Value, int Next) ReadVarint(byte[] bytes, int off)
    {
        long n = 0;
        var shift = 0;
        while (true)
        {
            if (off >= bytes.Length) throw new InvalidOperationException("pomelo: varint truncated");
            var b = bytes[off++];
            n += (long)(b & 0x7f) << shift;
            shift += 7;
            if (shift > 63) throw new InvalidOperationException("pomelo: varint too long");
            if ((b & 0x80) == 0) return ((int)n, off);
        }
    }
}

/// <summary>拆包缓冲：一个 WS 帧可能包含多个 pomelo 包，也可能跨帧。</summary>
public sealed class PacketBuffer
{
    private readonly List<byte> _buf = new();

    public void Push(byte[] bytes) => _buf.AddRange(bytes);

    public List<PomeloPacket> Take()
    {
        var outList = new List<PomeloPacket>();
        while (_buf.Count >= 4)
        {
            var size = (_buf[1] << 16) | (_buf[2] << 8) | _buf[3];
            if (size > 1 << 24) throw new InvalidOperationException("pomelo: packet too large");
            if (_buf.Count < 4 + size) break;
            var data = _buf.GetRange(4, size).ToArray();
            outList.Add(new PomeloPacket(_buf[0], data));
            _buf.RemoveRange(0, 4 + size);
        }
        return outList;
    }
}
