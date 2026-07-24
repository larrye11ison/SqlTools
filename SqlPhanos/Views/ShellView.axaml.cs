using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using SqlPhanos.Services;
using SqlPhanos.ViewModels;
using System.Diagnostics;
using System.Linq;

namespace SqlPhanos.Views;

public partial class ShellView : Window
{
    private static readonly string[] PaneOrder = { "Search", "Documents", "SearchResults" };

    public ShellView()
    {
        Debug.WriteLine("=== ShellView constructor ===");
        InitializeComponent();

        var viewModel = new ShellViewModel();
        DataContext = viewModel;

        Debug.WriteLine($"ShellView DataContext set to: {DataContext?.GetType().Name}");

        // handledEventsToo is required on both of these: descendants (the DataGrid,
        // AvaloniaEdit's TextEditor, etc.) routinely mark GotFocus/KeyDown as handled as part
        // of their own internal behavior, which would otherwise stop this from ever observing
        // focus changes or the Ctrl+Shift+P shortcut when it originates inside those controls.
        // Ctrl+Shift+P is handled directly here (rather than via a declarative Window.KeyBinding
        // + the ApplicationShortcutMessage pipeline used for the other shortcuts) for that reason.
        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        Debug.WriteLine("ShellView XAML loaded");
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            FocusNextPane();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control)
        {
            (DataContext as ShellViewModel)?.CloseActiveDocument();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            GetActiveDocumentView()?.OpenFind();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.M && e.KeyModifiers == KeyModifiers.Control)
        {
            if (GetActiveDocumentView()?.DataContext is SqlDocumentViewModel activeDocumentViewModel)
            {
                activeDocumentViewModel.ToggleDisplayModeCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settingsView = new SettingsView();
        await settingsView.ShowDialog(this);
    }

    private SqlDocumentView? GetActiveDocumentView()
    {
        return this.GetVisualDescendants()
            .OfType<SqlDocumentView>()
            .FirstOrDefault(v => v.IsEffectivelyVisible);
    }

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NewFocusedElement is not IInputElement element || element is not Visual visual)
        {
            return;
        }

        string? paneId = null;
        if (visual.FindAncestorOfType<SearchResultsView>(includeSelf: true) is not null)
        {
            paneId = "SearchResults";
        }
        else if (visual.FindAncestorOfType<SearchView>(includeSelf: true) is not null)
        {
            paneId = "Search";
        }
        else if (visual.FindAncestorOfType<SqlDocumentView>(includeSelf: true) is not null)
        {
            paneId = "Documents";
        }

        if (paneId is not null)
        {
            PaneFocusTracker.RecordFocus(paneId, element);
        }
    }

    private void FocusNextPane()
    {
        var currentIndex = System.Array.IndexOf(PaneOrder, PaneFocusTracker.CurrentPaneId);
        var nextPaneId = PaneOrder[(currentIndex + 1 + PaneOrder.Length) % PaneOrder.Length];

        // Always advance the cycle position, even if nothing ends up actually receiving focus
        // below (e.g. the Documents pane's default target doesn't exist because no document is
        // open). Otherwise GotFocus never fires for a no-op focus attempt, CurrentPaneId never
        // moves off the pane the user started in, and every subsequent press recomputes the
        // same "next" pane - effectively trapping the user in whichever pane they started from.
        PaneFocusTracker.CurrentPaneId = nextPaneId;

        if (PaneFocusTracker.TryGetLastFocus(nextPaneId, out var element) && element is not null)
        {
            element.Focus();
            return;
        }

        switch (nextPaneId)
        {
            case "Search":
                FocusSearchPaneDefault();
                break;
            case "SearchResults":
                FocusSearchResultsPaneDefault();
                break;
            case "Documents":
                FocusDocumentsPaneDefault();
                break;
        }
    }

    private void FocusSearchPaneDefault()
    {
        var searchView = this.GetVisualDescendants().OfType<SearchView>().FirstOrDefault();
        var objectNameBox = searchView?.FindControl<TextBox>("ObjectNameBox");
        objectNameBox?.Focus();
    }

    private void FocusSearchResultsPaneDefault()
    {
        var searchResultsView = this.GetVisualDescendants().OfType<SearchResultsView>().FirstOrDefault();
        var list = searchResultsView?.FindControl<ListBox>("ResultsList");
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

    private void FocusDocumentsPaneDefault()
    {
        GetActiveDocumentView()?.FocusEditor();
    }
}
