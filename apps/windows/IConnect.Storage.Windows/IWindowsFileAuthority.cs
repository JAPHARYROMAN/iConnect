using IConnect.Core;

namespace IConnect.Storage.Windows;

// Kept internal: incoming network sessions are always constructed with StorageGrantStore.
internal interface IWindowsFileAuthority
{
    event Action? Changed;
    T WithAuthorization<T>(Func<T> operation);
    IReadOnlyList<StorageGrant> ForPeer(string peer);
    StorageGrant Get(string id, string peer);
    void ValidateLocation(StorageGrant grant, IReadOnlyList<string> segments);
}

// Recovery has an owner purpose even when it has no paired-device authority.
internal interface IWindowsRecoveryAuthority : IWindowsFileAuthority
{
    string OwnerScope { get; }
    TimeProvider Clock { get; }
    void RequirePublicLocation(string canonicalPath);
    void ValidateAccess(StorageGrant grant, IReadOnlyList<string> segments);
}
