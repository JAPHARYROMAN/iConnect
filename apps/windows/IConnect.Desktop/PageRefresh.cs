using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private sealed record CardScope(string Key);
    private sealed record FocusBookmark(string Card, string Action, bool AutomationId, FocusState State)
    {
        internal bool Matches(Button button) => AutomationId
            ? AutomationProperties.GetAutomationId(button) == Action
            : string.IsNullOrEmpty(AutomationProperties.GetAutomationId(button)) && button.Content is string label && label == Action;
    }
    private sealed record FocusRestore(FocusBookmark Bookmark, object? Fallback);
    private readonly Dictionary<string, Border> refreshCards = [];
    private readonly Dictionary<string, Expander> refreshExpanders = [];
    private HashSet<string> expandedOnRefresh = [];
    private string? renderedPage;
    private FocusRestore? pendingFocus;
    private bool windowActive;

    private Border RefreshableCard(string key, string title, string text, params UIElement[] actions) =>
        RegisterRefreshCard(key, Card(title, text, actions));

    private Border RefreshableCard(string key, string title, Func<string?> subtitle, params UIElement[] actions) =>
        RegisterRefreshCard(key, Card(title, ProgressBody(subtitle), actions));

    private Border RegisterRefreshCard(string key, Border card)
    {
        card.Tag = new CardScope(key);
        refreshCards.Add(key, card);
        return card;
    }

    private Expander RefreshableExpander(string key, string header, UIElement content)
    {
        var expander = new Expander { Header = header, Content = content, IsExpanded = expandedOnRefresh.Contains(key),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        refreshExpanders.Add(key, expander);
        return expander;
    }

    private object? FocusedElement() => Page.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;

    private FocusBookmark? BeginPageRefresh()
    {
        var samePage = renderedPage == currentPage;
        var focused = samePage && windowActive && activeDialog is null ? FocusedElement() : null;
        var bookmark = ReadFocusBookmark(focused as DependencyObject);
        // Several durable updates can arrive before the dispatcher restores focus.
        if (bookmark is null && samePage && windowActive && activeDialog is null &&
            pendingFocus is { } pending && ReferenceEquals(focused, pending.Fallback))
            bookmark = pending.Bookmark;
        pendingFocus = null;
        expandedOnRefresh = samePage ? refreshExpanders.Where(x => x.Value.IsExpanded).Select(x => x.Key).ToHashSet() : [];
        refreshExpanders.Clear();
        refreshCards.Clear();
        renderedPage = currentPage;
        return bookmark;
    }

    private static FocusBookmark? ReadFocusBookmark(DependencyObject? focused)
    {
        Button? button = null;
        for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            button ??= element as Button;
            if (element is not Border { Tag: CardScope scope }) continue;
            if (button is not { Content: string action, FocusState: FocusState.Keyboard or FocusState.Programmatic }) return null;
            var id = AutomationProperties.GetAutomationId(button);
            var bookmark = new FocusBookmark(scope.Key, string.IsNullOrEmpty(id) ? action : id, !string.IsNullOrEmpty(id), button.FocusState);
            return ButtonsIn(element).Where(bookmark.Matches).Take(2).Count() == 1 ? bookmark : null;
        }
        return null;
    }

    private void RestoreRefreshFocus(FocusBookmark? bookmark)
    {
        if (bookmark is null || !refreshCards.TryGetValue(bookmark.Card, out var card)) return;
        var restore = new FocusRestore(bookmark, FocusedElement());
        pendingFocus = restore;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!ReferenceEquals(pendingFocus, restore)) return;
            pendingFocus = null;
            if (closing || hidden || !windowActive || activeDialog is not null ||
                !ReferenceEquals(FocusedElement(), restore.Fallback)) return;
            var matches = ButtonsIn(card).Where(bookmark.Matches).Take(2).ToArray();
            if (matches.Length == 1 && matches[0] is { IsEnabled: true, IsLoaded: true } button) button.Focus(bookmark.State);
        })) pendingFocus = null;
    }

    private static IEnumerable<Button> ButtonsIn(DependencyObject root)
    {
        if (root is Button button) { yield return button; yield break; }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in ButtonsIn(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
