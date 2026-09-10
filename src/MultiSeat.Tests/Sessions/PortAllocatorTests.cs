using MultiSeat.Service;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared;
using Xunit;

namespace MultiSeat.Tests.Sessions;

public class PortAllocatorTests
{
    [Fact]
    public void Allocate_ReturnsUniquePortBlocks()
    {
        var allocator = new PortAllocator();
        var ports = new HashSet<int>();

        for (int i = 0; i < Constants.MaxSeats; i++)
        {
            var port = allocator.Allocate();
            Assert.True(ports.Add(port), $"Duplicate port base: {port}");
        }
    }

    [Fact]
    public void Allocate_ThrowsWhenExhausted()
    {
        var allocator = new PortAllocator();

        for (int i = 0; i < Constants.MaxSeats; i++)
            allocator.Allocate();

        // Running out of port blocks is a CAPACITY condition, which the API answers with 503
        // rather than 400 — the request was fine, the host is full (#29 PR D).
        var ex = Assert.Throws<CapacityExhaustedException>(() => allocator.Allocate());

        // ⭐ And it is still an InvalidOperationException, which is what lets every non-HTTP
        // caller that already catches that keep working untouched. Assert.Throws demands an
        // exact type, so this second assertion is the one that pins the compatibility promise.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void Release_MakesPortAvailableAgain()
    {
        var allocator = new PortAllocator();
        var port = allocator.Allocate();
        allocator.Release(port);

        var port2 = allocator.Allocate();
        Assert.Equal(port, port2);
    }

    [Fact]
    public void PortOffsets_AreCorrect()
    {
        var allocator = new PortAllocator();
        var basePort = allocator.Allocate();

        Assert.Equal(basePort + Constants.OffsetGfeHttp, allocator.GetGfeHttpPort(basePort));
        Assert.Equal(basePort + Constants.OffsetWebUi, allocator.GetWebUiPort(basePort));
        Assert.Equal(basePort + Constants.OffsetVideo, allocator.GetVideoPort(basePort));
        Assert.Equal(basePort + Constants.OffsetAudio, allocator.GetAudioPort(basePort));
        Assert.Equal(basePort + Constants.OffsetControl, allocator.GetControlPort(basePort));
    }
}
