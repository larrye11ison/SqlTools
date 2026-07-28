using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using SqlPhanos.Messages;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Views;

public partial class SearchResultsGridView : UserControl, IRecipient<FocusFirstSearchResultMessage>
{
    public SearchResultsGridView()
    {
        InitializeComponent();
        WeakReferenceMessenger.Default.Register<FocusFirstSearchResultMessage>(this);

        // Bubble + handledEventsToo (the fix that worked for the card view's ListBox, see
        // SearchResultsView's constructor) gets the Script command to run here too, but DataGrid
        // still *also* moves to the next row on Enter - unlike ListBox, that row-navigation is a
        // real side effect that already happened by the time a Bubble handler runs, not just a
        // Handled flag my handler could suppress after the fact. Tunnel intercepts Enter before
        // DataGrid's own handling ever sees it, so setting Handled=true here actually prevents
        // the navigation instead of just outrunning it.
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        grid?.AddHandler(KeyDownEvent, ResultsGrid_KeyDown, RoutingStrategies.Tunnel);
    }

    public void Receive(FocusFirstSearchResultMessage message)
    {
        // The card view (SearchResultsView) is registered for the exact same message - only
        // whichever of the two is currently pinned/visible should react (see
        // ShellViewModel.ApplyResultsViewMode - the other one is unpinned/detached at any
        // given time, never both).
        if (!this.IsEffectivelyVisible)
        {
            return;
        }

        // Deferred for the same reason as SearchResultsView's identical handler: this runs
        // synchronously inside the FilteredResults reassignment that triggered it, before the
        // DataGrid's ItemsSource binding has necessarily caught up.
        Dispatcher.UIThread.Post(FocusFirstResult, DispatcherPriority.Input);
    }

    // Also used by ShellView's Ctrl+Shift+R default when nothing was previously focused here.
    public void FocusFirstResult()
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid is null || grid.ItemsSource is not System.Collections.IList { Count: > 0 })
        {
            return;
        }

        grid.SelectedIndex = 0;
        grid.Focus();
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

        if (DataContext is not SearchResultsGridViewModel viewModel)
        {
            return;
        }

        viewModel.SearchResults.ScriptObjectCommand.Execute(item);
        e.Handled = true;
    }

    private void ResultsGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not SearchResultViewModel item)
        {
            return;
        }

        if (DataContext is not SearchResultsGridViewModel viewModel)
        {
            return;
        }

        viewModel.SearchResults.ScriptObjectCommand.Execute(item);
        e.Handled = true;
    }
}
