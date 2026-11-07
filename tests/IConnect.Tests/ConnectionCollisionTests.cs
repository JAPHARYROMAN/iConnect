using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace IConnect.Tests;

public sealed class ConnectionCollisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Simultaneous_signed_offers_choose_one_session_and_ignore_its_superseded_candidates(bool lowerFirst)
    {
        await using var server=new WebApplicationFactory<Program>().WithWebHostBuilder(builder=>builder.UseEnvironment("Development"));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var one=DeviceIdentity.Ephemeral("Collision one"); using var two=DeviceIdentity.Ephemeral("Collision two"); using var unrelated=DeviceIdentity.Ephemeral("Unrelated pairing");
        var ordered=new[] {one,two}.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray();
        var first=ordered[lowerFirst?0:1]; var second=ordered[lowerFirst?1:0];
        var directory=Path.Combine(Path.GetTempPath(),"iconnect-collision-"+ProtocolRules.NewToken()); var gate=new OfferGate();
        try
        {
            var firstPath=Path.Combine(directory,"first"); var secondPath=Path.Combine(directory,"second");
            using var firstTrust=new TrustedPeerStore(firstPath,first.Id); firstTrust.Add(new(second.Id,second.Descriptor.Name,Convert.ToBase64String(second.PublicKey)));
            firstTrust.Add(new(unrelated.Id,unrelated.Descriptor.Name,Convert.ToBase64String(unrelated.PublicKey)));
            using var secondTrust=new TrustedPeerStore(secondPath,second.Id); secondTrust.Add(new(first.Id,first.Descriptor.Name,Convert.ToBase64String(first.PublicKey)));
            var firstConnection=await Register(server,first,gate,timeout.Token); var secondConnection=await Register(server,second,gate,timeout.Token);
            var messages=new ConcurrentQueue<string>(); var firstReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var secondReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var firstClient=new ProofClient(first,firstConnection,new Uri("http://localhost"),firstPath,messages.Enqueue,trustStore:firstTrust);
            await using var secondClient=new ProofClient(second,secondConnection,new Uri("http://localhost"),secondPath,messages.Enqueue,trustStore:secondTrust);
            var phases = new ConcurrentQueue<ProofClient.PeerLinkState>();
            firstClient.SessionStateChanged += _ => throw new IOException("Generated observer failure");
            firstClient.SessionStateChanged += phases.Enqueue;
            firstClient.SessionStateChanged+=state=> {if(state.Authenticated) firstReady.TrySetResult();}; secondClient.SessionStateChanged+=state=> {if(state.Authenticated) secondReady.TrySetResult();};
            var firstLoop=firstClient.RunAsync(timeout.Token); var secondLoop=secondClient.RunAsync(timeout.Token);
            try
            {
                await firstClient.ConnectPeerAsync(second.Id,timeout.Token); await secondClient.ConnectPeerAsync(first.Id,timeout.Token);
                await Task.WhenAll(firstReady.Task,secondReady.Task).WaitAsync(timeout.Token);
                Assert.Contains(phases, state => state.Phase == ProofClient.PeerLinkPhase.Connecting && !state.Authenticated);
                Assert.Contains(phases, state => state.Phase == ProofClient.PeerLinkPhase.Authenticated && state.Authenticated);
                firstClient.Revoke(unrelated.Id); Assert.DoesNotContain(phases, state => state.PeerId == unrelated.Id);
                Assert.Equal(2,gate.Offers.Count); Assert.Single(messages,message=>message.StartsWith("Both devices connected at once.",StringComparison.Ordinal));
                // A fresh signed candidate from the discarded attempt cannot interrupt the surviving authenticated channel.
                var losing=ordered[1]; var connection=losing.Id==first.Id?firstConnection:secondConnection;
                var stale=new Signal {SessionId=gate.Offers[losing.Id],FromDevice=losing.Id,ToDevice=ordered[0].Id,Kind="candidate",
                    Body="candidate:fixture 1 UDP 123 192.0.2.1 9999 typ host",Mid="0",CreatedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Nonce=ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32))};
                stale.Signature=ByteString.CopyFrom(losing.Sign(ProtocolRules.SignalTranscript(stale))); await connection.SendAsync("signal",stale,timeout.Token);
                await firstClient.SendTestFileAsync(timeout.Token); await secondClient.SendTestFileAsync(timeout.Token);
                Assert.False(firstLoop.IsCompleted); Assert.False(secondLoop.IsCompleted);
                Assert.Single(Directory.GetFiles(Path.Combine(firstPath,"inbox"),"*.bin")); Assert.Single(Directory.GetFiles(Path.Combine(secondPath,"inbox"),"*.bin"));
            }
            finally { timeout.Cancel(); foreach(var loop in new[] {firstLoop,secondLoop}) try {await loop;} catch(OperationCanceledException) {} }
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
    private static async Task<SignalConnection> Register(WebApplicationFactory<Program> server,DeviceIdentity identity,OfferGate gate,CancellationToken cancellation)
    {
        var socket=await server.Server.CreateWebSocketClient().ConnectAsync(new Uri("wss://localhost/signal"),cancellation);
        var connection=new SignalConnection(new GatedSocket(socket,gate)); var challenge=await connection.ReceiveAsync(cancellation);
        var nonce=Challenge.Parser.ParseFrom(challenge!.Payload).Nonce;
        await connection.SendAsync("register",new Registration {Identity=identity.Descriptor,SignalStatus=true,Signature=ByteString.CopyFrom(identity.Sign(ProtocolRules.RegistrationTranscript(nonce.ToByteArray())))},cancellation);
        Assert.Equal("registered",(await connection.ReceiveAsync(cancellation))!.Kind); return connection;
    }
    private sealed class OfferGate
    {
        public ConcurrentDictionary<string,string> Offers {get;}=new();
        private readonly TaskCompletionSource both=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Pass(byte[] bytes,CancellationToken cancellation)
        {
            var frame=ProtocolRules.Parse(bytes); if(frame.Kind!="signal") return; var signal=Signal.Parser.ParseFrom(frame.Payload); if(signal.Kind!="offer") return;
            Offers[signal.FromDevice]=signal.SessionId; if(Offers.Count==2) both.TrySetResult(); await both.Task.WaitAsync(cancellation);
        }
    }
    private sealed class GatedSocket(WebSocket socket,OfferGate gate):WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus=>socket.CloseStatus;
        public override string? CloseStatusDescription=>socket.CloseStatusDescription;
        public override WebSocketState State=>socket.State;
        public override string? SubProtocol=>socket.SubProtocol;
        public override void Abort()=>socket.Abort();
        public override Task CloseAsync(WebSocketCloseStatus status,string? reason,CancellationToken cancellation)=>socket.CloseAsync(status,reason,cancellation);
        public override Task CloseOutputAsync(WebSocketCloseStatus status,string? reason,CancellationToken cancellation)=>socket.CloseOutputAsync(status,reason,cancellation);
        public override void Dispose()=>socket.Dispose();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken cancellation)=>socket.ReceiveAsync(buffer,cancellation);
        public override async Task SendAsync(ArraySegment<byte> bytes,WebSocketMessageType kind,bool end,CancellationToken cancellation)
        { if(kind==WebSocketMessageType.Binary && end) await gate.Pass(bytes.ToArray(),cancellation); await socket.SendAsync(bytes,kind,end,cancellation); }
    }
}
