using System.Diagnostics.Tracing;

namespace IConnect.Connectivity;

/// <summary>Opt-in process-local transport lifecycle tracing. No session, signalling or file payloads.</summary>
[EventSource(Name = "iConnect-NativeTransport")]
internal sealed class NativeTransportEvents : EventSource
{
    internal static readonly NativeTransportEvents Log = new();
    private NativeTransportEvents() { }

    [Event(1, Level = EventLevel.Informational)]
    public void Created(int peer) { if (IsEnabled()) WriteEvent(1, peer); }

    [Event(2, Level = EventLevel.Informational)]
    public void ChannelAttached(int peer, int channel) { if (IsEnabled()) WriteEvent(2, peer, channel); }

    [Event(3, Level = EventLevel.Informational)]
    public void ChannelOpened(int peer, int channel) { if (IsEnabled()) WriteEvent(3, peer, channel); }

    [Event(4, Level = EventLevel.Informational)]
    public void ConnectionState(int peer, int state, int channel) { if (IsEnabled()) WriteEvent(4, peer, state, channel); }

    [Event(5, Level = EventLevel.Informational)]
    public void IceState(int peer, int state) { if (IsEnabled()) WriteEvent(5, peer, state); }

    [Event(6, Level = EventLevel.Warning)]
    public void Failed(int peer, string errorType) { if (IsEnabled()) WriteEvent(6, peer, errorType); }

    [Event(7, Level = EventLevel.Informational)]
    public void Disposing(int peer, int channel) { if (IsEnabled()) WriteEvent(7, peer, channel); }
}
