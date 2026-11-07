using System.Security.Cryptography;
using IConnect.Core;
using Xunit;

namespace IConnect.Tests;

public sealed class WindowsIdentityTests
{
    [Fact]
    public void The_same_Cng_profile_reopens_the_identity_without_exporting_the_private_key()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows identity test.");
        var keyName = "iConnect.Tests." + ProtocolRules.NewToken();
        try
        {
            using var first = DeviceIdentity.OpenWindows(keyName, "First label");
            using var second = DeviceIdentity.OpenWindows(keyName, "Second label");
            Assert.Equal(first.Id, second.Id);
            var transcript = ProtocolRules.RegistrationTranscript(RandomNumberGenerator.GetBytes(32));
            Assert.True(ProtocolRules.Verify(first.PublicKey, transcript, second.Sign(transcript)));
            using var stored = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
            Assert.Throws<CryptographicException>(() => stored.Export(CngKeyBlobFormat.Pkcs8PrivateBlob));
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
            {
                using var createdByTest = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                createdByTest.Delete();
            }
        }
    }
}
