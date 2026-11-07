using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace IConnect.AppModel;

/// <summary>Local current-user activation only. The protocol has no paths, settings, secrets or file operations.</summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopActivation : IAsyncDisposable
{
    private readonly NamedPipeServerStream server;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listening;
    private readonly SecurityIdentifier owner;
    public string PipeName { get; }
    public static string NameFor(string profile)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var key = identity.User?.Value + "|" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)).ToUpperInvariant();
        return "iConnect.Desktop." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
    public DesktopActivation(string profile)
    {
        PipeName = NameFor(profile);
        using var identity = WindowsIdentity.GetCurrent(); owner = identity.User ?? throw new IOException("The Windows user is unavailable.");
        var security = new PipeSecurity(); security.SetOwner(owner); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Explicit User SID avoids granting the token's Owner group (which can be Administrators).
        server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64, 64, security);
    }
    public void Start(Func<Task> open)
    { if (listening is not null) throw new InvalidOperationException("Activation is already running."); listening = Listen(open); }
    private async Task Listen(Func<Task> open)
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var message = new byte[5]; await server.ReadExactlyAsync(message, deadline.Token).ConfigureAwait(false);
                if (!message.AsSpan().SequenceEqual("OPEN\n"u8)) continue;
                SecurityIdentifier? caller = null;
                server.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(true); caller = identity?.User; });
                if (caller != owner) continue;
                await open().WaitAsync(deadline.Token).ConfigureAwait(false);
                await server.WriteAsync("OK\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                // Disconnect can discard unread pipe output. Wait for the client's read acknowledgement, with the same deadline.
                await server.ReadExactlyAsync(message, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException) { }
            finally { if (server.IsConnected) server.Disconnect(); }
        }
    }
    public static async Task<bool> TryOpenAsync(string profile, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", NameFor(profile), PipeDirection.InOut, PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(deadline.Token);
                using var identity = WindowsIdentity.GetCurrent();
                if (!Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)), identity.User)) return false;
                await pipe.WriteAsync("OPEN\n"u8.ToArray(), deadline.Token);
                var reply = new byte[3]; await pipe.ReadExactlyAsync(reply, deadline.Token);
                if (!reply.AsSpan().SequenceEqual("OK\n"u8)) return false;
                try { await pipe.WriteAsync("DONE\n"u8.ToArray(), deadline.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { /* The open was already acknowledged. */ }
                return true;
            }
            catch (IOException) { /* A prior client can still be disconnecting. OPEN is idempotent and carries no authority. */ }
            catch (Exception ex) when (ex is UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException) { return false; }
            try { await Task.Delay(50, deadline.Token); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (listening is not null) await listening;
        server.Dispose(); lifetime.Dispose();
    }
}
