using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class PreviewTests
{
    [Fact]
    public async Task Viewer_clear_cancels_inflight_decode_and_blocks_late_publication()
    {
        var bytes=Png(32,16); var entry=Entry(bytes,"fixture.png"); var started=new TaskCompletionSource<PreviewInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken rendering=default;
        var viewer=new PreviewSession(new FakeRenderer(async(input,_,token)=> { rendering=token; started.SetResult(input); await release.Task; return new(1,1,1,0,[0,0,255,255]); }));
        var open=viewer.OpenAsync(entry,(request,_)=>Task.FromResult(Response(entry,request,bytes)),()=>true);
        var decoded=await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(viewer.View.Busy);
        viewer.Clear(); Assert.True(rendering.IsCancellationRequested); release.SetResult(); await open;
        Assert.Equal(new PreviewView(),viewer.View); Assert.All(decoded.Bytes,value=>Assert.Equal((byte)0,value));
    }
    [Fact]
    public async Task Viewer_retains_only_one_pdf_and_clears_pages_when_its_view_ends()
    {
        var bytes="%PDF-1.4\ngenerated fixture"u8.ToArray(); var entry=Entry(bytes,"fixture.pdf"); PreviewInput? retained=null; var available=true;
        var viewer=new PreviewSession(new FakeRenderer((input,page,_)=> { retained=input; return Task.FromResult(new PreviewFrame(1,1,2,page,[0,0,255,255])); }));
        await viewer.OpenAsync(entry,(request,_)=>Task.FromResult(Response(entry,request,bytes)),()=>available);
        Assert.Equal(0,viewer.View.Frame!.Page); await viewer.PageAsync(1); Assert.Equal(1,viewer.View.Frame!.Page);
        available=false; await viewer.PageAsync(0); Assert.Equal(new PreviewView(),viewer.View); Assert.All(retained!.Bytes,value=>Assert.Equal((byte)0,value));
    }
    [Fact]
    public async Task Viewer_new_selection_cannot_be_overwritten_by_an_older_read()
    {
        var slow="old text"u8.ToArray(); var fast="new text"u8.ToArray(); var oldEntry=Entry(slow); var newEntry=Entry(fast); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var viewer=new PreviewSession();
        var old=viewer.OpenAsync(oldEntry,async(request,_)=> { started.TrySetResult(); await release.Task; return Response(oldEntry,request,slow); },()=>true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await viewer.OpenAsync(newEntry,(request,_)=>Task.FromResult(Response(newEntry,request,fast)),()=>true);
        release.SetResult(); await old; Assert.Equal("new text",viewer.View.Text); viewer.Clear();
    }
    private sealed class FakeRenderer(Func<PreviewInput,int,CancellationToken,Task<PreviewFrame>> render) : IPreviewRenderer
    { public Task<PreviewFrame> RenderAsync(PreviewInput input,int page,CancellationToken cancellation)=>render(input,page,cancellation); }
    private static FileEntry Entry(byte[] bytes,string name="fixture.txt") => new() { Id=ProtocolRules.NewToken(), GrantId=ProtocolRules.NewToken(), Name=name, Size=bytes.Length, Version=new string('a',64), Permissions=3 };
    private static FileResponse Response(FileEntry entry,FileRequest request,byte[] bytes)
    {
        var result=new FileResponse { RequestId=ProtocolRules.NewToken() }; result.Entries.Add(entry.Clone());
        if(request.Operation==FileOperation.ReadRange) {
            var count=(int)Math.Min(request.Length,bytes.Length-request.Offset); result.Content=ByteString.CopyFrom(bytes,(int)request.Offset,count); result.EndOfFile=request.Offset+count==bytes.Length;
        }
        return result;
    }
    private static byte[] Png(int width,int height)
    {
        var bytes=new byte[33]; new byte[] {137,80,78,71,13,10,26,10}.CopyTo(bytes,0); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8),13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12)); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16),width); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20),height); return bytes;
    }
    [Fact]
    public async Task Preview_reads_bounded_ranges_and_rechecks_the_original_metadata()
    {
        var bytes=Enumerable.Repeat((byte)'a',70_000).ToArray(); var entry=Entry(bytes); var requests=new List<FileRequest>();
        var preview=await PreviewRules.FetchTextAsync(entry,(request,_)=> { requests.Add(request); return Task.FromResult(Response(entry,request,bytes)); },default);
        Assert.Equal(StorageRules.MaxReadRange,preview.Bytes.Length); Assert.False(preview.EndOfFile); Assert.Equal(3,requests.Count);
        Assert.Equal(FileOperation.GetMetadata,requests[0].Operation); Assert.Equal(FileOperation.GetMetadata,requests[^1].Operation);
        Assert.All(requests.Where(x=>x.Operation==FileOperation.ReadRange),x=>Assert.InRange(x.Length,1u,(uint)StorageRules.MaxReadRange));
        Assert.Equal(new string('a',StorageRules.MaxReadRange),PreviewRules.Text(preview));
    }
    [Theory]
    [InlineData("version")]
    [InlineData("grant")]
    [InlineData("name")]
    [InlineData("permission")]
    [InlineData("eof")]
    [InlineData("empty-range")]
    [InlineData("late-change")]
    public async Task Untrusted_or_changed_range_metadata_never_produces_a_preview(string attack)
    {
        var bytes="Habari!"u8.ToArray(); var entry=Entry(bytes); var calls=0;
        await Assert.ThrowsAnyAsync<IOException>(()=>PreviewRules.FetchTextAsync(entry,(request,_)=> {
            var result=Response(entry,request,bytes); calls++;
            if(request.Operation==FileOperation.ReadRange) switch(attack) {
                case "version": result.Entries[0].Version=new string('b',64); break;
                case "grant": result.Entries[0].GrantId=ProtocolRules.NewToken(); break;
                case "name": result.Entries[0].Name="other.txt"; break;
                case "permission": result.Entries[0].Permissions=1; break;
                case "eof": result.EndOfFile=false; break;
                case "empty-range": result.Content=ByteString.Empty; break;
            }
            if(attack=="late-change" && calls==3) result.Entries[0].Version=new string('b',64);
            return Task.FromResult(result);
        },default));
    }
    [Theory]
    [InlineData(0,1)]
    [InlineData(8193,1)]
    [InlineData(8192,8192)]
    [InlineData(int.MaxValue,int.MaxValue)]
    public void Dimension_bombs_are_refused_before_native_decoding(int width,int height) => Assert.Throws<InvalidDataException>(()=>PreviewRules.Probe(PreviewKind.Png,Png(width,height)));
    [Fact]
    public async Task Image_limit_is_checked_before_contacting_the_source_and_cancellation_is_honored()
    {
        var entry=Entry([],"huge.pdf"); entry.Size=PreviewRules.MaxBytes+1;
        await Assert.ThrowsAsync<IOException>(()=>PreviewRules.FetchAsync(entry,(_,_)=>throw new Exception("Must not read."),default));
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>PreviewRules.FetchTextAsync(entry,(_,_)=>throw new Exception("Must not read."),cancelled.Token));
    }
    [Fact]
    public void Text_handles_incomplete_utf8_only_at_a_truncated_boundary_and_removes_control_codes()
    {
        var bytes=Encoding.UTF8.GetBytes("Habari\u001b!\n").Concat(new byte[] {0xe2,0x82}).ToArray();
        Assert.Equal("Habari!\n",PreviewRules.Text(new(PreviewKind.Text,"text",bytes,false)));
        Assert.Throws<DecoderFallbackException>(()=>PreviewRules.Text(new(PreviewKind.Text,"text",bytes,true)));
        Assert.Throws<InvalidDataException>(()=>PreviewRules.Text(new(PreviewKind.Text,"binary",[0],true)));
    }
    [Fact]
    public async Task Real_ntfs_source_remains_opaque_and_revocation_prevents_publication()
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory); while(repo is not null && !File.Exists(Path.Combine(repo.FullName,"Directory.Build.props"))) repo=repo.Parent;
        var root=Path.Combine(repo!.FullName,".local","preview-tests",ProtocolRules.NewToken()); var shared=Path.Combine(root,"shared"); Directory.CreateDirectory(shared);
        try {
            var bytes=Png(32,16); File.WriteAllBytes(Path.Combine(shared,"fixture.png"),bytes); var peer=new string('a',64); var grants=new StorageGrantStore(Path.Combine(root,"state")); grants.Add(peer,shared);
            using var files=new WindowsFileService(grants);
            async Task<FileResponse> Rpc(FileRequest request,CancellationToken ct) { request.RequestId=ProtocolRules.NewToken(); return await files.ExecuteAsync(peer,request,ct); }
            var folder=(await Rpc(new() { Operation=FileOperation.ListRoots },default)).Entries.Single();
            var entry=(await Rpc(new() { Operation=FileOperation.ListDirectory,NodeId=folder.Id },default)).Entries.Single();
            var input=await PreviewRules.FetchAsync(entry,Rpc,default); Assert.Equal(PreviewKind.Png,input.Kind); Assert.Equal(bytes,input.Bytes);
            await Assert.ThrowsAnyAsync<IOException>(()=>PreviewRules.FetchAsync(entry,async(request,ct)=> { var result=await Rpc(request,ct); if(request.Operation==FileOperation.ReadRange)grants.RevokePeer(peer); return result; },default));
        } finally { Directory.Delete(root,true); } // Newly allocated fixture subtree only.
    }
}
