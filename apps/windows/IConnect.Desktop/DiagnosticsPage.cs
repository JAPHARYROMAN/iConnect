using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private async Task ExportDiagnosticsAsync()
    {
        var review = await Model.ReviewDiagnostics();
        var preview = new TextBox { Text = review.Json, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            MaxHeight = 360, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        try
        {
            if (await ShowAsync(Dialog("Review redacted diagnostic report", Column(
                Body("This exact JSON contains app/OS versions, connection states, permission and queue counts, recovery counts and the number of local activity entries. Counts can reveal usage. Names, device IDs, addresses, filenames, paths, content, pairing data, activity text and raw errors are omitted. It is a best-effort snapshot; the review expires after two minutes."),
                preview), "Choose report folder…")) != ContentDialogResult.Primary) return;
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSingleFolderAsync(); if (destination is null) return;
            var path = await Model.ExportDiagnostics(review, destination.Path);
            await ShowAsync(Dialog("Diagnostic report created", Column(Body("The new report was verified after writing. Share it only when you choose."), Selectable(path)), "Done"));
        }
        finally { Model.CancelDiagnosticReview(); }
    }
}
