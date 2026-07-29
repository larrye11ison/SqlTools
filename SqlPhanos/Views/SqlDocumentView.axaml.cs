using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private TextEditor? _editor;
    private TextMateHost.Installation? _textMateInstallation;
    private SearchPanel? _searchPanel;
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
        if (_searchPanel is null)
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
        if (_searchPanel.IsClosed)
        {
            _searchPanel.Open();
        }

        Dispatcher.UIThread.Post(() => _searchPanel.Reactivate(), DispatcherPriority.Input);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<TextEditor>("Editor");
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
    }

    private void EnsureTextMateInstalled()
    {
        if (_editor is null)
        {
            return;
        }

        _searchPanel ??= SearchPanel.Install(_editor);

        if (_textMateInstallation is not null)
        {
            return;
        }

        _textMateInstallation = TextMateHost.InstallTextMate(
            _editor,
            _registryOptions,
            true,
            ex => System.Diagnostics.Debug.WriteLine($"TextMate initialization error: {ex.Message}"));

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
        }
        else
        {
            _editor.Document = new TextDocument();
            ApplyGrammar("source.sql");
        }
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
        if (_textMateInstallation is null)
        {
            return;
        }

        var themeName = ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;

        _textMateInstallation.SetTheme(_registryOptions.LoadTheme(themeName));
    }

    private void DisposeTextMate()
    {
        if (_trackedViewModel is not null)
        {
            _trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _trackedViewModel = null;
        }

        _textMateInstallation?.Dispose();
        _textMateInstallation = null;

        _searchPanel?.Uninstall();
        _searchPanel = null;
    }
}
