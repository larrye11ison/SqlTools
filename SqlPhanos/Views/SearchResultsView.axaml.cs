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
        // ever see it, so this must observe already-handled events too.
        var resultsList = this.FindControl<ListBox>("ResultsList");
        resultsList?.AddHandler(KeyDownEvent, ResultsList_KeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        WeakReferenceMessenger.Default.Register<FocusFirstSearchResultMessage>(this);
    }

    public void Receive(FocusFirstSearchResultMessage message)
    {
        // This runs synchronously inside the same call stack as the FilteredResults
        // reassignment that triggered it, before the ListBox's ItemsSource binding and
        // layout have necessarily caught up - selecting/focusing immediately here was
        // landing focus on the ListBox before it had a live SelectedItem, so Enter had
        // nothing to act on. Deferring to a dispatcher pass after Loaded/Render priority
        // work (Input is lower than both) guarantees the binding has settled first.
        Dispatcher.UIThread.Post(() =>
        {
            var list = this.FindControl<ListBox>("ResultsList");
            if (list is null)
            {
                return;
            }

            if (list.ItemsSource is System.Collections.IList { Count: > 0 })
            {
                list.SelectedIndex = 0;
            }

            list.Focus();
        }, DispatcherPriority.Input);
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
