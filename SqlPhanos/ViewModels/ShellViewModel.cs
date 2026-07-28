using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.Docking;
using SqlPhanos.Enums;
using SqlPhanos.Messages;
using SqlPhanos.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SqlPhanos.ViewModels;

public partial class ShellViewModel : ObservableObject, IRecipient<OpenDocumentMessage>, IRecipient<StatusMessage>, IRecipient<ResultsViewModeChangedMessage>
{
    private readonly DockFactory _dockFactory;

    [ObservableProperty]
    private IFactory? _factory;

    [ObservableProperty]
    private IRootDock? _layout;

    [ObservableProperty]
    private string _statusMessage = "Ready. Add a connection to start searching.";

    public string AppTitle => $"SqlPhanos v{AppVersionService.GetDisplayVersion()} - SQL Server Object Search";

    public ShellViewModel()
    {
        Debug.WriteLine("ShellViewModel constructor called");

        _dockFactory = new DockFactory(this);
        Factory = _dockFactory;
        Layout = _dockFactory.CreateLayout();
        if (Layout is not null)
        {
            _dockFactory.InitLayout(Layout);
        }

        WeakReferenceMessenger.Default.Register<OpenDocumentMessage>(this);
        WeakReferenceMessenger.Default.Register<StatusMessage>(this);
        WeakReferenceMessenger.Default.Register<ResultsViewModeChangedMessage>(this);

        // Both results dockables already start in the correct place by construction (card in
        // SearchResultsDock, grid dock left empty - see DockFactory.CreateLayout), matching
        // SearchViewModel.ResultsAsGrid's default of false, so this is a no-op today. Calling
        // it anyway keeps startup state and toggled state going through the exact same code
        // path, so there's only one place that can ever be wrong.
        ApplyResultsViewMode(SearchViewModel.ResultsAsGrid);
    }

    public SearchViewModel SearchViewModel => _dockFactory.SearchViewModel;

    public void Receive(OpenDocumentMessage message)
    {
        AddDocument(message.Value);
    }

    public void Receive(StatusMessage message)
    {
        StatusMessage = message.Value;
    }

    public void Receive(ResultsViewModeChangedMessage message)
    {
        ApplyResultsViewMode(message.Value);
    }

    // Bound to Ctrl+R (see ShellView) - just flips the same property the checkbox is bound to,
    // so ApplyResultsViewMode always runs through the one path regardless of which triggered it.
    public void ToggleResultsGrid()
    {
        SearchViewModel.ResultsAsGrid = !SearchViewModel.ResultsAsGrid;
    }

    // Card and grid results are shown/hidden by toggling whether the *whole ToolDock* (with its
    // Tool content always intact inside it) is present in its parent's VisibleDockables -
    // confirmed against Dock.Avalonia's actual source (FactoryBase.Dockable.cs/FactoryBase.cs in
    // the wieslawsoltes/Dock repo) after two wrong guesses:
    //   - Pin/Unpin's "pinned" state means "stays docked and visible," not the reverse.
    //   - RemoveDockable(dockable, collapse: true) removing just the *inner* Tool item also
    //     structurally removes the now-empty ToolDock from ITS OWN parent (CollapseDock) with no
    //     symmetric "restore" call, orphaning a stored ToolDock reference after the first toggle
    //     - which is why unchecking the box after checking it left nothing visible at all.
    // Toggling the container itself sidesteps both: RemoveDockable(container, true) is safe here
    // because LeftPanel/OuterLayout never become empty themselves (Search/Documents remain), and
    // showing it again just re-adds the same never-touched container - only the splitter (which
    // RemoveDockable's cleanup discards, with no automatic counterpart on the way back in) needs
    // to be manually recreated.
    private void ApplyResultsViewMode(bool asGrid)
    {
        if (Factory is null)
        {
            return;
        }

        if (asGrid)
        {
            HideContainer(_dockFactory.LeftPanel, _dockFactory.SearchResultsDock);
            ShowContainer(_dockFactory.OuterLayout, _dockFactory.SearchResultsGridDock);
        }
        else
        {
            HideContainer(_dockFactory.OuterLayout, _dockFactory.SearchResultsGridDock);
            ShowContainer(_dockFactory.LeftPanel, _dockFactory.SearchResultsDock);
        }

        ResetBaselineProportions();
    }

    // Whichever of SearchDock/MainLayoutDock was just left alone in its parent had its
    // Proportion rewritten to 1.0 by ProportionalStackPanel's renormalization (see
    // DockFactory.SearchDock's doc comment). Confirmed against Dock.Avalonia's actual theme XAML
    // (ProportionalDockControl.axaml in the Dock repo) that IDockable.CollapsedProportion - a
    // SEPARATE model property from Proportion - is *also* two-way bound to the panel, and
    // ProportionManager.HandleCollapsedChildren prefers it over Proportion whenever it holds a
    // non-NaN value, which it always does here (every layout pass writes the just-computed value
    // into it, since nothing in this app uses the soft IsCollapsed-flag collapse). Resetting only
    // Proportion (as a first attempt) did nothing, because CollapsedProportion kept overriding it
    // right back - both have to be reset for this to actually stick.
    private void ResetBaselineProportions()
    {
        _dockFactory.SearchDock.Proportion = 0.35;
        _dockFactory.SearchDock.CollapsedProportion = 0.35;
        _dockFactory.SearchResultsDock.Proportion = 0.65;
        _dockFactory.SearchResultsDock.CollapsedProportion = 0.65;
        _dockFactory.MainLayoutDock.Proportion = double.NaN;
        _dockFactory.MainLayoutDock.CollapsedProportion = double.NaN;
        _dockFactory.SearchResultsGridDock.Proportion = 0.3;
        _dockFactory.SearchResultsGridDock.CollapsedProportion = 0.3;
    }

    private void HideContainer(IDock parent, IDockable container)
    {
        if (parent.VisibleDockables?.Contains(container) != true)
        {
            return;
        }

        Factory!.RemoveDockable(container, collapse: true);
    }

    private void ShowContainer(IDock parent, IDockable container)
    {
        if (parent.VisibleDockables?.Contains(container) == true)
        {
            return;
        }

        Factory!.AddDockable(parent, new ProportionalDockSplitter());
        Factory!.AddDockable(parent, container);
    }

    private void AddDocument(Document document)
    {
        if (Layout is null || Factory is null) return;

        document.Context = document;
        // No floating/dragging-between-docks for anything in this app, documents included.
        document.CanFloat = false;
        document.CanDrag = false;
        document.CanDrop = false;

        var documentDock = FindDocumentDock(Layout);
        if (documentDock is not null)
        {
            Factory.AddDockable(documentDock, document);
            Factory.SetActiveDockable(document);
            Factory.SetFocusedDockable(documentDock, document);
        }
    }

    [RelayCommand]
    private void ExecuteShortcut(ApplicationShortcut shortcut)
    {
        if (shortcut == ApplicationShortcut.NextDocument || shortcut == ApplicationShortcut.PreviousDocument)
        {
            NavigateDocuments(shortcut == ApplicationShortcut.NextDocument);
            return;
        }

        IDockable? context = null;
        if (Layout is IRootDock root)
        {
            context = root.ActiveDockable;
        }

        Debug.WriteLine($"Executing shortcut: {shortcut} Context: {context?.Title ?? "None"}");
        WeakReferenceMessenger.Default.Send(new ApplicationShortcutMessage(shortcut, context));
    }

    public void CloseActiveDocument()
    {
        if (Layout is null || Factory is null) return;

        var documentDock = FindDocumentDock(Layout);
        if (documentDock?.ActiveDockable is IDockable activeDockable)
        {
            Factory.CloseDockable(activeDockable);
        }
    }

    private void NavigateDocuments(bool next)
    {
        if (Layout is null || Factory is null) return;

        var documentDock = FindDocumentDock(Layout);
        if (documentDock is not null && documentDock.VisibleDockables?.Count > 1)
        {
            var index = -1;
            if (documentDock.ActiveDockable != null)
            {
                index = documentDock.VisibleDockables.IndexOf(documentDock.ActiveDockable);
            }

            if (index == -1)
            {
                if (documentDock.VisibleDockables.Count > 0)
                {
                    Factory.SetActiveDockable(documentDock.VisibleDockables[0]);
                }
                return;
            }

            int newIndex;
            if (next)
            {
                newIndex = (index + 1) % documentDock.VisibleDockables.Count;
            }
            else
            {
                newIndex = (index - 1 + documentDock.VisibleDockables.Count) % documentDock.VisibleDockables.Count;
            }

            Factory.SetActiveDockable(documentDock.VisibleDockables[newIndex]);
        }
    }

    private IDocumentDock? FindDocumentDock(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock && documentDock.Id == "Documents")
        {
            return documentDock;
        }

        if (dockable is IDock dock)
        {
            if (dock.VisibleDockables is not null)
            {
                foreach (var visible in dock.VisibleDockables)
                {
                    var result = FindDocumentDock(visible);
                    if (result is not null) return result;
                }
            }
        }

        return null;
    }
}