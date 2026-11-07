using IConnect.Core;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IConnect.Preview.Windows;

public sealed class WindowsPreviewRenderer : IPreviewRenderer
{
    private readonly PreviewSandbox sandbox;
    public WindowsPreviewRenderer() : this(Path.Combine(AppContext.BaseDirectory, "PreviewWorker", "IConnect.Preview.Worker.exe")) { }
    internal WindowsPreviewRenderer(string executable, TimeSpan? deadline = null) => sandbox = new(executable, deadline);
    public async Task<PreviewFrame> RenderAsync(PreviewInput input, int page, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (input.Kind == PreviewKind.Text || !input.EndOfFile || page is < 0 or >= PreviewRules.MaxPages) throw new InvalidDataException("Unsupported preview request.");
        PreviewRules.Probe(input.Kind, input.Bytes);
        var request = new byte[16 + input.Bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(request,0x31565049); BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4),(int)input.Kind);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8),page); BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(12),input.Bytes.Length); input.Bytes.CopyTo(request,16);
        byte[]? response=null;
        try
        {
            response=await sandbox.ExchangeAsync(request,PreviewRules.RenderSide*PreviewRules.RenderSide*4+24,cancellation);
            return Parse(response,page,input.Kind);
        }
        finally { CryptographicOperations.ZeroMemory(request); if(response is not null) CryptographicOperations.ZeroMemory(response); }
    }
    internal static PreviewFrame Parse(byte[] response, int page, PreviewKind kind)
    {
        using var reader = new BinaryReader(new MemoryStream(response));
        if (response.Length < 24 || reader.ReadInt32() != 0x31525049) throw new InvalidDataException("Invalid preview output.");
        var width = reader.ReadInt32(); var height = reader.ReadInt32(); var pages = reader.ReadInt32(); var actualPage = reader.ReadInt32(); var size = reader.ReadInt32();
        if (width is < 1 or > PreviewRules.RenderSide || height is < 1 or > PreviewRules.RenderSide || pages is < 1 or > PreviewRules.MaxPages || page != actualPage || page >= pages ||
            kind != PreviewKind.Pdf && (pages != 1 || page != 0) || size != (long)width * height * 4 || response.Length != 24 + size) throw new InvalidDataException("Invalid preview raster bounds.");
        return new(width, height, pages, page, reader.ReadBytes(size));
    }
}
