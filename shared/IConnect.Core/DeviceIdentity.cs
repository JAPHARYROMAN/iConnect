using System.Runtime.Versioning;
using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Protocol;

namespace IConnect.Core;

public sealed class DeviceIdentity(ECDsa key, string name) : IDisposable
{
    public byte[] PublicKey { get; } = key.ExportSubjectPublicKeyInfo();
    public string Id => ProtocolRules.DeviceId(PublicKey);
    public Identity Descriptor => new() { PublicKey = ByteString.CopyFrom(PublicKey), Name = name };
    public byte[] Sign(byte[] transcript) => key.SignData(transcript, HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence);
    public static DeviceIdentity Ephemeral(string name) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), name);

    [SupportedOSPlatform("windows")]
    public static DeviceIdentity OpenWindows(string keyName, string name)
    {
        using var storedKey = CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)
            ? CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)
            : CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing
            });
        return new(new ECDsaCng(storedKey), name);
    }

    public void Dispose() => key.Dispose();
}
