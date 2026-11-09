using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using IConnect.Core;

namespace IConnect.Connectivity;

public sealed class NativeRtc : IDisposable
{
    public sealed record LocalSignal(string Kind, string Body, string Mid = "");
    private static readonly ConcurrentDictionary<int, NativeRtc> Peers = new();
    private static readonly ConcurrentDictionary<int, NativeRtc> Channels = new();
    private static readonly Native.DescriptionCallback DescriptionCallback = OnDescription;
    private static readonly Native.CandidateCallback CandidateCallback = OnCandidate;
    private static readonly Native.DataChannelCallback DataChannelCallback = OnDataChannel;
    private static readonly Native.OpenCallback OpenCallback = OnOpen;
    private static readonly Native.MessageCallback MessageCallback = OnMessage;
    private static readonly Native.StateCallback StateCallback = OnState;
    private static readonly Native.StateCallback IceStateCallback = OnIceState;
    private readonly Channel<byte[]> messages = Channel.CreateBounded<byte[]>(32);
    private readonly Channel<LocalSignal> signals = Channel.CreateBounded<LocalSignal>(128);
    private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object signalingGate = new();
    private readonly Queue<(string Body, string Mid)> earlyCandidates = new();
    private const int EarlyCandidateLimit = 128;
    private readonly int peer;
    private int channel = -1;
    private int disposed;
    private int failed;
    private int iceState = -1;
    private readonly ConcurrentQueue<int> connectionStates = new();
    public ChannelReader<byte[]> Messages => messages.Reader;
    public ChannelReader<LocalSignal> Signals => signals.Reader;
    public Task Opened => opened.Task;
    internal bool HasEnded => Volatile.Read(ref disposed) != 0 || Volatile.Read(ref failed) != 0;
    public string LocalFingerprint { get; private set; } = "";
    public string RemoteFingerprint { get; private set; } = "";

    static NativeRtc()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeRtc).Assembly, (name, _, _) =>
            name == Native.Library ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,
                "libDatachannel", "datachannel.dll")) : IntPtr.Zero);
    }

    public NativeRtc(string? stunServer = null, bool loopbackOnly = false)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The Phase 0 native host requires Windows x64.");
        var configuredStun=ConnectionRules.Stun(stunServer??"");
        var server = configuredStun.Length==0 ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(configuredStun);
        var serverList = server == IntPtr.Zero ? IntPtr.Zero : Marshal.AllocHGlobal(IntPtr.Size);
        var bindAddress = loopbackOnly ? Marshal.StringToCoTaskMemUTF8("127.0.0.1") : IntPtr.Zero;
        try
        {
            if (serverList != IntPtr.Zero) Marshal.WriteIntPtr(serverList, server);
            var config = new Native.Configuration
            {
                IceServers = serverList, IceServersCount = serverList == IntPtr.Zero ? 0 : 1,
                BindAddress = bindAddress, DisableAutoNegotiation = true, MaxMessageSize = ProtocolRules.MaxFrameBytes
            };
            peer = Check(Native.rtcCreatePeerConnection(ref config));
        }
        finally
        {
            if (server != IntPtr.Zero) Marshal.FreeCoTaskMem(server);
            if (serverList != IntPtr.Zero) Marshal.FreeHGlobal(serverList);
            if (bindAddress != IntPtr.Zero) Marshal.FreeCoTaskMem(bindAddress);
        }
        Peers[peer] = this;
        NativeTransportEvents.Log.Created(peer);
        Check(Native.rtcSetLocalDescriptionCallback(peer, DescriptionCallback));
        Check(Native.rtcSetLocalCandidateCallback(peer, CandidateCallback));
        Check(Native.rtcSetDataChannelCallback(peer, DataChannelCallback));
        Check(Native.rtcSetStateChangeCallback(peer, StateCallback));
        Check(Native.rtcSetIceStateChangeCallback(peer, IceStateCallback));
    }

    public void Offer()
    {
        Attach(Check(Native.rtcCreateDataChannel(peer, "iconnect-v1")));
        Check(Native.rtcSetLocalDescription(peer, "offer"));
    }

    public void SetDescription(string sdp, string type)
    {
        if (type is not ("offer" or "answer")) throw new InvalidDataException("Invalid SDP type.");
        RemoteFingerprint = ProtocolRules.Fingerprint(sdp);
        Check(Native.rtcSetRemoteDescription(peer, sdp, type));
        if (type == "offer") Check(Native.rtcSetLocalDescription(peer, "answer"));
    }

    public void AddCandidate(string candidate, string mid)
    {
        ProtocolRules.ValidateCandidate(candidate);
        Check(Native.rtcAddRemoteCandidate(peer, candidate, mid));
    }

    public bool TryApplyRemoteSignal(string kind, string body, string mid)
    {
        lock (signalingGate)
        {
            if (HasEnded) return false;
            try
            {
                if (kind == "candidate")
                {
                    if (RemoteFingerprint.Length != 0) AddCandidate(body, mid);
                    else
                    {
                        ProtocolRules.ValidateCandidate(body);
                        if (mid.Length > 64) throw new InvalidDataException("Invalid candidate media identifier.");
                        if (earlyCandidates.Count >= EarlyCandidateLimit) throw new IOException("Early candidate limit exceeded.");
                        earlyCandidates.Enqueue((body, mid));
                    }
                }
                else
                {
                    SetDescription(body, kind);
                    while (!HasEnded && earlyCandidates.TryDequeue(out var candidate))
                        AddCandidate(candidate.Body, candidate.Mid);
                }
                return true;
            }
            catch (IOException ex)
            {
                // A native negotiation failure ends this transport. The coordinator
                // still enforces signature, replay and session checks independently.
                Fail(ex);
                return false;
            }
        }
    }

    public void Send(byte[] data)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (data.Length > ProtocolRules.MaxFrameBytes || channel < 0) throw new InvalidDataException("Invalid channel message.");
        if (Check(Native.rtcGetBufferedAmount(channel)) > 1024 * 1024) throw new IOException("Peer send buffer limit exceeded.");
        Check(Native.rtcSendMessage(channel, data, data.Length));
    }

    public (string Local, string Remote) SelectedPair()
    {
        var local = new byte[2048];
        var remote = new byte[2048];
        Check(Native.rtcGetSelectedCandidatePair(peer, local, local.Length, remote, remote.Length));
        string Text(byte[] value) => Encoding.UTF8.GetString(value).TrimEnd('\0');
        ProtocolRules.ValidateCandidate(Text(local));
        ProtocolRules.ValidateCandidate(Text(remote));
        return (Text(local), Text(remote));
    }

    private void Attach(int id)
    {
        if (channel >= 0) throw new InvalidDataException("Unexpected extra data channel.");
        var label = new byte[128];
        Check(Native.rtcGetDataChannelLabel(id, label, label.Length));
        if (Encoding.UTF8.GetString(label).TrimEnd('\0') != "iconnect-v1")
            throw new InvalidDataException("Unexpected channel label.");
        channel = id;
        Channels[id] = this;
        NativeTransportEvents.Log.ChannelAttached(peer, id);
        Check(Native.rtcSetOpenCallback(id, OpenCallback));
        Check(Native.rtcSetMessageCallback(id, MessageCallback));
        if (Native.rtcIsOpen(id)) { NativeTransportEvents.Log.ChannelOpened(peer, id); opened.TrySetResult(); }
    }

    private void Fail(Exception ex)
    {
        if (Interlocked.Exchange(ref failed, 1) != 0) return;
        NativeTransportEvents.Log.Failed(peer, ex.GetType().Name);
        opened.TrySetException(ex);
        messages.Writer.TryComplete(ex);
        signals.Writer.TryComplete(ex);
    }

    // Never unwind a managed exception into the native callback stack.
    private static void OnDescription(int pc, string sdp, string type, IntPtr _)
    {
        if (!Peers.TryGetValue(pc, out var self)) return;
        try
        {
            self.LocalFingerprint = ProtocolRules.Fingerprint(sdp);
            if (!self.signals.Writer.TryWrite(new(type, sdp))) throw new IOException("Signalling queue exceeded.");
        }
        catch (Exception ex) { self.Fail(ex); }
    }
    private static void OnCandidate(int pc, string candidate, string mid, IntPtr _)
    {
        if (!Peers.TryGetValue(pc, out var self)) return;
        try
        {
            ProtocolRules.ValidateCandidate(candidate);
            if (!self.signals.Writer.TryWrite(new("candidate", candidate, mid))) throw new IOException("Candidate queue exceeded.");
        }
        catch (Exception ex) { self.Fail(ex); }
    }
    private static void OnDataChannel(int pc, int dc, IntPtr _)
    {
        if (!Peers.TryGetValue(pc, out var self)) return;
        try { self.Attach(dc); }
        catch (Exception ex) { self.Fail(ex); }
    }
    private static void OnOpen(int id, IntPtr _)
    { if (Channels.TryGetValue(id, out var self)) { NativeTransportEvents.Log.ChannelOpened(self.peer, id); self.opened.TrySetResult(); } }
    private static void OnState(int pc, int state, IntPtr _)
    {
        if (!Peers.TryGetValue(pc, out var self)) return;
        NativeTransportEvents.Log.ConnectionState(pc, state, Volatile.Read(ref self.channel));
        self.connectionStates.Enqueue(state);
        while (self.connectionStates.Count > 16) self.connectionStates.TryDequeue(out var ignored);
        if (state >= 3)
        {
            var failure = new IOException("Direct connection ended.");
            failure.Data["NativeConnectionState"] = state;
            failure.Data["NativeChannelAttached"] = Volatile.Read(ref self.channel) >= 0;
            failure.Data["NativeChannelOpened"] = self.opened.Task.IsCompletedSuccessfully;
            failure.Data["NativeIceState"] = Volatile.Read(ref self.iceState);
            failure.Data["NativePeerId"] = pc;
            failure.Data["NativeChannelId"] = Volatile.Read(ref self.channel);
            failure.Data["NativeConnectionHistory"] = string.Join(',', self.connectionStates.ToArray());
            self.Fail(failure);
        }
    }
    private static void OnIceState(int pc, int state, IntPtr _)
    { if (Peers.TryGetValue(pc, out var self)) { Volatile.Write(ref self.iceState, state); NativeTransportEvents.Log.IceState(pc, state); } }
    private static void OnMessage(int id, IntPtr data, int size, IntPtr _)
    {
        if (!Channels.TryGetValue(id, out var self)) return;
        try
        {
            if (size is <= 0 or > ProtocolRules.MaxFrameBytes) throw new InvalidDataException("Invalid binary channel frame.");
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, size);
            if (!self.messages.Writer.TryWrite(bytes)) throw new IOException("Receive queue exceeded.");
        }
        catch (Exception ex) { self.Fail(ex); }
    }

    public void Dispose()
    {
        lock (signalingGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            NativeTransportEvents.Log.Disposing(peer, Volatile.Read(ref channel));
            Peers.TryRemove(peer, out _);
            if (channel >= 0) { Channels.TryRemove(channel, out _); Native.rtcDeleteDataChannel(channel); }
            Native.rtcDeletePeerConnection(peer);
            earlyCandidates.Clear();
            messages.Writer.TryComplete();
            signals.Writer.TryComplete();
            opened.TrySetCanceled();
        }
    }

    private static int Check(int result) => result < 0 ? throw new IOException($"WebRTC operation failed ({result}).") : result;

    private static class Native
    {
        public const string Library = "iconnect-datachannel";
        [StructLayout(LayoutKind.Sequential)]
        public struct Configuration
        {
            public IntPtr IceServers;
            public int IceServersCount;
            public IntPtr ProxyServer;
            public IntPtr BindAddress;
            public int CertificateType;
            public int IceTransportPolicy;
            [MarshalAs(UnmanagedType.I1)] public bool EnableIceTcp;
            [MarshalAs(UnmanagedType.I1)] public bool EnableIceUdpMux;
            [MarshalAs(UnmanagedType.I1)] public bool DisableAutoNegotiation;
            [MarshalAs(UnmanagedType.I1)] public bool ForceMediaTransport;
            public ushort PortRangeBegin;
            public ushort PortRangeEnd;
            public int Mtu;
            public int MaxMessageSize;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DescriptionCallback(int pc, [MarshalAs(UnmanagedType.LPUTF8Str)] string sdp, [MarshalAs(UnmanagedType.LPUTF8Str)] string type, IntPtr ptr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void CandidateCallback(int pc, [MarshalAs(UnmanagedType.LPUTF8Str)] string candidate, [MarshalAs(UnmanagedType.LPUTF8Str)] string mid, IntPtr ptr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DataChannelCallback(int pc, int channel, IntPtr ptr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void OpenCallback(int channel, IntPtr ptr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void StateCallback(int pc, int state, IntPtr ptr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void MessageCallback(int channel, IntPtr data, int size, IntPtr ptr);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcCreatePeerConnection(ref Configuration configuration);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcDeletePeerConnection(int pc);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetLocalDescriptionCallback(int pc, DescriptionCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetLocalCandidateCallback(int pc, CandidateCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetStateChangeCallback(int pc, StateCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetIceStateChangeCallback(int pc, StateCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetDataChannelCallback(int pc, DataChannelCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetLocalDescription(int pc, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetRemoteDescription(int pc, [MarshalAs(UnmanagedType.LPUTF8Str)] string sdp, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcAddRemoteCandidate(int pc, [MarshalAs(UnmanagedType.LPUTF8Str)] string candidate, [MarshalAs(UnmanagedType.LPUTF8Str)] string mid);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcCreateDataChannel(int pc, [MarshalAs(UnmanagedType.LPUTF8Str)] string label);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcDeleteDataChannel(int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcGetDataChannelLabel(int id, byte[] buffer, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetOpenCallback(int id, OpenCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetMessageCallback(int id, MessageCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSendMessage(int id, byte[] buffer, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcGetBufferedAmount(int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcGetSelectedCandidatePair(int pc, byte[] local, int localSize, byte[] remote, int remoteSize);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool rtcIsOpen(int id);
    }
}
