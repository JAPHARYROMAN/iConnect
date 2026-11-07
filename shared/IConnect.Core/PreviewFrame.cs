namespace IConnect.Core;

/// <summary>Unencoded, premultiplied BGRA pixels only. Native file formats never reach a UI image decoder.</summary>
public sealed record PreviewFrame(int Width, int Height, int Pages, int Page, byte[] Bgra);
public interface IPreviewRenderer
{
    Task<PreviewFrame> RenderAsync(PreviewInput input, int page, CancellationToken cancellation);
}
