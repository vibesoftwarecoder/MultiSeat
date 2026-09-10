using MultiSeat.Shared;

namespace MultiSeat.Service.Streaming;

/// <summary>
/// Thread-safe port block allocator. Each seat gets a PortsPerSeat-wide block
/// covering Apollo's full port range (GFE HTTPS at base-5 through RTSP at base+21).
/// Always allocates the lowest available port first so seat 0 consistently gets 47984.
/// </summary>
public sealed class PortAllocator
{
    private readonly object _lock = new();
    private readonly SortedSet<int> _available = [];
    private readonly HashSet<int> _allocated = [];

    public PortAllocator()
    {
        for (int i = 0; i < Constants.MaxSeats; i++)
            _available.Add(Constants.PortBase + i * Constants.PortsPerSeat);
    }

    public int Allocate()
    {
        lock (_lock)
        {
            if (_available.Count == 0)
                throw new CapacityExhaustedException("No port blocks available. All seats occupied.");

            var port = _available.Min;
            _available.Remove(port);
            _allocated.Add(port);
            return port;
        }
    }

    public void Release(int portBase)
    {
        lock (_lock)
        {
            if (_allocated.Remove(portBase))
                _available.Add(portBase);
        }
    }

    // Named for what they are. The previous names were inherited from the aliases in Constants
    // and were inverted: "GetHttpsPort" returned the GFE HTTP port and "GetHttpPort" returned the
    // web UI, which is HTTPS.
    public int GetGfeHttpPort(int portBase) => portBase + Constants.OffsetGfeHttp;
    public int GetWebUiPort(int portBase) => portBase + Constants.OffsetWebUi;
    public int GetVideoPort(int portBase) => portBase + Constants.OffsetVideo;
    public int GetAudioPort(int portBase) => portBase + Constants.OffsetAudio;
    public int GetControlPort(int portBase) => portBase + Constants.OffsetControl;
}
