using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace IConnect.Tests;

public sealed class NativeDefaultTransportTests
{
    [Fact]
    public void Default_runtime_copies_the_corrected_binary_and_complete_source_bundle()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "PackageMetadata", "NativeTransport"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "bundle-evidence.json")));
        using var build = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "build-evidence.json")));
        var sourceLockPath = Path.Combine(root, "source-lock.json");
        using var sourceLock = JsonDocument.Parse(File.ReadAllText(sourceLockPath));
        Assert.Equal(Hash(sourceLockPath), manifest.RootElement.GetProperty("sourceLockSha256").GetString());
        var openssl = sourceLock.RootElement.GetProperty("openssl");
        Assert.Equal("openssl-3.5.9", openssl.GetProperty("tag").GetString());
        Assert.Equal("2030-04-08", openssl.GetProperty("supportUntil").GetString());
        var runtime = Path.Combine(AppContext.BaseDirectory, "libDatachannel", "datachannel.dll");
        var digest = Hash(runtime);
        Assert.Equal(digest, manifest.RootElement.GetProperty("binarySha256").GetString());
        Assert.Equal(digest, build.RootElement.GetProperty("binarySha256").GetString());
        Assert.Equal(new[] { "serialize-sctp-initialization-v1", "initialize-dtls-before-receive-v1" },
            build.RootElement.GetProperty("nativePatches").EnumerateArray().Select(patch => patch.GetProperty("id").GetString()).ToArray());
        var count = 0;
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = Path.GetFullPath(Path.Combine(root, file.Name));
            Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(file.Value.GetString(), Hash(path));
            count++;
        }
        Assert.True(count >= 15);
        var components = manifest.RootElement.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(7, components.Length);
        Assert.Equal(openssl.GetProperty("commit").GetString(),
            components.Single(component => component.GetProperty("name").GetString() == "openssl").GetProperty("commit").GetString());
        foreach (var component in components)
        {
            Check(component, "sourceArchive", "sourceArchiveSha256");
            Check(component, "rootLicense", "rootLicenseSha256");
        }

        void Check(JsonElement component, string pathProperty, string hashProperty)
        {
            var path = Path.GetFullPath(Path.Combine(root, component.GetProperty(pathProperty).GetString()!));
            Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(component.GetProperty(hashProperty).GetString(), Hash(path));
        }
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}
