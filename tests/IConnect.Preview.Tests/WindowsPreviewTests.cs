using System.Text;
using IConnect.Core;
using IConnect.Preview.Windows;
using Xunit;

namespace IConnect.Preview.Tests;

public sealed class WindowsPreviewTests
{
    [Fact]
    public async Task Explicit_handle_list_excludes_an_inheritable_owner_file_handle()
    {
        var path=Path.Combine(Path.GetTempPath(),"iconnect-preview-handle-"+Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path,"private-inheritance-fixture-"+Guid.NewGuid().ToString("N"));
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            Assert.True(SetHandleInformation(file.SafeFileHandle,1,1));
            using var bytes=new MemoryStream(); using var writer=new BinaryWriter(bytes); writer.Write(6); writer.Write((long)file.SafeFileHandle.DangerousGetHandle());
            var response=await new PreviewSandbox(Fixture).ExchangeAsync(bytes.ToArray(),128,CancellationToken.None); Assert.Equal(new byte[] {0},response);
        }
        finally { File.Delete(path); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool SetHandleInformation(Microsoft.Win32.SafeHandles.SafeFileHandle handle,uint mask,uint flags);
    private static string Worker => Environment.GetEnvironmentVariable("ICONNECT_PREVIEW_TEST_WORKER") ?? Path.Combine(AppContext.BaseDirectory, "PreviewWorker", "IConnect.Preview.Worker.exe");
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixture", "PreviewWorker", "IConnect.Preview.Fixture.exe");
    [Fact]
    public async Task Offline_local_explorer_reads_real_files_and_closes_its_native_preview_on_navigation()
    {
        var folder=Path.Combine(Path.GetTempPath(),"iconnect-preview-local-"+Guid.NewGuid().ToString("N"));
        try
        {
            var documents=Path.Combine(folder,"documents"); var state=Path.Combine(folder,"state"); PreviewFixtures.Write(documents);
            await using var explorer=new IConnect.AppModel.LocalExplorerController(state,new WindowsPreviewRenderer(Worker));
            await explorer.AddAsync(documents); await explorer.BrowseAsync(explorer.Rows.Single().Entry);
            explorer.Select(explorer.Rows.Single(row=>row.Entry.Name=="fixture.png").Entry.Id); await explorer.InspectAsync(true);
            Assert.Empty(explorer.FilePreview.View.Error); Assert.NotNull(explorer.FilePreview.View.Frame);
            Assert.Equal(new byte[] {0,0,255,255},Center(explorer.FilePreview.View.Frame!));
            explorer.Select(explorer.Rows.Single(row=>row.Entry.Name=="fixture.pdf").Entry.Id); await explorer.InspectAsync(true);
            Assert.Empty(explorer.FilePreview.View.Error); Assert.NotNull(explorer.FilePreview.View.Frame);
            Assert.Equal(2,explorer.FilePreview.View.Frame.Pages); await explorer.FilePreview.PageAsync(1);
            Assert.Empty(explorer.FilePreview.View.Error); Assert.NotNull(explorer.FilePreview.View.Frame);
            Assert.Equal(new byte[] {255,0,0,255},Center(explorer.FilePreview.View.Frame!));
            await explorer.BrowseAsync(); Assert.Equal(new PreviewView(),explorer.FilePreview.View);
            Assert.False(File.Exists(Path.Combine(state,"trust.json"))); Assert.False(File.Exists(Path.Combine(state,"grants.json")));
        }
        finally { if(Directory.Exists(folder)) Directory.Delete(folder,true); }
    }
    [Fact]
    public async Task Native_jpeg_and_large_png_are_bounded_to_the_render_size()
    {
        foreach (var (kind,id,width,height) in new[] { (PreviewKind.Jpeg,global::Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId,32,16), (PreviewKind.Png,global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,3000,2000) })
        {
            using var stream=new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder=await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(id,stream); var pixels=new byte[width*height*4];
            for(var index=0;index<pixels.Length;index+=4) { pixels[index+2]=255; pixels[index+3]=255; }
            encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,(uint)width,(uint)height,96,96,pixels); await encoder.FlushAsync();
            using var reader=new global::Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size); var bytes=new byte[stream.Size]; reader.ReadBytes(bytes);
            var frame=await new WindowsPreviewRenderer(Worker).RenderAsync(new(kind,"generated",bytes,true),0,CancellationToken.None);
            Assert.InRange(frame.Width,1,PreviewRules.RenderSide); Assert.InRange(frame.Height,1,PreviewRules.RenderSide);
            var center=Center(frame); Assert.InRange(center[0],(byte)0,(byte)3); Assert.InRange(center[1],(byte)0,(byte)3); Assert.InRange(center[2],(byte)250,(byte)255);
            if(width>PreviewRules.RenderSide) Assert.Equal(PreviewRules.RenderSide,frame.Width);
        }
    }
    [Fact]
    public async Task Native_png_and_pdf_pixels_are_decoded_inside_the_sandbox()
    {
        var folder = Path.Combine(Path.GetTempPath(), "iconnect-preview-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            PreviewFixtures.Write(folder); var renderer = new WindowsPreviewRenderer(Worker);
            var png = await renderer.RenderAsync(new(PreviewKind.Png, "fixture.png", File.ReadAllBytes(Path.Combine(folder, "fixture.png")), true), 0, CancellationToken.None);
            Assert.Equal((32, 16, 1, 0), (png.Width, png.Height, png.Pages, png.Page)); Assert.Equal(new byte[] { 0, 0, 255, 255 }, png.Bgra[..4]);
            var pdf = new PreviewInput(PreviewKind.Pdf, "fixture.pdf", File.ReadAllBytes(Path.Combine(folder, "fixture.pdf")), true);
            var green = await renderer.RenderAsync(pdf, 0, CancellationToken.None); Assert.Equal(2, green.Pages); Assert.Equal(new byte[] { 0, 255, 0, 255 }, Center(green));
            var blue = await renderer.RenderAsync(pdf, 1, CancellationToken.None); Assert.Equal(new byte[] { 255, 0, 0, 255 }, Center(blue));
            await Assert.ThrowsAsync<InvalidDataException>(() => renderer.RenderAsync(pdf, 2, CancellationToken.None));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task Sandbox_denies_private_files_loopback_and_child_processes()
    {
        var secret = Path.Combine(Path.GetTempPath(), "iconnect-preview-secret-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(secret, "generated test secret");
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            using var data = new MemoryStream(); using var writer = new BinaryWriter(data); writer.Write(1); writer.Write(secret); writer.Write(((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
            var reply = await new PreviewSandbox(Fixture).ExchangeAsync(data.ToArray(), 128, CancellationToken.None);
            Assert.Equal("True,True,True", Encoding.ASCII.GetString(reply));
            Assert.False(listener.Pending());
            using var control = new System.Net.Sockets.TcpClient(); await control.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
            Assert.True(listener.Pending()); // The same listener is reachable outside the sandbox.
        }
        finally { File.Delete(secret); }
    }
    [Fact]
    public async Task Cancellation_and_deadline_terminate_a_blocked_process()
    {
        var pid = 0; using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PreviewSandbox(Fixture, started: value=>pid=value).ExchangeAsync(BitConverter.GetBytes(2), 128, cancel.Token));
        AssertDead(pid);
        await Assert.ThrowsAsync<TimeoutException>(() => new PreviewSandbox(Fixture, TimeSpan.FromSeconds(2), value=>pid=value).ExchangeAsync(BitConverter.GetBytes(2), 128, CancellationToken.None));
        AssertDead(pid);
    }
    [Fact]
    public async Task Oversized_output_and_memory_exhaustion_are_stopped()
    {
        var pid=0;
        await Assert.ThrowsAsync<InvalidDataException>(() => new PreviewSandbox(Fixture, started: value=>pid=value).ExchangeAsync(BitConverter.GetBytes(3), 128, CancellationToken.None)); AssertDead(pid);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PreviewSandbox(Fixture, TimeSpan.FromSeconds(8), value=>pid=value).ExchangeAsync(BitConverter.GetBytes(4), 128, CancellationToken.None)); AssertDead(pid);
    }
    [Fact]
    public async Task Invalid_input_and_output_cannot_reach_a_ui_decoder()
    {
        var renderer = new WindowsPreviewRenderer(Worker);
        await Assert.ThrowsAsync<InvalidDataException>(() => renderer.RenderAsync(new(PreviewKind.Pdf, "bad.pdf", "%PDF-1.4\ninvalid"u8.ToArray(), true), 0, CancellationToken.None));
        foreach (var (width, height, pages, page, size) in new[] { (1281, 1, 1, 0, 4), (1, 1, 129, 0, 4), (1, 1, 1, 1, 4), (1, 1, 1, 0, int.MaxValue), (1, 1, 1, 0, 0) })
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
            foreach (var value in new[] { 0x31525049, width, height, pages, page, size }) writer.Write(value);
            Assert.Throws<InvalidDataException>(() => WindowsPreviewRenderer.Parse(bytes.ToArray(), 0, PreviewKind.Pdf));
        }
    }
    private static byte[] Center(PreviewFrame frame) => frame.Bgra.AsSpan((frame.Height / 2 * frame.Width + frame.Width / 2) * 4, 4).ToArray();
    private static void AssertDead(int pid)
    {
        Assert.True(pid>0); try { using var process=System.Diagnostics.Process.GetProcessById(pid); Assert.True(process.HasExited); } catch(ArgumentException) { }
    }
}
