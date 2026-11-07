using IConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private void AddPreview(PreviewSession session)
    {
        var view = session.View; if (view.Name.Length == 0) return;
        var panel = Column(Body(view.Name, 18), Body("PNG, JPEG and PDF up to 8 MiB. UTF-8 text: first 32 KiB.", 12));
        if (view.Busy) panel.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left });
        if (view.Error.Length != 0) panel.Children.Add(Body(view.Error));
        if (view.Text.Length != 0) panel.Children.Add(new ScrollViewer { Content = Selectable(view.Text), MaxHeight = 300 });
        if (view.Frame is { } frame)
        {
            // The worker's wire frame is raw BGRA; WinUI never parses a received image/PDF file.
            var bitmap = new WriteableBitmap(frame.Width, frame.Height);
            using (var pixels = bitmap.PixelBuffer.AsStream()) pixels.Write(frame.Bgra);
            bitmap.Invalidate();
            var rotation = new RotateTransform();
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = 340, MaxHeight = 340,
                RenderTransform = rotation, RenderTransformOrigin = new Windows.Foundation.Point(.5, .5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(image, $"{view.Name}, page {frame.Page + 1} of {frame.Pages}");
            var canvas = new Grid { Width = 360, Height = 360 }; canvas.Children.Add(image);
            var viewport = new ScrollViewer { Content = canvas, Height = 380, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 1, MaxZoomFactor = 4,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(viewport, "Preview image. Use the zoom and rotation controls."); panel.Children.Add(viewport);
            panel.Children.Add(Row(Action("Zoom out", () => { viewport.ChangeView(null, null, Math.Max(1, viewport.ZoomFactor / 1.25f)); return Task.CompletedTask; }),
                Action("Zoom in", () => { viewport.ChangeView(null, null, Math.Min(4, viewport.ZoomFactor * 1.25f)); return Task.CompletedTask; }),
                Action("Rotate 90°", () => { rotation.Angle = (rotation.Angle + 90) % 360; return Task.CompletedTask; }),
                Action("Reset view", () => { rotation.Angle = 0; viewport.ChangeView(0, 0, 1); return Task.CompletedTask; })));
            if (frame.Pages > 1) panel.Children.Add(Row(Action("Previous PDF page", () => session.PageAsync(frame.Page - 1), !view.Busy && frame.Page > 0),
                Body($"Page {frame.Page + 1} of {frame.Pages}"), Action("Next PDF page", () => session.PageAsync(frame.Page + 1), !view.Busy && frame.Page + 1 < frame.Pages)));
        }
        panel.Children.Add(Action("Close preview", () => { session.Clear(); return Task.CompletedTask; }));
        var card=Card("File preview", "", panel);
        card.Loaded += (_,_) => card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        Page.Children.Add(card);
    }
}
