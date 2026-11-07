using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf;
using IConnect.Protocol;

namespace IConnect.Core;

public static partial class ProtocolRules
{
    public const uint Version = 1;
    public const int MaxFrameBytes = 64 * 1024;
    public const int ChunkBytes = 16 * 1024;
    public const int MaxTestFileBytes = 1024 * 1024;

    public static Frame Pack(string kind, IMessage payload) => new()
    {
        Version = Version, Kind = kind, Payload = payload.ToByteString()
    };

    public static Frame Parse(byte[] bytes)
    {
        if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Message exceeds limit.");
        var frame = Frame.Parser.ParseFrom(bytes);
        if (frame.Version != Version) throw new InvalidDataException("Incompatible iConnect protocol version. Use matching releases.");
        if (frame.Kind.Length is < 1 or > 40) throw new InvalidDataException("Invalid protocol frame.");
        return frame;
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode64(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }

    public static string DeviceId(byte[] publicKey)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out var read);
        var parameters = key.ExportParameters(false);
        if (read != publicKey.Length || key.KeySize != 256 ||
            parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
            !publicKey.SequenceEqual(key.ExportSubjectPublicKeyInfo()))
            throw new CryptographicException("A canonical P-256 public key is required.");
        return Convert.ToHexStringLower(SHA256.HashData(publicKey));
    }

    public static bool Verify(byte[] publicKey, byte[] transcript, byte[] signature)
    {
        try
        {
            _ = DeviceId(publicKey);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(transcript, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
    }

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static byte[] RegistrationTranscript(byte[] nonce) => Transcript("register", Base64Url(nonce));
    public static byte[] PairingTranscript(Pairing pairing) => Transcript("pair-approve",
        pairing.TransactionId, pairing.Token, Base64Url(pairing.Host.PublicKey.ToByteArray()),
        Base64Url(pairing.Guest.PublicKey.ToByteArray()), pairing.ExpiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string VerificationCode(Pairing pairing)
    {
        var hash = SHA256.HashData(PairingTranscript(pairing));
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static byte[] SignalTranscript(Signal signal) => Transcript("signal", signal.SessionId,
        signal.FromDevice, signal.ToDevice, signal.Kind, Base64Url(Encoding.UTF8.GetBytes(signal.Body)),
        Base64Url(Encoding.UTF8.GetBytes(signal.Mid)), Base64Url(signal.Nonce.ToByteArray()),
        signal.CreatedAt.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static byte[] SessionTranscript(string session, string host, string guest,
        string offerFingerprint, string answerFingerprint, byte[] hostNonce, byte[] guestNonce) =>
        Transcript("session", session, host, guest, offerFingerprint, answerFingerprint,
            Base64Url(hostNonce), Base64Url(guestNonce));

    private static byte[] Transcript(string purpose, params string[] fields)
    {
        if (fields.Any(x => x.Contains('\n') || x.Contains('\r')))
            throw new InvalidDataException("Invalid transcript field.");
        return Encoding.UTF8.GetBytes($"iconnect/{purpose}/v1\n{string.Join('\n', fields)}");
    }

    public static void ValidateCandidate(string candidate)
    {
        if (candidate.Length > 2048 || candidate.Contains('\r') || candidate.Contains('\n') ||
            !CandidateType().IsMatch(candidate) || Regex.Matches(candidate, @"\btyp\s").Count != 1)
            throw new InvalidDataException("Only direct ICE candidates are allowed.");
    }

    public static string Fingerprint(string sdp)
    {
        if (sdp.Length > 24 * 1024 || sdp.Contains('\0')) throw new InvalidDataException("Invalid SDP.");
        var lines = sdp.Split('\n').Select(x => x.TrimEnd('\r')).ToArray();
        if (lines.Any(x => x.StartsWith("m=") && !x.StartsWith("m=application ")))
            throw new InvalidDataException("Only a data channel is supported.");
        foreach (var line in lines.Where(x => x.StartsWith("a=candidate:"))) ValidateCandidate(line[2..]);
        var fingerprints = lines.Where(x => x.StartsWith("a=fingerprint:"))
            .Select(x => x[14..].Trim().ToLowerInvariant()).Distinct().ToArray();
        if (fingerprints.Length != 1 || !FingerprintPattern().IsMatch(fingerprints[0]))
            throw new InvalidDataException("A single SHA-256 DTLS fingerprint is required.");
        return fingerprints[0];
    }

    public static void ValidateSignal(Signal signal, string sender, string recipient, byte[] publicKey,
        DateTimeOffset now)
    {
        if (signal.FromDevice != sender || signal.ToDevice != recipient ||
            !TokenPattern().IsMatch(signal.SessionId) || signal.Nonce.Length != 32 ||
            signal.Kind is not ("offer" or "answer" or "candidate") || signal.Mid.Length > 64 ||
            signal.CreatedAt < now.ToUnixTimeMilliseconds() - 60_000 ||
            signal.CreatedAt > now.ToUnixTimeMilliseconds() + 60_000 ||
            !Verify(publicKey, SignalTranscript(signal), signal.Signature.ToByteArray()))
            throw new InvalidDataException("Unauthenticated or expired connection message.");
        if (signal.Kind == "candidate") ValidateCandidate(signal.Body);
        else _ = Fingerprint(signal.Body);
    }

    [GeneratedRegex(@"^(?:candidate:)?\S+ \d+ (?:[Uu][Dd][Pp]|[Tt][Cc][Pp]) \d+ \S+ \d+ typ (host|srflx|prflx)(?:\s.*)?$")]
    private static partial Regex CandidateType();
    [GeneratedRegex(@"^sha-256 (?:[a-f0-9]{2}:){31}[a-f0-9]{2}$")]
    private static partial Regex FingerprintPattern();
    [GeneratedRegex(@"^[A-Za-z0-9_-]{43}$")]
    public static partial Regex TokenPattern();
}
