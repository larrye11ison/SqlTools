using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Views;

public partial class SearchResultsView : UserControl
{
    public SearchResultsView()
    {
        InitializeComponent();

        // The DataGrid's own internal key handling (row navigation) marks Enter as
        // Handled before an ordinary bubble-routed handler on the same element would
        // ever see it, so this must observe already-handled events too.
        var resultsGrid = this.FindControl<DataGrid>("ResultsGrid");
        resultsGrid?.AddHandler(KeyDownEvent, ResultsGrid_KeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ResultsGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (sender is not DataGrid grid || grid.SelectedItem is not SearchResultViewModel item)
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

    private void ResultsGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not SearchResultViewModel item)
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
