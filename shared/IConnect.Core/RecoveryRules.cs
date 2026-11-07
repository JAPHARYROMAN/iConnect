using IConnect.Protocol;

namespace IConnect.Core;

public static class RecoveryRules
{
    public const string Capability = "recovery-v1";
    public static bool IsChange(FileOperation operation) => operation is FileOperation.Delete or FileOperation.Restore;
    public static bool IsRpc(FileOperation operation) => IsChange(operation) || operation is FileOperation.RecoveryContext or FileOperation.ListDeleted or FileOperation.RecoveryStatus;
}
