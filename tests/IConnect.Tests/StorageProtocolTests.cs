using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using Xunit;

namespace IConnect.Tests;

public sealed class StorageProtocolTests
{
    [Theory]
    [InlineData("control")]
    [InlineData("time")]
    [InlineData("state")]
    [InlineData("restore")]
    [InlineData("count")]
    public void Recovery_responses_reject_malformed_remote_metadata(string attack)
    {
        var id = MutationRules.NewId();
        var item = new DeletedInfo { Id = id, Name = "saved.txt", DeletedAt = MutationRules.CreatedAt(id), State = DeletedState.DeletedStored, CanRestore = true };
        var response = new FileResponse { RequestId = ProtocolRules.NewToken() }; response.DeletedItems.Add(item); StorageRules.ValidateResponse(response);
        switch (attack)
        {
            case "control": item.Name = "bad\u001bname"; break;
            case "time": item.DeletedAt++; break;
            case "state": item.State = (DeletedState)99; break;
            case "restore": item.State = DeletedState.DeletedRestored; break;
            case "count": for (var n = 0; n < 32; n++) response.DeletedItems.Add(item.Clone()); break;
        }
        Assert.Throws<InvalidDataException>(() => StorageRules.ValidateResponse(response));
    }
    [Theory]
    [InlineData("control")]
    [InlineData("negative-size")]
    [InlineData("unknown-status")]
    [InlineData("oversized-content")]
    [InlineData("too-many-items")]
    [InlineData("invalid-root")]
    [InlineData("invalid-timestamp")]
    public void UntrustedFileResponsesCannotEscapeBoundsOrInjectDisplayControls(string attack)
    {
        var response = new FileResponse { RequestId = ProtocolRules.NewToken() };
        var entry = new FileEntry { Id = ProtocolRules.NewToken(), GrantId = ProtocolRules.NewToken(),
            Name = "report.txt", Version = new string('a', 64), Permissions = 3 };
        response.Entries.Add(entry);
        StorageRules.ValidateResponse(response);
        switch (attack)
        {
            case "control": entry.Name = "\u001b[2Jreport.txt"; break;
            case "negative-size": entry.Size = -1; break;
            case "unknown-status": response.Status = (FileStatus)99; break;
            case "oversized-content": response.Content = ByteString.CopyFrom(new byte[StorageRules.MaxReadRange + 1]); break;
            case "too-many-items": for (var i = 0; i < StorageRules.PageSize; i++) response.Entries.Add(entry.Clone()); break;
            case "invalid-root": entry.IsRoot = true; break;
            case "invalid-timestamp": entry.ModifiedAt = long.MaxValue; break;
        }
        Assert.Throws<InvalidDataException>(() => StorageRules.ValidateResponse(response));
    }
}
