using Starve.Protocol.Pomelo;

namespace Starve.Core.Tests;

public sealed class PomeloCodecTests
{
    [Fact]
    public void RequestMessageRoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 255 };

        var encoded = Codec.EncodeMessage(MsgType.Request, 300, "world.player.move", payload);
        var decoded = Codec.DecodeMessage(encoded);

        Assert.Equal(MsgType.Request, decoded.Type);
        Assert.Equal(300, decoded.Id);
        Assert.Equal("world.player.move", decoded.Route);
        Assert.Equal(payload, decoded.Data);
    }

    [Fact]
    public void ResponseDoesNotCarryRoute()
    {
        var encoded = Codec.EncodeMessage(MsgType.Response, 9, "ignored", new byte[] { 7 });

        var decoded = Codec.DecodeMessage(encoded);

        Assert.Equal(MsgType.Response, decoded.Type);
        Assert.Equal(9, decoded.Id);
        Assert.Empty(decoded.Route);
        Assert.Equal(new byte[] { 7 }, decoded.Data);
    }

    [Fact]
    public void PacketBufferHandlesFragmentedAndCombinedPackets()
    {
        var first = Codec.EncodePacket(PacketType.Heartbeat, Array.Empty<byte>());
        var second = Codec.EncodePacket(PacketType.Data, new byte[] { 4, 5, 6 });
        var buffer = new PacketBuffer();

        buffer.Push(first[..2]);
        Assert.Empty(buffer.Take());

        buffer.Push(first[2..].Concat(second).ToArray());
        var packets = buffer.Take();

        Assert.Equal(2, packets.Count);
        Assert.Equal(PacketType.Heartbeat, packets[0].Type);
        Assert.Empty(packets[0].Data);
        Assert.Equal(PacketType.Data, packets[1].Type);
        Assert.Equal(new byte[] { 4, 5, 6 }, packets[1].Data);
    }

    [Fact]
    public void DecodeRejectsTruncatedVarint()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Codec.DecodeMessage(new byte[] { (byte)(MsgType.Request << 1), 0x80 }));
    }
}
