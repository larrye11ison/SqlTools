using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
