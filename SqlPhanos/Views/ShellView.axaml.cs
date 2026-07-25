using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
        // Bubble alone is enough - the Window is the root of the visual tree, so the bubble
        // phase always reaches back here, and handledEventsToo already bypasses any descendant
        // marking the event Handled along the way. Registering Tunnel too made this handler
        // fire twice per event (once tunneling down, once bubbling back up), which silently
        // cancelled out the Ctrl+M formatting toggle (two toggles = no net change).
        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
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
            CloseActiveDocument();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            OpenFindInActiveDocument();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.M && e.KeyModifiers == KeyModifiers.Control)
        {
            ToggleActiveDocumentFormatting();
            e.Handled = true;
        }
    }

    // Shared by both the OnWindowKeyDown shortcuts above and the menu items below, so the
    // top menu always does exactly what its matching keyboard shortcut does.
    private void CloseActiveDocument()
    {
        (DataContext as ShellViewModel)?.CloseActiveDocument();
    }

    private void OpenFindInActiveDocument()
    {
        if (GetActiveDocumentView() is SqlDocumentView sqlView)
        {
            sqlView.OpenFind();
        }
    }

    // Ctrl+M does "toggle formatting" on a script tab and "reformat in place" on a query tab -
    // different actions on different document types, but the same key, matching how the user
    // thinks about both as "fix up how this SQL looks."
    private void ToggleActiveDocumentFormatting()
    {
        switch (GetActiveDocumentView()?.DataContext)
        {
            case SqlDocumentViewModel sqlDocumentViewModel:
                sqlDocumentViewModel.ToggleDisplayModeCommand.Execute(null);
                break;
            case QueryXLeratorDocumentViewModel queryViewModel:
                queryViewModel.ReformatCommand.Execute(null);
                break;
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settingsView = new SettingsView();
        await settingsView.ShowDialog(this);
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var aboutView = new AboutView();
        await aboutView.ShowDialog(this);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void OnToggleFormattingClick(object? sender, RoutedEventArgs e) => ToggleActiveDocumentFormatting();

    private void OnFindClick(object? sender, RoutedEventArgs e) => OpenFindInActiveDocument();

    private void OnCloseDocumentClick(object? sender, RoutedEventArgs e) => CloseActiveDocument();

    private void OnCyclePaneClick(object? sender, RoutedEventArgs e) => FocusNextPane();

    // Returns whichever MDI document view (SqlDocumentView, QueryXLeratorDocumentView, or
    // ScriptDatabasesDocumentView) is currently active, or null if none is / no document is open.
    private Control? GetActiveDocumentView()
    {
        return this.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(v => v is (SqlDocumentView or QueryXLeratorDocumentView or ScriptDatabasesDocumentView) && v.IsEffectivelyVisible);
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
        else if (visual.FindAncestorOfType<SqlDocumentView>(includeSelf: true) is not null ||
                 visual.FindAncestorOfType<QueryXLeratorDocumentView>(includeSelf: true) is not null ||
                 visual.FindAncestorOfType<ScriptDatabasesDocumentView>(includeSelf: true) is not null)
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
        searchResultsView?.FocusFirstResult();
    }

    private void FocusDocumentsPaneDefault()
    {
        switch (GetActiveDocumentView())
        {
            case SqlDocumentView sqlView:
                sqlView.FocusEditor();
                break;
            case QueryXLeratorDocumentView queryView:
                queryView.FocusEditor();
                break;
            case ScriptDatabasesDocumentView scriptDatabasesView:
                scriptDatabasesView.FocusDefault();
                break;
        }
    }
}
