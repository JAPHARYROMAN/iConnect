using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Protocol;

namespace IConnect.Core;

public static class PairingTrust
{
    public static void ValidateContext(Pairing pairing, byte[] localKey, string token, byte[] expectedHost,
        bool isHost, DateTimeOffset now)
    {
        if (pairing.Host is null || pairing.Guest is null || pairing.Token != token ||
            !ProtocolRules.TokenPattern().IsMatch(pairing.Token) ||
            !ProtocolRules.TokenPattern().IsMatch(pairing.TransactionId) ||
            !pairing.Host.PublicKey.Span.SequenceEqual(expectedHost) ||
            !(isHost ? pairing.Host : pairing.Guest).PublicKey.Span.SequenceEqual(localKey) ||
            pairing.ExpiresAt <= now.ToUnixTimeMilliseconds() || pairing.ExpiresAt > now.AddMinutes(2).ToUnixTimeMilliseconds())
            throw new InvalidDataException("Pairing invitation or identity does not match.");
        foreach (var identity in new[] { pairing.Host, pairing.Guest })
        {
            _ = ProtocolRules.DeviceId(identity.PublicKey.ToByteArray());
            if (identity.Name.Length is < 1 or > 64 || identity.Name.Any(char.IsControl))
                throw new InvalidDataException("Invalid device name.");
        }
        if (pairing.Host.PublicKey.Equals(pairing.Guest.PublicKey))
            throw new InvalidDataException("A device cannot pair with itself.");
    }

    public static void ValidateCompletion(Pairing pending, Pairing completed, bool locallyApproved)
    {
        var transcript = ProtocolRules.PairingTranscript(pending);
        if (!locallyApproved || !transcript.SequenceEqual(ProtocolRules.PairingTranscript(completed)) ||
            completed.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ||
            !ProtocolRules.Verify(pending.Host.PublicKey.ToByteArray(), transcript, completed.HostApproval.ToByteArray()) ||
            !ProtocolRules.Verify(pending.Guest.PublicKey.ToByteArray(), transcript, completed.GuestApproval.ToByteArray()))
            throw new CryptographicException("Mutually approved pairing was not proven.");
    }
}
