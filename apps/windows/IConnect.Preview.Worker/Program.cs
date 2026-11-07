using IConnect.Core;
using System.Runtime.InteropServices;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

// This program is launched only by the capability-free AppContainer/job broker.
// It accepts bytes over stdin and emits one bounded raw raster. No paths, URLs, scripts or fonts are supplied by the owner.
try
{
    if (!IsIsolated()) return 2;
    using var reader = new BinaryReader(Console.OpenStandardInput());
    if (reader.ReadInt32() != 0x31565049) return 2;
    var kind = (PreviewKind)reader.ReadInt32(); var index = reader.ReadInt32(); var size = reader.ReadInt32();
    if (kind is not (PreviewKind.Png or PreviewKind.Jpeg or PreviewKind.Pdf) || index is < 0 or >= PreviewRules.MaxPages || size is < 1 or > PreviewRules.MaxBytes) return 2;
    var bytes = reader.ReadBytes(size); if (bytes.Length != size || reader.BaseStream.ReadByte() != -1) return 2;
    var probe = PreviewRules.Probe(kind, bytes);
    using var source = new InMemoryRandomAccessStream();
    using (var data = new DataWriter(source.GetOutputStreamAt(0))) { data.WriteBytes(bytes); await data.StoreAsync(); }
    source.Seek(0); var pages = 1;
    using var raster = new InMemoryRandomAccessStream();
    var imageSource = (IRandomAccessStream)source;
    if (kind == PreviewKind.Pdf)
    {
        var pdf = await PdfDocument.LoadFromStreamAsync(source);
        if (pdf.PageCount is < 1 or > PreviewRules.MaxPages || index >= pdf.PageCount) return 2;
        pages = (int)pdf.PageCount; using var page = pdf.GetPage((uint)index);
        var w = page.Size.Width; var h = page.Size.Height;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w is < 1 or > 16384 || h is < 1 or > 16384) return 2;
        var factor = PreviewRules.RenderSide / Math.Max(w, h);
        await page.RenderToStreamAsync(raster, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, Math.Round(w * factor)), DestinationHeight = (uint)Math.Max(1, Math.Round(h * factor)) });
        raster.Seek(0); imageSource = raster;
    }
    else if (index != 0) return 2;
    var decoder = await BitmapDecoder.CreateAsync(imageSource);
    if (kind != PreviewKind.Pdf && (decoder.PixelWidth != probe!.Width || decoder.PixelHeight != probe.Height)) return 2;
    var scale = Math.Min(1, (double)PreviewRules.RenderSide / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
    var width = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)); var height = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
    if (width > PreviewRules.RenderSide || height > PreviewRules.RenderSide) return 2;
    var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
        new BitmapTransform { ScaledWidth = width, ScaledHeight = height }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
    if (pixels.Length != (long)width * height * 4) return 2;
    using var writer = new BinaryWriter(Console.OpenStandardOutput());
    writer.Write(0x31525049); writer.Write((int)width); writer.Write((int)height); writer.Write(pages); writer.Write(index); writer.Write(pixels.Length); writer.Write(pixels); writer.Flush();
    return 0;
}
catch (Exception error) { return error.HResult; }

static bool IsIsolated()
{
    if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 8, out var token)) return false;
    try { return Native.GetTokenInformation(token, 29, out var value, sizeof(int), out _) && value == 1; }
    finally { Native.CloseHandle(token); }
}
internal static class Native
{
    [DllImport("kernel32.dll")] internal static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll")] internal static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] internal static extern bool GetTokenInformation(nint token, int kind, out int value, int size, out int returned);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
}
