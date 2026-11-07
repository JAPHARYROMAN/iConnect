using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace IConnect.Tests;

public sealed class UploadBatchRulesTests
{
    private static UploadBatchManifest Plan(int count = 2)
    {
        var plan = new UploadBatchManifest { Collision = UploadCollision.UploadKeepBoth };
        plan.Items.Add(new UploadBatchItem { Parent = -1, Name = "folder", IsDirectory = true });
        for (var i = 1; i < count; i++) plan.Items.Add(new UploadBatchItem { Parent = 0, Name = $"Habari-文書-{i}.txt", Size = 6, Sha256 = ByteString.CopyFrom(SHA256.HashData("salama"u8)) });
        return plan;
    }
    private static UploadBatchHeader Header(UploadBatchManifest plan) => UploadBatchRules.Header(MutationRules.NewId(), ProtocolRules.NewToken(), ProtocolRules.NewToken(), plan);
    [Fact]
    public void File_receipts_bind_bytes_names_kind_and_publication_outcome_to_the_original_child()
    {
        var h = Header(Plan()); var peer = new string('a', 64); var descriptor = new UploadInfo { Id = UploadBatchRules.ChildId(peer, h, 1, false), Name = "file.bin", Size = 6, Sha256 = ByteString.CopyFrom(SHA256.HashData("salama"u8)), Collision = UploadCollision.UploadKeepBoth };
        var response = new FileResponse { RequestId = ProtocolRules.NewToken(), MutationEpoch = h.Epoch, Upload = descriptor.Clone(), BatchItem = new() { Header = h, Index = 1, Id = descriptor.Id } };
        Assert.Equal(descriptor, UploadBatchRules.CheckFileReceipt(peer, h, 1, descriptor, response));
        foreach (var change in new Action<FileResponse>[] { r => r.Upload.Name = "changed.bin", r => r.Upload.Size++, r => r.Upload.Sha256 = ByteString.CopyFrom(new byte[32]), r => r.Upload.Collision = UploadCollision.UploadSkip,
            r => r.BatchItem.IsDirectory = true, r => r.BatchItem.Phase = UploadBatchItemPhase.BatchItemFailed, r => r.Upload = null })
        { var changed = response.Clone(); change(changed); Assert.Throws<InvalidDataException>(() => UploadBatchRules.CheckFileReceipt(peer, h, 1, descriptor, changed)); }
        response.Upload.Offset = 6; response.Upload.Phase = UploadPhase.UploadCompleted; response.Upload.FinalName = "file (2).bin";
        response.BatchItem.Phase = UploadBatchItemPhase.BatchItemSucceeded; response.BatchItem.ResultingName = "file (2).bin";
        Assert.Equal(response.Upload, UploadBatchRules.CheckFileReceipt(peer, h, 1, descriptor, response));
        response.BatchItem.ResultingName = "other.bin"; Assert.Throws<InvalidDataException>(() => UploadBatchRules.CheckFileReceipt(peer, h, 1, descriptor, response));
    }
    [Fact]
    public void Reserved_child_ids_cannot_become_ordinary_operations_when_the_wall_clock_is_reset()
    {
        var header = Header(Plan()); var child = UploadBatchRules.ChildId(new string('a', 64), header, 0, true);
        foreach (var now in new long[] { 0, 1, 3_600_000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() })
            Assert.Equal(FileStatus.StaleReference, Assert.Throws<StorageException>(() => MutationRules.RequireCurrent(child, now)).Status);
    }
    [Fact]
    public void Both_platforms_use_the_independently_encoded_manifest_and_child_id_vector()
    {
        var vector = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "upload-batches.json")))!;
        var bytes = Convert.FromHexString(vector["encodedHex"]); var plan = UploadBatchRules.Decode(bytes);
        Assert.Equal(2, plan.Items.Count); Assert.Equal("folder", plan.Items[0].Name); Assert.Equal("Salama.txt", plan.Items[1].Name);
        Assert.Equal(vector["sourceHash"], Convert.ToHexStringLower(plan.Items[1].Sha256.Span));
        var header = UploadBatchRules.Header(vector["id"], vector["epoch"], vector["grant"], plan);
        Assert.Equal(vector["sha256"], Convert.ToHexStringLower(header.Sha256.Span)); Assert.Equal(bytes, UploadBatchRules.Encode(plan));
        Assert.Equal(vector["fileChildId"], UploadBatchRules.ChildId(vector["peer"], header, 1, false));
    }
    [Fact]
    public void Full_4096_item_plan_assembles_with_idempotent_bounded_chunks()
    {
        var plan = Plan(4096); var encoded = UploadBatchRules.Encode(plan); var header = Header(plan);
        using var assembly = new UploadBatchAssembly(header);
        Assert.Throws<InvalidDataException>(() => assembly.Complete());
        Assert.Throws<InvalidDataException>(() => assembly.Add(StorageRules.MaxReadRange, encoded.AsSpan(StorageRules.MaxReadRange, StorageRules.MaxReadRange)));
        for (var at = 0; at < encoded.Length; at += StorageRules.MaxReadRange)
        {
            var chunk = encoded.AsSpan(at, Math.Min(StorageRules.MaxReadRange, encoded.Length - at)).ToArray();
            Assert.Equal(at + chunk.Length, assembly.Add(at, chunk)); Assert.Equal(assembly.Offset, assembly.Add(at, chunk));
            var altered = chunk.ToArray(); altered[0] ^= 1;
            Assert.Throws<InvalidDataException>(() => assembly.Add(at, altered));
        }
        Assert.Equal(4096, assembly.Complete().Items.Count); Assert.Equal(4095 * 6L, header.TotalBytes);
        var returned = assembly.Complete(); returned.Items.Clear(); Assert.Equal(4096, assembly.Complete().Items.Count);
        assembly.Dispose(); Assert.Throws<ObjectDisposedException>(() => assembly.Complete());
    }
    [Theory]
    [InlineData("count")][InlineData("roots")][InlineData("depth")][InlineData("parent")][InlineData("file-parent")]
    [InlineData("duplicate")][InlineData("unicode-case")][InlineData("hash")][InlineData("directory-content")]
    [InlineData("overflow")][InlineData("surrogate")][InlineData("source-error-parent")][InlineData("source-error-hash")][InlineData("path")]
    [InlineData("traversal-name")][InlineData("slash")][InlineData("backslash")][InlineData("reserved")][InlineData("trailing-dot")]
    public void Malformed_or_ambiguous_plans_are_rejected(string attack)
    {
        var plan = Plan();
        switch (attack)
        {
            case "count": plan = Plan(4097); break;
            case "roots": plan.Items.Clear(); for (var i = 0; i < 129; i++) plan.Items.Add(new UploadBatchItem { Parent = -1, Name = "root" + i, IsDirectory = true }); break;
            case "depth": plan.Items.Clear(); for (var i = 0; i < 33; i++) plan.Items.Add(new UploadBatchItem { Parent = i - 1, Name = "level" + i, IsDirectory = true }); break;
            case "parent": plan.Items[1].Parent = 1; break;
            case "file-parent": plan.Items.Add(new UploadBatchItem { Parent = 1, Name = "child", IsDirectory = true }); break;
            case "duplicate": plan.Items.Add(plan.Items[1].Clone()); plan.Items[2].Name = plan.Items[1].Name.ToUpperInvariant(); break;
            case "unicode-case": plan.Items[1].Name = "\U00010428.txt"; plan.Items.Add(plan.Items[1].Clone()); plan.Items[2].Name = "\U00010400.txt"; break;
            case "hash": plan.Items[1].Sha256 = ByteString.Empty; break;
            case "directory-content": plan.Items[0].Size = 1; break;
            case "overflow": plan.Items[1].Size = StorageRules.MaxTransferSize; plan.Items.Add(plan.Items[1].Clone()); plan.Items[2].Name = "other.txt"; break;
            case "surrogate": plan.Items[1].Name = "bad\ud800.txt"; break;
            case "source-error-parent": plan.Items[0].Error = "Source unavailable"; break;
            case "source-error-hash": plan.Items[1].Error = "Source unavailable"; break;
            case "path": plan.Items.Clear(); for (var i = 0; i < 9; i++) plan.Items.Add(new UploadBatchItem { Parent = i - 1, Name = new string('a', 255), IsDirectory = true }); break;
            case "traversal-name": plan.Items[1].Name = ".."; break;
            case "slash": plan.Items[1].Name = "bad/name"; break;
            case "backslash": plan.Items[1].Name = "bad\\name"; break;
            case "reserved": plan.Items[1].Name = "NUL.txt"; break;
            case "trailing-dot": plan.Items[1].Name = "bad."; break;
        }
        Assert.Throws<InvalidDataException>(() => UploadBatchRules.Validate(plan));
    }
    [Fact]
    public void Exact_root_depth_and_large_file_bounds_preserve_explicit_source_issues()
    {
        var roots = new UploadBatchManifest(); for (var i = 0; i < 128; i++) roots.Items.Add(new UploadBatchItem { Parent = -1, Name = "root" + i, IsDirectory = true });
        Assert.Equal(0, UploadBatchRules.Validate(roots));
        var deep = new UploadBatchManifest(); for (var i = 0; i < 32; i++) deep.Items.Add(new UploadBatchItem { Parent = i - 1, Name = "level" + i, IsDirectory = true });
        Assert.Equal(0, UploadBatchRules.Validate(deep));
        var plan = Plan(); plan.Items[1].Size = StorageRules.MaxTransferSize;
        plan.Items.Add(new UploadBatchItem { Parent = 0, Name = "unavailable:name", Size = 99, Error = "Source cannot be copied." });
        Assert.Equal(StorageRules.MaxTransferSize, UploadBatchRules.Validate(plan));
        Assert.Equal(plan.Items.Count, UploadBatchRules.Decode(UploadBatchRules.Encode(plan)).Items.Count);
        plan.Items.Add(new UploadBatchItem { Parent = 0, Name = "straße.txt", IsDirectory = true }); plan.Items.Add(new UploadBatchItem { Parent = 0, Name = "STRASSE.txt", IsDirectory = true });
        Assert.Equal(StorageRules.MaxTransferSize, UploadBatchRules.Validate(plan));
    }
    [Fact]
    public void Encoding_limit_and_unknown_or_noncanonical_fields_are_not_silently_accepted()
    {
        var plan = Plan(4096);
        for (var i = 1; i < plan.Items.Count; i++) { plan.Items[i].Name = new string('文', 245) + i; plan.Items[i].Sha256 = ByteString.Empty; plan.Items[i].Error = new string('文', 512); }
        Assert.Throws<InvalidDataException>(() => UploadBatchRules.Validate(plan));
        var encoded = UploadBatchRules.Encode(Plan());
        Assert.Throws<InvalidDataException>(() => UploadBatchRules.Decode([.. encoded, 0x78, 0x01]));
        Assert.Throws<InvalidDataException>(() => UploadBatchRules.Decode([.. encoded, 0x10, 0x01])); // Repeated known scalar is also noncanonical.
        Assert.Throws<InvalidDataException>(() => UploadBatchRules.Decode(encoded.AsSpan(0, encoded.Length - 1)));
        Assert.Throws<InvalidDataException>(() => UploadBatchRules.Decode(new byte[UploadBatchRules.MaxEncodedBytes + 1]));
    }
    [Theory]
    [InlineData("hash")][InlineData("items")][InlineData("bytes")]
    public void Inconsistent_intent_cannot_finish_and_clears_assembly(string attack)
    {
        var plan = Plan(); var encoded = UploadBatchRules.Encode(plan); var header = Header(plan);
        if (attack == "hash") header.Sha256 = ByteString.CopyFrom(new byte[32]); else if (attack == "items") header.ItemCount++; else header.TotalBytes++;
        using var assembly = new UploadBatchAssembly(header); assembly.Add(0, encoded);
        Assert.Throws<InvalidDataException>(() => assembly.Complete()); Assert.Throws<ObjectDisposedException>(() => assembly.Complete());
    }
    [Fact]
    public void Child_operations_bind_the_parent_and_cannot_be_replayed_as_standalone_creation()
    {
        var header = Header(Plan()); var peer = new string('a', 64); var child = UploadBatchRules.ChildId(peer, header, 1, false);
        Assert.Equal(0, MutationRules.CreatedAt(child));
        Assert.Throws<StorageException>(() => MutationRules.RequireCurrent(child, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.NotEqual(child, UploadBatchRules.ChildId(peer, header, 0, false)); Assert.NotEqual(child, UploadBatchRules.ChildId(peer, header, 1, true));
        Assert.NotEqual(child, UploadBatchRules.ChildId(new string('b', 64), header, 1, false));
        foreach (var field in new[] { "id", "epoch", "grant", "manifest" })
        {
            var changed = header.Clone(); if (field == "id") changed.Id = MutationRules.NewId(); else if (field == "epoch") changed.Epoch = ProtocolRules.NewToken(); else if (field == "grant") changed.GrantId = ProtocolRules.NewToken(); else changed.Sha256 = ByteString.CopyFrom(new byte[32]);
            Assert.NotEqual(child, UploadBatchRules.ChildId(peer, changed, 1, false));
        }
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        header.GrantId = header.GrantId[..^1] + alphabet[alphabet.IndexOf(header.GrantId[^1]) ^ 1];
        Assert.False(UploadBatchRules.CanonicalToken(header.GrantId)); Assert.Throws<InvalidDataException>(() => new UploadBatchAssembly(header));
    }
    [Fact]
    public void Caller_mutation_and_invalid_chunks_cannot_change_the_captured_intent()
    {
        var plan = Plan(); var encoded = UploadBatchRules.Encode(plan); var header = Header(plan);
        using var assembly = new UploadBatchAssembly(header); header.TotalBytes = 0;
        Assert.Throws<InvalidDataException>(() => assembly.Add(0, [])); Assert.Throws<InvalidDataException>(() => assembly.Add(-1, encoded));
        Assert.Throws<InvalidDataException>(() => assembly.Add(1, encoded));
        assembly.Add(0, encoded); encoded[0] ^= 1; Assert.Equal(6, UploadBatchRules.Validate(assembly.Complete()));
    }
    [Fact]
    public void Unknown_intent_fields_are_rejected_before_allocating_assembly()
    {
        var encoded = Header(Plan()).ToByteArray(); var altered = UploadBatchHeader.Parser.ParseFrom([.. encoded, 0x78, 0x01]);
        Assert.Throws<InvalidDataException>(() => new UploadBatchAssembly(altered));
    }
    [Fact]
    public void Tiny_repeated_items_are_rejected_before_protobuf_object_allocation()
    {
        var attack = new byte[2_000_000]; for (var i = 0; i < attack.Length; i += 2) attack[i] = 10;
        Assert.Throws<InvalidDataException>(() => UploadBatchRules.Decode(attack));
        var before = GC.GetAllocatedBytesForCurrentThread(); Assert.Throws<InvalidDataException>(() => UploadBatchRules.Decode(attack));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 512 * 1024, "Invalid wire counts must be rejected before repeated message allocation.");
        foreach (var bytes in new byte[][] { [10, 255, 255, 255, 255, 127], [10, 2, 18, 127], [10, 2, 120, 1], Enumerable.Repeat((byte)0xff, 11).ToArray() })
            Assert.Throws<InvalidDataException>(() => UploadBatchRules.Decode(bytes));
    }
    [Fact]
    public void Admission_receipts_cannot_change_the_original_plan_or_return_impossible_progress()
    {
        var header = Header(Plan()); var receipt = new UploadBatchReceipt { Header = header, Phase = UploadBatchPlanPhase.UploadPlanReady, ManifestOffset = header.EncodedLength };
        var response = new FileResponse { RequestId = ProtocolRules.NewToken(), MutationEpoch = header.Epoch, Batch = receipt };
        Assert.Equal(receipt, UploadBatchRules.CheckReceipt(header, response));
        var variants = new List<FileResponse>();
        foreach (var field in new[] { "id", "epoch", "grant", "hash", "count", "bytes" })
        {
            var changed = response.Clone(); var h = changed.Batch.Header;
            if (field == "id") h.Id = MutationRules.NewId(); else if (field == "epoch") h.Epoch = ProtocolRules.NewToken(); else if (field == "grant") h.GrantId = ProtocolRules.NewToken();
            else if (field == "hash") h.Sha256 = ByteString.CopyFrom(new byte[32]); else if (field == "count") h.ItemCount++; else h.TotalBytes++;
            variants.Add(changed);
        }
        var epoch = response.Clone(); epoch.MutationEpoch = ProtocolRules.NewToken(); variants.Add(epoch);
        var incomplete = response.Clone(); incomplete.Batch.ManifestOffset = 0; variants.Add(incomplete);
        var canceled = response.Clone(); canceled.Batch.Phase = UploadBatchPlanPhase.UploadPlanCanceled; variants.Add(canceled);
        var unknown = response.Clone(); unknown.Batch.Phase = (UploadBatchPlanPhase)42; variants.Add(unknown);
        foreach (var changed in variants) Assert.Throws<InvalidDataException>(() => UploadBatchRules.CheckReceipt(header, changed));
    }
    [Fact]
    public void Batch_admission_is_independent_of_standalone_uploads_and_mutations()
    {
        Assert.Equal("upload-batches-v1", UploadBatchRules.Capability);
        for (var value = 31; value <= 44; value++)
        { var operation = (FileOperation)value; Assert.True(UploadBatchRules.IsRpc(operation)); Assert.False(UploadRules.IsRpc(operation)); Assert.False(MutationRules.IsMutationRpc(operation)); }
        Assert.False(UploadBatchRules.IsRpc(FileOperation.PrepareUpload)); Assert.False(UploadBatchRules.IsRpc((FileOperation)(-1)));
    }
    [Fact]
    public void Terminal_receipts_have_exact_bounded_outcomes_and_completed_plans_cannot_hide_unresolved_items()
    {
        var header = Header(Plan()); var receipt = new UploadBatchReceipt { Header = header, Phase = UploadBatchPlanPhase.UploadPlanCompleted,
            ManifestOffset = header.EncodedLength, EffectsStarted = true, ItemOutcomes = ByteString.CopyFrom(Enumerable.Repeat((byte)1, (int)header.ItemCount).ToArray()) };
        UploadBatchRules.ValidateReceipt(receipt); Assert.True(UploadBatchRules.IsTerminal(receipt.Phase)); Assert.False(UploadBatchRules.IsTerminal(UploadBatchPlanPhase.UploadPlanStopping));
        foreach (var invalid in new byte[] { 0, 4, 5, 255 }) { var changed = receipt.Clone(); var bytes = changed.ItemOutcomes.ToByteArray(); bytes[0] = invalid; changed.ItemOutcomes = ByteString.CopyFrom(bytes); Assert.Throws<InvalidDataException>(() => UploadBatchRules.ValidateReceipt(changed)); }
        var shortReceipt = receipt.Clone(); shortReceipt.ItemOutcomes = ByteString.Empty; Assert.Throws<InvalidDataException>(() => UploadBatchRules.ValidateReceipt(shortReceipt));
        var stopped = receipt.Clone(); stopped.Phase = UploadBatchPlanPhase.UploadPlanStopped; stopped.ItemOutcomes = ByteString.CopyFrom(new byte[header.ItemCount]); UploadBatchRules.ValidateReceipt(stopped);
        stopped.Phase = UploadBatchPlanPhase.UploadPlanStopping; Assert.Throws<InvalidDataException>(() => UploadBatchRules.ValidateReceipt(stopped));
    }
    [Fact]
    public void Scoped_directory_receipt_cannot_change_the_parent_index_kind_or_identity()
    {
        var h = Header(Plan()); var peer = new string('a', 64);
        var receipt = new UploadBatchItemReceipt { Header = h, Index = 0, Id = UploadBatchRules.ChildId(peer, h, 0, true), IsDirectory = true,
            Phase = UploadBatchItemPhase.BatchItemSucceeded, ResultingName = "folder" };
        var response = new FileResponse { RequestId = ProtocolRules.NewToken(), MutationEpoch = h.Epoch, BatchItem = receipt };
        Assert.Equal(receipt, UploadBatchRules.CheckItemReceipt(peer, h, 0, true, response));
        foreach (var field in new[] { "index", "kind", "id", "grant", "phase", "epoch" })
        {
            var changed = response.Clone(); var item = changed.BatchItem;
            if (field == "index") item.Index = 1; else if (field == "kind") item.IsDirectory = false; else if (field == "id") item.Id = MutationRules.NewId();
            else if (field == "grant") item.Header.GrantId = ProtocolRules.NewToken(); else if (field == "phase") item.Phase = (UploadBatchItemPhase)42; else changed.MutationEpoch = ProtocolRules.NewToken();
            Assert.Throws<InvalidDataException>(() => UploadBatchRules.CheckItemReceipt(peer, h, 0, true, changed));
        }
        Assert.Throws<InvalidDataException>(() => UploadBatchRules.CheckItemReceipt(new string('b', 64), h, 0, true, response));
    }
}
