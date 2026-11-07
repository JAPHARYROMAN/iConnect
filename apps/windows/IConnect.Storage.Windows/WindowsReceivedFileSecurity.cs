using IConnect.Core;
using Microsoft.Win32.SafeHandles;

namespace IConnect.Storage.Windows;

/// <summary>Origin handling for the bounded synthetic transport inbox, which has no user paths or resume protocol.</summary>
public static class WindowsReceivedFileSecurity
{
    public static string MarkPrivateStaging(SafeFileHandle handle)
    {
        var identity = SecureWindowsDirectory.Inspect(handle).Identity;
        SecureWindowsDirectory.MarkUntrusted(handle, identity); return identity;
    }
    public static async Task FinishSyntheticAsync(string inbox, string id, string identity, long size, string hash, CancellationToken cancellation)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(id) || size is < 0 or > ProtocolRules.MaxTestFileBytes || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new InvalidDataException("Invalid synthetic inbox binding.");
        using var root = SecureWindowsDirectory.OpenRoot(inbox);
        using (var part = root.OpenMutableChild(id + ".part"))
        {
            if (part.Info.Identity != identity || part.Info.Size != size) throw new IOException("The synthetic inbox partial changed.");
            part.MarkUntrusted(); part.RenameTo(root, id + ".bin");
        }
        await ReceivedFileSecurity.VerifyPublishedAsync(root, id + ".bin", identity, size, hash, new WindowsAttachmentChecks(), () => { }, cancellation);
    }
}
