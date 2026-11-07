using System.Runtime.InteropServices;
using System.Security.Cryptography;
using IConnect.Attachment.Worker;
using IConnect.Core;
using IConnect.Storage.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace IConnect.Tests;

public sealed class AttachmentPathBindingTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string Parent { get; }
        public const string Name = "Generated 文書 with original extension.txt";
        public string Path => System.IO.Path.Combine(Parent, Name);
        public Fixture()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(System.IO.Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
            Root = System.IO.Path.Combine(repo!.FullName, ".local", "attachment-path-tests", ProtocolRules.NewToken());
            Parent = System.IO.Path.Combine(Root, new string('a', 65), new string('b', 65), new string('c', 65));
            Directory.CreateDirectory(Parent); File.WriteAllText(Path, "Generated unexecuted attachment bytes.\n");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    [Fact]
    public async Task Real_attachment_checks_keep_long_nested_Unicode_names_and_origin_or_fail_closed_without_a_short_alias()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var root = SecureWindowsDirectory.OpenRoot(f.Parent);
        string path, identity;
        using (var file = root.OpenChild(Fixture.Name)) { path = file.Info.CanonicalPath; identity = file.Info.Identity; }
        Assert.True(path.Length >= 260);
        string? alias;
        using (var binding = AttachmentPathBinding.Open(path, identity)) alias = binding.TryShortParentPath();
        var bytes = File.ReadAllBytes(f.Path); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var verify = ReceivedFileSecurity.VerifyPublishedAsync(root, Fixture.Name, identity, bytes.Length, hash, new WindowsAttachmentChecks(), () => { }, default);
        if (alias is null) await Assert.ThrowsAsync<AttachmentCheckException>(() => verify);
        else
        {
            Assert.Equal(Fixture.Name, System.IO.Path.GetFileName(alias));
            Assert.StartsWith(path[..49], alias, StringComparison.OrdinalIgnoreCase); Assert.True(alias.Length < 260);
            await verify;
        }
        Assert.Equal(bytes, File.ReadAllBytes(f.Path)); Assert.Contains("ZoneId=4", File.ReadAllText(f.Path + ":Zone.Identifier"));
    }

    [Fact]
    public async Task An_unshortenable_leaf_keeps_its_untrusted_bytes_and_never_receives_attachment_approval()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var name = new string('x', 240) + ".txt";
        File.WriteAllText(System.IO.Path.Combine(f.Parent, name), "Kept for owner review.");
        using var root = SecureWindowsDirectory.OpenRoot(f.Parent); string path, identity;
        using (var file = root.OpenChild(name)) { path = file.Info.CanonicalPath; identity = file.Info.Identity; }
        using (var binding = AttachmentPathBinding.Open(path, identity)) Assert.Null(binding.TryShortParentPath());
        var bytes = File.ReadAllBytes(System.IO.Path.Combine(f.Parent, name));
        await Assert.ThrowsAsync<AttachmentCheckException>(() => ReceivedFileSecurity.VerifyPublishedAsync(root, name, identity, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), new WindowsAttachmentChecks(), () => { }, default));
        Assert.Equal(bytes, File.ReadAllBytes(System.IO.Path.Combine(f.Parent, name)));
        Assert.Contains("ZoneId=4", File.ReadAllText(System.IO.Path.Combine(f.Parent, name) + ":Zone.Identifier"));
    }

    [Fact]
    public void Binding_pins_long_parents_and_original_leaf_against_rename_and_short_name_reassignment()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); string path, identity;
        using (var root = SecureWindowsDirectory.OpenRoot(f.Parent))
        using (var file = root.OpenChild(Fixture.Name)) { path = file.Info.CanonicalPath; identity = file.Info.Identity; }
        using (var binding = AttachmentPathBinding.Open(path, identity))
        {
            var alias = binding.TryShortParentPath();
            if (alias is not null) Assert.Equal(File.ReadAllBytes(f.Path), File.ReadAllBytes(alias));
            foreach (var item in new[] { f.Root, f.Parent, f.Path })
            {
                using var deletion = OpenForDelete(item);
                Assert.True(deletion.IsInvalid); Assert.Equal(32, Marshal.GetLastWin32Error());
            }
            using var parentWriter = CreateFileW(f.Parent, 0x40000000, 7, 0, 3, 0x02000000, 0);
            Assert.True(parentWriter.IsInvalid); Assert.Equal(32, Marshal.GetLastWin32Error());
            using var attributesOnly = CreateFileW(f.Parent, 0x80, 7, 0, 3, 0x02000000, 0);
            Assert.False(attributesOnly.IsInvalid);
            Assert.False(SetFileShortNameW(attributesOnly, "DIFFER~1")); Assert.Equal(5, Marshal.GetLastWin32Error());
            Assert.Throws<IOException>(() => Directory.Move(f.Parent, f.Parent + "-moved"));
            Assert.Throws<IOException>(() => File.Move(f.Path, f.Path + ".moved"));
        }
        using var after = OpenForDelete(f.Parent); Assert.False(after.IsInvalid);
    }

    [Theory]
    [InlineData(0x10000u)]
    [InlineData(0x40000000u)]
    public async Task Wrong_identity_or_a_preexisting_parent_mutation_handle_refuses_the_actual_worker_before_approval(uint access)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); string path, identity;
        using (var root = SecureWindowsDirectory.OpenRoot(f.Parent))
        using (var file = root.OpenChild(Fixture.Name)) { path = file.Info.CanonicalPath; identity = file.Info.Identity; }
        var checks = new WindowsAttachmentChecks();
        await Assert.ThrowsAsync<AttachmentCheckException>(() => checks.CheckAsync(path, "00000000:0000000000000000", default));
        using (var deletion = CreateFileW(f.Parent, access, 7, 0, 3, 0x02000000, 0))
        {
            Assert.False(deletion.IsInvalid);
            await Assert.ThrowsAsync<AttachmentCheckException>(() => checks.CheckAsync(path, identity, default));
        }
        Assert.False(File.Exists(f.Path + ":Zone.Identifier"));
        Assert.Equal("Generated unexecuted attachment bytes.\n", File.ReadAllText(f.Path));
    }

    private static SafeFileHandle OpenForDelete(string path) => CreateFileW(path, 0x10000, 7, 0, 3, 0x02000000 | 0x00200000, 0);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileShortNameW(SafeFileHandle file, string name);
}
