using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SqlPhanos.Services;
using SqlPhanos.ViewModels;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace SqlPhanos.Views;

public partial class ShellView : Window
{
    private DispatcherTimer? _updatePulseTimer;
    private bool _updatePulseDim;

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
        // focus changes or the pane-focus/results-toggle shortcuts when they originate inside
        // those controls. Those shortcuts are handled directly here (rather than via a
        // declarative Window.KeyBinding + the ApplicationShortcutMessage pipeline used for the
        // other shortcuts) for that reason. Bubble alone is enough - the Window is the root of
        // the visual tree, so the bubble phase always reaches back here, and handledEventsToo
        // already bypasses any descendant marking the event Handled along the way. Registering
        // Tunnel too made this handler fire twice per event (once tunneling down, once bubbling
        // back up), which silently cancelled out the Ctrl+M formatting toggle (two toggles = no
        // net change).
        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        // ShellView is the one long-lived MainWindow for the app's whole lifetime, so this
        // subscription is never unsubscribed - matches UpdateCheckService's own lifetime.
        UpdateCheckService.UpdateAvailableChanged += OnUpdateAvailableChanged;
        if (UpdateCheckService.AvailableUpdate is not null)
        {
            ShowUpdateAvailable();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        Debug.WriteLine("ShellView XAML loaded");
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.R && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            FocusSearchResultsPane();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.D && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            FocusDocumentsPane();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.Control)
        {
            ToggleResultsGrid();
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

    // Saved connections (connections.json) and the per-server dependency index cache
    // (dependency-indexes\<profileId>.sqlite3) both live under this one folder - see
    // ConnectionProfileStoreService and DependencyExplorerDocumentViewModel, which each compute
    // the same %LocalAppData%\SqlPhanos root independently.
    private void OnOpenDataFolderClick(object? sender, RoutedEventArgs e)
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SqlPhanos");
        Directory.CreateDirectory(dataFolder);
        Process.Start(new ProcessStartInfo(dataFolder) { UseShellExecute = true });
    }

    private void OnUpdateAvailableChanged(object? sender, EventArgs e)
    {
        // Fires from UpdateCheckService's background Timer, not the UI thread.
        Dispatcher.UIThread.Post(ShowUpdateAvailable);
    }

    private void ShowUpdateAvailable()
    {
        if (this.FindControl<MenuItem>("UpdateAvailableMenuItem") is not { } menuItem)
        {
            return;
        }

        menuItem.IsVisible = true;

        if (_updatePulseTimer is not null)
        {
            return;
        }

        // Toggling Opacity every 500ms against the 0.5s DoubleTransition set on this MenuItem in
        // ShellView.axaml is what turns the toggle into a continuous ~1-second fade rather than a
        // hard flicker.
        _updatePulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _updatePulseTimer.Tick += (_, _) =>
        {
            _updatePulseDim = !_updatePulseDim;
            menuItem.Opacity = _updatePulseDim ? 0.3 : 1.0;
        };
        _updatePulseTimer.Start();
    }

    private async void OnCheckForUpdateClick(object? sender, RoutedEventArgs e)
    {
        var update = UpdateCheckService.AvailableUpdate;
        if (update is null)
        {
            return;
        }

        var dialog = new UpdateAvailableView();
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        string zipPath;
        try
        {
            zipPath = await DownloadUpdateAsync(update.DownloadUrl);
        }
        catch (Exception ex)
        {
            // Download failed - leave the app running untouched, same as clicking Later. The
            // updater is never launched unless the download actually succeeded.
            Debug.WriteLine($"Update download failed: {ex}");
            return;
        }

        var updaterPath = Path.Combine(AppContext.BaseDirectory, "SqlPhanos.Updater.exe");
        var relaunchPath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "SqlPhanos.exe");

        Process.Start(updaterPath, new[]
        {
            Environment.ProcessId.ToString(),
            zipPath,
            AppContext.BaseDirectory,
            relaunchPath
        });

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static async Task<string> DownloadUpdateAsync(string url)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SqlPhanos-UpdateCheck");

        var bytes = await client.GetByteArrayAsync(url);
        var zipPath = Path.Combine(Path.GetTempPath(), $"SqlPhanos-update-{Guid.NewGuid():N}.zip");
        await File.WriteAllBytesAsync(zipPath, bytes);
        return zipPath;
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

    private void OnFocusSearchResultsClick(object? sender, RoutedEventArgs e) => FocusSearchResultsPane();

    private void OnFocusDocumentsClick(object? sender, RoutedEventArgs e) => FocusDocumentsPane();

    private void OnToggleResultsGridClick(object? sender, RoutedEventArgs e) => ToggleResultsGrid();

    private void ToggleResultsGrid()
    {
        (DataContext as ShellViewModel)?.ToggleResultsGrid();

        // Follow the pane that Ctrl+R just revealed, same as jumping there directly - showing
        // a results view without moving focus into it would be a half-finished-feeling toggle.
        // Deferred (unlike FocusSearchResultsPane's other callers): the toggle just changed
        // which of SearchResultsView/SearchResultsGridView is present in the dock tree, and its
        // control isn't necessarily materialized in the visual tree yet at this exact point -
        // GetVisualDescendants() in FocusSearchResultsPaneDefault needs a layout pass to have
        // run first, or it finds nothing and silently does nothing. Same reasoning as
        // SearchResultsView's own Dispatcher deferral for "focus first result."
        Dispatcher.UIThread.Post(FocusSearchResultsPane, DispatcherPriority.Input);
    }

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
        if (visual.FindAncestorOfType<SearchResultsView>(includeSelf: true) is not null ||
            visual.FindAncestorOfType<SearchResultsGridView>(includeSelf: true) is not null)
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

    // Ctrl+Shift+R - jumps straight to whichever results view (card or grid) is currently
    // pinned/visible, restoring keyboard focus to whatever control was last focused there
    // (PaneFocusTracker), or that view's own "focus first result" default if nothing is tracked
    // yet (e.g. the pane hasn't been focused since the app opened, or since it last flipped
    // between card/grid). Unlike the old Ctrl+Shift+P cycling this replaces, this always jumps
    // straight to this one pane rather than computing a "next" pane from wherever focus happens
    // to be now.
    private void FocusSearchResultsPane() => FocusPane("SearchResults", FocusSearchResultsPaneDefault);

    // Ctrl+Shift+D - same idea as FocusSearchResultsPane, for the Documents/MDI area.
    private void FocusDocumentsPane() => FocusPane("Documents", FocusDocumentsPaneDefault);

    private static void FocusPane(string paneId, System.Action focusDefault)
    {
        if (PaneFocusTracker.TryGetLastFocus(paneId, out var element) && element is not null)
        {
            element.Focus();
            return;
        }

        focusDefault();
    }

    private void FocusSearchResultsPaneDefault()
    {
        // Exactly one of the two results views is ever pinned/visible at a time (see
        // ShellViewModel.ApplyResultsViewMode) - find whichever one that currently is.
        switch (this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(v => (v is SearchResultsView or SearchResultsGridView) && v.IsEffectivelyVisible))
        {
            case SearchResultsView cardView:
                cardView.FocusFirstResult();
                break;
            case SearchResultsGridView gridView:
                gridView.FocusFirstResult();
                break;
        }
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
