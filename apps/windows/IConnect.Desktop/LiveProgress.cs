using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using IConnect.Storage.Windows;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private readonly List<Action> liveProgress = [];

    private TextBlock ProgressBody(Func<string?> read, double size = 14)
    {
        var text = Body("", size);
        UpdateProgressText(text, read());
        liveProgress.Add(() => UpdateProgressText(text, read()));
        return text;
    }

    private ProgressBar DownloadProgressBar(Func<DownloadJob> read)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100 };
        void Update()
        {
            var job = read();
            var value = job.Size == 0 ? 0 : (double)job.Offset / job.Size * 100;
            if (bar.Value != value) bar.Value = value;
            var indeterminate = job.Phase is DownloadPhase.Preparing or DownloadPhase.Verifying;
            if (bar.IsIndeterminate != indeterminate) bar.IsIndeterminate = indeterminate;
        }
        Update();
        liveProgress.Add(Update);
        return bar;
    }

    private void RefreshProgress()
    {
        if (closing || hidden || currentPage is not ("transfers" or "local")) return;
        foreach (var update in liveProgress) update();
    }

    private static void UpdateProgressText(TextBlock text, string? value)
    {
        value ??= "";
        if (text.Text != value) text.Text = value;
        var visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (text.Visibility != visibility) text.Visibility = visibility;
    }
}
