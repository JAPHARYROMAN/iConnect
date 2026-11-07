using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using Xunit;

namespace IConnect.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void Signatures_bind_the_message_and_the_key()
    {
        using var alice = DeviceIdentity.Ephemeral("Alice");
        using var mallory = DeviceIdentity.Ephemeral("Mallory");
        var transcript = ProtocolRules.RegistrationTranscript(RandomNumberGenerator.GetBytes(32));
        var signature = alice.Sign(transcript);
        Assert.True(ProtocolRules.Verify(alice.PublicKey, transcript, signature));
        Assert.False(ProtocolRules.Verify(mallory.PublicKey, transcript, signature));
        transcript[^1] ^= 1;
        Assert.False(ProtocolRules.Verify(alice.PublicKey, transcript, signature));
    }

    [Fact]
    public void Pairing_approvals_bind_both_identities_and_the_transaction()
    {
        using var host = DeviceIdentity.Ephemeral("Host");
        using var guest = DeviceIdentity.Ephemeral("Guest");
        var pairing = new Pairing
        {
            TransactionId = ProtocolRules.NewToken(), Token = ProtocolRules.NewToken(), Host = host.Descriptor,
            Guest = guest.Descriptor, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds()
        };
        var signature = host.Sign(ProtocolRules.PairingTranscript(pairing));
        Assert.True(ProtocolRules.Verify(host.PublicKey, ProtocolRules.PairingTranscript(pairing), signature));
        pairing.Token = ProtocolRules.NewToken();
        Assert.False(ProtocolRules.Verify(host.PublicKey, ProtocolRules.PairingTranscript(pairing), signature));
    }

    [Theory]
    [InlineData("candidate:1 1 UDP 123 192.0.2.1 1234 typ relay")]
    [InlineData("candidate:1 1 UDP 123 192.0.2.1 1234 typ unknown")]
    [InlineData("not a candidate")]
    public void Non_direct_candidates_are_rejected(string candidate) =>
        Assert.Throws<InvalidDataException>(() => ProtocolRules.ValidateCandidate(candidate));

    [Fact]
    public void Sdp_requires_one_sha256_fingerprint_and_rejects_relay_candidates()
    {
        var fingerprint = string.Join(':', Enumerable.Repeat("ab", 32));
        var sdp = $"v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 {fingerprint}\r\n";
        Assert.Equal("sha-256 " + fingerprint, ProtocolRules.Fingerprint(sdp));
        Assert.Throws<InvalidDataException>(() => ProtocolRules.Fingerprint(sdp +
            "a=candidate:1 1 UDP 123 192.0.2.1 1234 typ relay\r\n"));
        Assert.Throws<InvalidDataException>(() => ProtocolRules.Fingerprint(sdp.Replace("sha-256", "sha-1")));
    }

    [Fact]
    public void Unsupported_protocol_versions_fail_closed()
    {
        var bytes = new Frame { Version = 2, Kind = "hello" }.ToByteArray();
        Assert.Throws<InvalidDataException>(() => ProtocolRules.Parse(bytes));
        Assert.Throws<InvalidDataException>(() => ProtocolRules.Parse(new byte[ProtocolRules.MaxFrameBytes + 1]));
    }

    [Fact]
    public void Receiver_requires_contiguous_data_and_a_matching_digest()
    {
        var data = RandomNumberGenerator.GetBytes(1024);
        var start = new TransferStart
        {
            TransferId = ProtocolRules.NewToken(), Name = "iconnect-test.bin", Size = data.Length,
            Sha256 = ByteString.CopyFrom(SHA256.HashData(data))
        };
        using var destination = new MemoryStream();
        using var receiver = new TestFileReceiver(start, destination);
        Assert.Throws<InvalidDataException>(() => receiver.Finish(start.TransferId));
        Assert.Throws<InvalidDataException>(() => receiver.Append(new TransferChunk
        { TransferId = start.TransferId, Offset = 1, Data = ByteString.CopyFrom(data) }));
        Assert.Equal(1024, receiver.Append(new TransferChunk
        { TransferId = start.TransferId, Offset = 0, Data = ByteString.CopyFrom(data) }));
        Assert.Equal(SHA256.HashData(data), receiver.Finish(start.TransferId));
        Assert.Equal(data, destination.ToArray());
        Assert.Throws<InvalidDataException>(() => receiver.Finish(start.TransferId));
    }

    [Fact]
    public void Receiver_never_reports_corruption_as_completed()
    {
        var start = new TransferStart
        {
            TransferId = ProtocolRules.NewToken(), Name = "iconnect-test.bin", Size = 1,
            Sha256 = ByteString.CopyFrom(new byte[32])
        };
        using var receiver = new TestFileReceiver(start, Stream.Null);
        receiver.Append(new TransferChunk { TransferId = start.TransferId, Data = ByteString.CopyFrom([42]) });
        Assert.Throws<InvalidDataException>(() => receiver.Finish(start.TransferId));
    }

    [Theory]
    [InlineData("../escape.bin", 10)]
    [InlineData("iconnect-test.bin", -1)]
    [InlineData("iconnect-test.bin", 1_048_577)]
    public void Receiver_rejects_arbitrary_paths_and_unbounded_sizes(string name, long size)
    {
        var start = new TransferStart
        { TransferId = ProtocolRules.NewToken(), Name = name, Size = size, Sha256 = ByteString.CopyFrom(new byte[32]) };
        Assert.Throws<InvalidDataException>(() => new TestFileReceiver(start, Stream.Null));
    }
}
