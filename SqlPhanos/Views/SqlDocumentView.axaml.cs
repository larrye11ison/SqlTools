using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlPhanos.Services;
using SqlPhanos.ViewModels;
using System;
using System.ComponentModel;
using TextMateSharp.Grammars;
using TextMateHost = AvaloniaEdit.TextMate.TextMate;

namespace SqlPhanos.Views;

public partial class SqlDocumentView : UserControl
{
    private enum FindTarget { Sql, Clr }

    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private TextEditor? _editor;
    private TextEditor? _clrEditor;
    private Grid? _contentGrid;
    private TextMateHost.Installation? _textMateInstallation;
    private TextMateHost.Installation? _clrTextMateInstallation;
    private SearchPanel? _searchPanel;
    private SearchPanel? _clrSearchPanel;
    // Which editor's Find panel is currently open, if any - Ctrl+F toggles between the two
    // rather than opening both at once. Null until the first Ctrl+F (or after both are closed).
    private FindTarget? _openFindTarget;
    private SqlDocumentViewModel? _trackedViewModel;
    private int? _pendingCaretOffset;
    private bool _pendingCaretWasFormatted;

    public SqlDocumentView()
    {
        InitializeComponent();

        if (this.FindControl<Control>("EncryptedConsentOverlay") is { } overlay &&
            this.FindControl<Button>("ConfirmDecryptButton") is { } confirmButton)
        {
            OverlayFocusHelper.FocusOnShow(overlay, confirmButton);
        }

        AttachedToVisualTree += (_, _) => EnsureTextMateInstalled();
        DetachedFromVisualTree += (_, _) => DisposeTextMate();
        DataContextChanged += (_, _) => SyncFromViewModel();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();

        ApplyFont();
        FontSettingsService.SettingsChanged += OnFontSettingsChanged;
        DetachedFromVisualTree += (_, _) => FontSettingsService.SettingsChanged -= OnFontSettingsChanged;
    }

    public void FocusEditor()
    {
        // TextEditor itself is deliberately non-focusable by design (AvaloniaEdit sets
        // FocusableProperty.OverrideDefaultValue<TextEditor>(false) - only TextArea, its
        // child, actually is), so _editor.Focus() is a silent no-op. Mouse clicks work because
        // TextArea's own pointer-press handling focuses it directly, bypassing TextEditor
        // entirely - which is exactly why this only ever worked after clicking with the mouse.
        _editor?.TextArea.Focus();
    }

    public void OpenFind()
    {
        // A CLR object's tab has two editors (SQL wrapper + decompiled C#) sharing the same
        // Ctrl+F - only one Find panel may be open at a time, and repeated Ctrl+F presses toggle
        // which editor it's attached to, rather than both showing up together.
        var clrAvailable = _clrEditor is { IsVisible: true };

        FindTarget target;
        if (_openFindTarget is { } current)
        {
            target = current == FindTarget.Sql ? FindTarget.Clr : FindTarget.Sql;
            if (target == FindTarget.Clr && !clrAvailable)
            {
                target = FindTarget.Sql;
            }
        }
        else
        {
            // First press: prefer whichever editor currently has focus, so Ctrl+F acts on
            // wherever the user's attention already is rather than always defaulting to the top.
            target = clrAvailable && _clrEditor!.TextArea.IsFocused ? FindTarget.Clr : FindTarget.Sql;
        }

        var (panelToOpen, panelToClose) = target == FindTarget.Sql
            ? (_searchPanel, _clrSearchPanel)
            : (_clrSearchPanel, _searchPanel);

        if (panelToClose is { IsClosed: false })
        {
            panelToClose.Close();
        }

        if (panelToOpen is null)
        {
            return;
        }

        // Open() (AvaloniaEdit's own SearchPanel, used the first time or after Close()) never
        // focuses its own search box - only Reactivate() does (confirmed against AvaloniaEdit's
        // actual source). That's fine when Ctrl+F is pressed while this tab's editor already has
        // focus, since focus already being nearby made it *look* like it worked before, but not
        // when focus was elsewhere (a different pane, a different tab) - the panel became
        // visible but nothing ever moved keyboard focus into it. Always reactivating after
        // opening covers both cases with one code path. Deferred a tick: Open() only just added
        // the panel as a child, and its search box needs its own template applied - the same
        // "control not materialized yet" timing already solved elsewhere in this app - before
        // Reactivate()'s Focus() call has anything real to land on.
        if (panelToOpen.IsClosed)
        {
            panelToOpen.Open();
        }

        Dispatcher.UIThread.Post(() => panelToOpen.Reactivate(), DispatcherPriority.Input);
        _openFindTarget = target;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<TextEditor>("Editor");
        _clrEditor = this.FindControl<TextEditor>("ClrEditor");
        _contentGrid = this.FindControl<Grid>("ContentGrid");
    }

    private void OnSaveAsDllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SqlDocumentViewModel viewModel ||
            !viewModel.TryGetClrAssemblyBytes(out var bytes, out var suggestedFileName))
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        async void SaveAsync()
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save assembly as",
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "dll",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Assembly DLL") { Patterns = new[] { "*.dll" } }
                }
            });

            if (file is not null)
            {
                await using var stream = await file.OpenWriteAsync();
                await stream.WriteAsync(bytes);
            }
        }

        SaveAsync();
    }

    private void OnFontSettingsChanged(object? sender, System.EventArgs e)
    {
        ApplyFont();
    }

    private void ApplyFont()
    {
        if (_editor is not null)
        {
            _editor.FontFamily = new FontFamily(FontSettingsService.CurrentFontFamily);
            _editor.FontSize = FontSettingsService.CurrentFontSize;
        }

        if (_clrEditor is not null)
        {
            _clrEditor.FontFamily = new FontFamily(FontSettingsService.CurrentFontFamily);
            _clrEditor.FontSize = FontSettingsService.CurrentFontSize;
        }
    }

    private void EnsureTextMateInstalled()
    {
        if (_editor is null)
        {
            return;
        }

        _searchPanel ??= SearchPanel.Install(_editor);
        if (_clrEditor is not null)
        {
            _clrSearchPanel ??= SearchPanel.Install(_clrEditor);
        }

        if (_textMateInstallation is not null)
        {
            return;
        }

        _textMateInstallation = TextMateHost.InstallTextMate(
            _editor,
            _registryOptions,
            true,
            ex => System.Diagnostics.Debug.WriteLine($"TextMate initialization error: {ex.Message}"));

        if (_clrEditor is not null)
        {
            _clrTextMateInstallation = TextMateHost.InstallTextMate(
                _clrEditor,
                _registryOptions,
                true,
                ex => System.Diagnostics.Debug.WriteLine($"TextMate initialization error (CLR editor): {ex.Message}"));
            _clrTextMateInstallation.SetGrammar("source.cs");
        }

        ApplyTheme();
        SyncFromViewModel();
    }

    private void SyncFromViewModel()
    {
        if (_editor is null)
        {
            return;
        }

        if (_trackedViewModel is not null)
        {
            _trackedViewModel.PropertyChanging -= OnViewModelPropertyChanging;
            _trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _trackedViewModel = null;
        }

        if (DataContext is SqlDocumentViewModel viewModel)
        {
            _trackedViewModel = viewModel;
            _trackedViewModel.PropertyChanging += OnViewModelPropertyChanging;
            _trackedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateEditorDocument(viewModel);
            ApplyGrammar(viewModel.SyntaxScopeName);
            // Re-attaching (e.g. switching MDI tabs away and back) disposes and reinstalls
            // TextMate (see AttachedToVisualTree/DetachedFromVisualTree), which is what was
            // silently dropping the decompiled C# pane's content - nothing here previously
            // restored it or the CLR-vs-non-CLR row sizing on that path, only the main editor.
            UpdateClrEditorDocument(viewModel);
            ApplyClrRowSizing(viewModel);
        }
        else
        {
            _editor.Document = new TextDocument();
            ApplyGrammar("source.sql");
        }
    }

    // The CLR pane's row is star-sized (for the GridSplitter to resize against the SQL editor)
    // rather than Auto, so it doesn't automatically collapse to zero height just because its
    // content's IsVisible is false the way an Auto row would - a non-CLR object was otherwise
    // left with a large empty reserved area where the CLR pane would be. IsClrObject/
    // HasClrDecompileError are fixed for a given tab's whole lifetime (one object scripted once),
    // so this only ever needs to run once per load/reattach, not track further live changes.
    private void ApplyClrRowSizing(SqlDocumentViewModel viewModel)
    {
        if (_contentGrid is null || _contentGrid.RowDefinitions.Count < 3)
        {
            return;
        }

        // Star-sized (splitter-adjustable) when there's a real second editor to show; Auto
        // (just big enough for the one-line error banner, no splitter) when decompilation failed
        // and there's no editor to divide space with; zero for an ordinary non-CLR object.
        _contentGrid.RowDefinitions[2].Height = viewModel.IsClrObject
            ? new GridLength(3, GridUnitType.Star)
            : viewModel.HasClrDecompileError
                ? GridLength.Auto
                : new GridLength(0);
    }

    private void OnViewModelPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        // CurrentSqlText only changes in one place: ShowOriginal()/ShowFormatted(), which the
        // Original/Formatted toggle command calls - never on typing or caret movement, since
        // nothing here listens to the editor's own caret events. Firing right before the swap
        // (not after) is what lets this capture where the caret was in the *old* rendering,
        // before UpdateEditorDocument below replaces the text under it.
        if (e.PropertyName != nameof(SqlDocumentViewModel.CurrentSqlText) || _editor is null ||
            sender is not SqlDocumentViewModel viewModel)
        {
            return;
        }

        _pendingCaretOffset = _editor.CaretOffset;
        _pendingCaretWasFormatted = viewModel.IsShowingFormatted;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SqlDocumentViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(SqlDocumentViewModel.CurrentSqlText))
        {
            UpdateEditorDocument(viewModel);
        }
        else if (e.PropertyName == nameof(SqlDocumentViewModel.DecompiledClrSource))
        {
            UpdateClrEditorDocument(viewModel);
        }
        else if (e.PropertyName is nameof(SqlDocumentViewModel.IsClrObject) or nameof(SqlDocumentViewModel.HasClrDecompileError))
        {
            ApplyClrRowSizing(viewModel);
        }
    }

    private void UpdateClrEditorDocument(SqlDocumentViewModel viewModel)
    {
        if (_clrEditor is null)
        {
            return;
        }

        // Set once when decompilation finishes, never toggled back and forth like Original/
        // Formatted - no caret/scroll preservation needed, unlike UpdateEditorDocument above.
        _clrEditor.Document = new TextDocument(viewModel.DecompiledClrSource ?? string.Empty);
    }

    private void UpdateEditorDocument(SqlDocumentViewModel viewModel)
    {
        if (_editor is null)
        {
            return;
        }

        var newText = viewModel.CurrentSqlText ?? string.Empty;

        // Replacing Document wholesale (rather than mutating its text) is what was resetting
        // scroll/caret on every Original/Formatted toggle - TextEditor.Document's setter means
        // "this is a different file now" and resets view state accordingly. Each SqlDocumentView
        // is 1:1 with one tab for its whole lifetime, so there's only ever one logical document
        // here; reusing the same TextDocument instance and updating its text is correct, not
        // just a workaround. ScrollOffset is captured/restored explicitly around the swap as a
        // guarantee, rather than relying on TextView happening to leave it alone on its own.
        if (_editor.Document is null)
        {
            _editor.Document = new TextDocument(newText);
            // Explicit rather than assumed - a freshly assigned Document is expected to start
            // with the caret at 0, but AvaloniaEdit's own TextEditor.Text setter resets it
            // explicitly too after a full-text replace, so this makes no assumptions either.
            _editor.CaretOffset = 0;
            _pendingCaretOffset = null;
            return;
        }

        var scrollOffset = _editor.TextArea.TextView.ScrollOffset;
        var hadFocus = _editor.TextArea.IsFocused;

        // An Original/Formatted toggle: OnViewModelPropertyChanging captured where the caret was
        // in the rendering we're leaving. Map it to the equivalent spot in the new text via the
        // canonicalization service's token position data; only fall back to blindly restoring the
        // old pixel scroll offset when no mapping was available (e.g. this script went through
        // one of the service's fallback formatting paths).
        int? mappedCaretOffset = null;
        if (_pendingCaretOffset is { } fromOffset)
        {
            mappedCaretOffset = viewModel.MapCaretOffset(fromOffset, _pendingCaretWasFormatted);
        }
        _pendingCaretOffset = null;

        _editor.Document.Text = newText;

        var editor = _editor;

        // Deferred: the document was just swapped wholesale, and AvaloniaEdit's TextView needs a
        // layout pass over the new content before caret placement/scroll-into-view and
        // (re)focusing land correctly - doing this synchronously here was silently dropping
        // keyboard focus out of the editor on every toggle (same class of timing issue as
        // SearchPanel.Open() needing a deferred Reactivate() call elsewhere in this app).
        Dispatcher.UIThread.Post(() =>
        {
            if (mappedCaretOffset is { } toOffset)
            {
                editor.CaretOffset = Math.Clamp(toOffset, 0, newText.Length);
                editor.TextArea.Caret.BringCaretToView();
            }
            else
            {
                editor.ScrollToHorizontalOffset(scrollOffset.X);
                editor.ScrollToVerticalOffset(scrollOffset.Y);
            }

            if (hadFocus)
            {
                editor.TextArea.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void ApplyGrammar(string? scopeName)
    {
        if (_textMateInstallation is null)
        {
            return;
        }

        _textMateInstallation.SetGrammar(string.IsNullOrWhiteSpace(scopeName)
            ? "source.sql"
            : scopeName);
    }

    private void ApplyTheme()
    {
        var themeName = ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;

        _textMateInstallation?.SetTheme(_registryOptions.LoadTheme(themeName));
        _clrTextMateInstallation?.SetTheme(_registryOptions.LoadTheme(themeName));
    }

    private void DisposeTextMate()
    {
        if (_trackedViewModel is not null)
        {
            _trackedViewModel.PropertyChanging -= OnViewModelPropertyChanging;
            _trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _trackedViewModel = null;
        }

        _textMateInstallation?.Dispose();
        _textMateInstallation = null;
        _clrTextMateInstallation?.Dispose();
        _clrTextMateInstallation = null;

        _searchPanel?.Uninstall();
        _searchPanel = null;
        _clrSearchPanel?.Uninstall();
        _clrSearchPanel = null;
        _openFindTarget = null;
    }
}
