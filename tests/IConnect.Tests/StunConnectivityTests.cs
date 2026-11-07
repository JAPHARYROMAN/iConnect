using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IConnect.Connectivity;
using Xunit;

namespace IConnect.Tests;

public sealed class StunConnectivityTests
{
    [Fact]
    public async Task Configured_stun_reaches_a_local_binding_server_and_emits_a_reflexive_candidate()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)); var endpoint=(IPEndPoint)server.Client.LocalEndPoint!;
        var requests=0;
        var serve=Task.Run(async()=>
        {
            while(!timeout.IsCancellationRequested)
            {
                var packet=await server.ReceiveAsync(timeout.Token); var input=packet.Buffer;
                if(input.Length<20 || BinaryPrimitives.ReadUInt16BigEndian(input)!=1 || BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(4))!=0x2112a442) continue;
                Interlocked.Increment(ref requests); var response=new byte[32]; input.AsSpan(0,20).CopyTo(response); response[0]=1; response[1]=1;
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2),12); BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20),0x20); response[23]=8; response[25]=1;
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26),(ushort)(packet.RemoteEndPoint.Port^0x2112));
                var mapped=new byte[] {198,51,100,77}; for(var index=0;index<4;index++) response[28+index]=(byte)(mapped[index]^input[4+index]);
                await server.SendAsync(response,packet.RemoteEndPoint,timeout.Token);
            }
        },timeout.Token);
        try
        {
            using var rtc=new NativeRtc($"stun:127.0.0.1:{endpoint.Port}"); rtc.Offer(); var observed=false;
            await foreach(var signal in rtc.Signals.ReadAllAsync(timeout.Token))
                if(signal.Kind=="candidate" && signal.Body.Contains("198.51.100.77",StringComparison.Ordinal) && signal.Body.Contains("typ srflx",StringComparison.Ordinal)) { observed=true; break; }
            Assert.True(observed); Assert.True(Volatile.Read(ref requests)>0);
        }
        finally { timeout.Cancel(); try {await serve;} catch(OperationCanceledException) {} }
    }
}
