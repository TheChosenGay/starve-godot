using Google.Protobuf;
using Starve.Game.V1;
using Starve.Protocol.World;

namespace Starve.Core.Tests;

// 组件解析缓存：必须"同字节复用、换字节失效"。
// 前者是性能（避免每帧重新 ParseFrom 产生 GC 压力），
// 后者是正确性（缓存住旧值会让客户端永远看到过期的位置）。
public sealed class EntityViewCacheTests
{
    [Fact]
    public void SameBytesReturnSameInstance()
    {
        var view = new EntityView(1);
        var pos = new Position { X = 3, Y = 4 };
        view.Components["Position"] = pos.ToByteArray();

        var a = view.Get("Position", Position.Parser);
        var b = view.Get("Position", Position.Parser);

        Assert.Same(a, b);              // 复用同一实例 = 没有重复分配
        Assert.Equal(3, a!.X);
    }

    [Fact]
    public void NewBytesInvalidateCache()
    {
        var view = new EntityView(1);
        view.Components["Position"] = new Position { X = 1, Y = 1 }.ToByteArray();
        Assert.Equal(1, view.Get("Position", Position.Parser)!.X);

        // 服务端下发新组件：写入侧每次都是新数组（ToByteArray），引用变化即失效
        view.Components["Position"] = new Position { X = 9, Y = 9 }.ToByteArray();
        var updated = view.Get("Position", Position.Parser);
        Assert.Equal(9, updated!.X);
    }

    [Fact]
    public void MissingComponentReturnsNull()
    {
        var view = new EntityView(1);
        Assert.Null(view.Get("Position", Position.Parser));
    }

    [Fact]
    public void DifferentComponentsCachedIndependently()
    {
        var view = new EntityView(1);
        view.Components["Position"] = new Position { X = 5, Y = 6 }.ToByteArray();
        view.Components["Health"] = new Health { Cur = 7, Max = 10 }.ToByteArray();

        Assert.Equal(5, view.Get("Position", Position.Parser)!.X);
        Assert.Equal(7, view.Get("Health", Health.Parser)!.Cur);
        // 两个组件的缓存不应互相覆盖
        Assert.Equal(5, view.Get("Position", Position.Parser)!.X);
    }
}
