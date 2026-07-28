using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using SqlPhanos.Messages;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Views;

public partial class SearchResultsView : UserControl, IRecipient<FocusFirstSearchResultMessage>
{
    public SearchResultsView()
    {
        InitializeComponent();

        // The ListBox's own internal key handling (item navigation) marks Enter as
        // Handled before an ordinary bubble-routed handler on the same element would
        // ever see it, so this must observe already-handled events too. Bubble alone is
        // enough - real keyboard focus always lands on an item container (a descendant
        // of this ListBox, see FocusFirstResult below), so the bubble phase always
        // passes through here. Registering Tunnel too made this fire twice per Enter
        // press (confirmed via a standalone repro) - harmless today only because
        // ScriptObjectInternalAsync's IsScripting guard happens to absorb the repeat.
        var resultsList = this.FindControl<ListBox>("ResultsList");
        resultsList?.AddHandler(KeyDownEvent, ResultsList_KeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        WeakReferenceMessenger.Default.Register<FocusFirstSearchResultMessage>(this);
    }

    public void Receive(FocusFirstSearchResultMessage message)
    {
        // The grid view (SearchResultsGridView) is registered for the exact same message -
        // only whichever of the two is currently pinned/visible should react (see
        // ShellViewModel.ApplyResultsViewMode - the other one is unpinned/detached at any
        // given time, never both).
        if (!this.IsEffectivelyVisible)
        {
            return;
        }

        // This runs synchronously inside the same call stack as the FilteredResults
        // reassignment that triggered it, before the ListBox's ItemsSource binding and
        // layout have necessarily caught up, so this is deferred to a dispatcher pass
        // after Loaded/Render priority work (Input is lower than both) to guarantee the
        // binding - and item container generation - have settled first.
        Dispatcher.UIThread.Post(FocusFirstResult, DispatcherPriority.Input);
    }

    // Also used by ShellView's Ctrl+Shift+P pane-cycling default for this pane, which had
    // the identical bug.
    public void FocusFirstResult()
    {
        var list = this.FindControl<ListBox>("ResultsList");
        if (list is null || list.ItemsSource is not System.Collections.IList { Count: > 0 })
        {
            return;
        }

        list.SelectedIndex = 0;

        // ListBox.Focusable is False by default - only its item containers are focusable
        // (confirmed via a standalone repro: ListBox.Focus() silently returns false,
        // leaving real keyboard focus wherever it was before, e.g. still on a Search-pane
        // control, so Enter kept triggering that pane's IsDefault button instead of
        // scripting anything here). Focusing the realized container for index 0 is what
        // actually moves keyboard focus; ListBox.Focus() is only a fallback for the case
        // where no container has been realized yet.
        if (list.ContainerFromIndex(0) is { } container)
        {
            container.Focus();
        }
        else
        {
            list.Focus();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ResultsList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (sender is not ListBox list || list.SelectedItem is not SearchResultViewModel item)
        {
            return;
        }

        if (DataContext is not SearchResultsViewModel viewModel)
        {
            return;
        }

        viewModel.ScriptObjectCommand.Execute(item);
        e.Handled = true;
    }

    private void ResultsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not SearchResultViewModel item)
        {
            return;
        }

        if (DataContext is not SearchResultsViewModel viewModel)
        {
            return;
        }

        viewModel.ScriptObjectCommand.Execute(item);
        e.Handled = true;
    }
}
