using System;
using System.Collections.Generic;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Docking
{
    public class DockFactory : Factory
    {
        private readonly object _context;

        public DockFactory(object context)
        {
            _context = context;
            // CanClose = false throughout: these tool panes have no way to be reopened once
            // closed (no menu/command recreates them), so the only supported way to get one out
            // of the way is to un-pin it (collapses it, still recoverable by pinning again) -
            // never to close it outright. CanFloat/CanDrag/CanDrop = false disables detaching
            // into a floating window and dragging a pane into a different dock entirely - this
            // app wants a fixed layout, not a freely rearrangeable one.
            SearchViewModel = new SearchViewModel { CanClose = false, CanFloat = false, CanDrag = false, CanDrop = false };
            SearchResultsViewModel = new SearchResultsViewModel { CanClose = false, CanFloat = false, CanDrag = false, CanDrop = false };
            SearchResultsGridViewModel = new SearchResultsGridViewModel(SearchResultsViewModel) { CanClose = false, CanFloat = false, CanDrag = false, CanDrop = false };
        }

        public SearchViewModel SearchViewModel { get; }

        public SearchResultsViewModel SearchResultsViewModel { get; }

        public SearchResultsGridViewModel SearchResultsGridViewModel { get; }

        // Exposed so ShellViewModel can toggle which of the two results ToolDocks is actually
        // present in its parent's VisibleDockables (see ApplyResultsViewMode). Neither
        // Dock.Avalonia's pin/auto-hide mechanism (its "pinned" state means the opposite of what
        // it sounds like) nor removing/re-adding just the *inner* Tool item (RemoveDockable's
        // collapse:true structurally removes the now-empty ToolDock from LeftPanel/OuterLayout,
        // with no symmetric "restore" call, orphaning the stored ToolDock reference after the
        // first toggle) turned out to work - toggling the ToolDock container itself, with its
        // Tool content always intact inside it, is what actually behaves predictably.
        public IDock SearchResultsDock { get; private set; } = null!;

        public IDock SearchResultsGridDock { get; private set; } = null!;

        public IDock LeftPanel { get; private set; } = null!;

        public IDock OuterLayout { get; private set; } = null!;

        // Exposed purely so ShellViewModel can reset these back to their baseline Proportion
        // after every card/grid toggle. ProportionalStackPanel (the actual Avalonia control
        // doing the layout, see Dock.Controls.ProportionalStackPanel/Internal/ProportionManager.cs
        // in the Dock repo) renormalizes proportions to sum to 1.0 among whatever children are
        // *currently present* - when SearchResultsDock/SearchResultsGridDock is removed, whichever
        // of these two becomes the sole remaining child gets rewritten to 1.0, and that inflated
        // value then throws off the next normalization once the sibling comes back, compounding
        // further on every subsequent toggle. Resetting both sides back to their known-good
        // values after every toggle stops that drift from accumulating.
        public IDock SearchDock { get; private set; } = null!;

        public IDock MainLayoutDock { get; private set; } = null!;

        public override IRootDock CreateLayout()
        {
            // Cast to IDockable for Dock.Model compatibility
            var searchViewModelDockable = (IDockable)SearchViewModel;
            var searchResultsViewModelDockable = (IDockable)SearchResultsViewModel;

            var searchDock = new ToolDock
            {
                Id = "SearchDock",
                Title = "Search",
                Proportion = 0.35,
                // The proportional split with Search Results below it must not be able to
                // shrink this below the point where its own fields/Search button get clipped
                // with no way to scroll to them. IDockable.MinHeight is a plain declarative
                // floor on the proportional layout, same idea as a WPF row's MinHeight.
                MinHeight = 320,
                CanFloat = false,
                CanDrag = false,
                CanDrop = false,
                ActiveDockable = searchViewModelDockable,
                VisibleDockables = CreateList<IDockable>(searchViewModelDockable)
            };
            SearchDock = searchDock;

            var searchResultsDock = new ToolDock
            {
                Id = "SearchResultsDock",
                Title = "Search Results",
                Proportion = 0.65,
                CanFloat = false,
                CanDrag = false,
                CanDrop = false,
                ActiveDockable = searchResultsViewModelDockable,
                VisibleDockables = CreateList<IDockable>(searchResultsViewModelDockable)
            };
            SearchResultsDock = searchResultsDock;

            var documentDock = new DocumentDock
            {
                Id = "Documents",
                Title = "Documents",
                IsCollapsable = false,
                Proportion = double.NaN,
                CanFloat = false,
                CanDrag = false,
                CanDrop = false,
                ActiveDockable = null,
                VisibleDockables = CreateList<IDockable>()
            };

            // Object Search stacked directly above Search Results in one narrow left-hand
            // panel, rather than Search Results spanning the full window width along the
            // bottom - the results list is meant to be read vertically, not scanned as wide
            // columns.
            var leftPanel = new ProportionalDock
            {
                Id = "LeftPanel",
                Title = "LeftPanel",
                Orientation = Orientation.Vertical,
                Proportion = 0.22,
                CanFloat = false,
                CanDrag = false,
                CanDrop = false,
                VisibleDockables = CreateList<IDockable>
                (
                    searchDock,
                    new ProportionalDockSplitter(),
                    searchResultsDock
                )
            };
            LeftPanel = leftPanel;

            var mainLayout = new ProportionalDock
            {
                Id = "MainLayout",
                Title = "MainLayout",
                Proportion = double.NaN,
                Orientation = Orientation.Horizontal,
                CanFloat = false,
                CanDrag = false,
                CanDrop = false,
                ActiveDockable = null,
                VisibleDockables = CreateList<IDockable>
                (
                    leftPanel,
                    new ProportionalDockSplitter(),
                    documentDock
                )
            };
            MainLayoutDock = mainLayout;

            // The grid results view gets its own dock, docked at the bottom of the *entire*
            // window (a sibling of mainLayout, not nested inside leftPanel/mainLayout's
            // horizontal split) so it spans the full app width - more horizontal room for a
            // wide datagrid matters more here than extra height for the left search panel.
            // Built with its Tool content already inside it (unlike searchResultsDock/
            // searchResultsGridDock, this content never gets removed - what toggles is whether
            // the *whole ToolDock* is present in outerLayout.VisibleDockables at all, see
            // ShellViewModel.ApplyResultsViewMode). It's deliberately left OUT of outerLayout's
            // initial VisibleDockables below, matching "Results as grid" being unchecked by
            // default - ApplyResultsViewMode adds it in (with a splitter) the first time the
            // user switches to grid mode.
            var searchResultsGridDockable = (IDockable)SearchResultsGridViewModel;
            var searchResultsGridDock = new ToolDock
            {
                Id = "SearchResultsGridDock",
                Title = "Search Results (Grid)",
                Proportion = 0.3,
                CanFloat = false,
                CanDrag = false,
                CanDrop = false,
                ActiveDockable = searchResultsGridDockable,
                VisibleDockables = CreateList<IDockable>(searchResultsGridDockable)
            };
            SearchResultsGridDock = searchResultsGridDock;

            var outerLayout = new ProportionalDock
            {
                Id = "OuterLayout",
                Title = "OuterLayout",
                Proportion = double.NaN,
                Orientation = Orientation.Vertical,
                CanFloat = false,
                CanDrag = false,
                CanDrop = false,
                VisibleDockables = CreateList<IDockable>(mainLayout)
            };
            OuterLayout = outerLayout;

            var rootDock = new RootDock
            {
                Id = "Root",
                Title = "Root",
                CanFloat = false,
                CanDrag = false,
                CanDrop = false,
                ActiveDockable = outerLayout,
                DefaultDockable = outerLayout,
                VisibleDockables = CreateList<IDockable>(outerLayout)
            };

            return rootDock;
        }

        public override void InitLayout(IDockable layout)
        {
            ContextLocator = new Dictionary<string, Func<object?>>
            {
                ["SearchViewModel"] = () => SearchViewModel,
                ["SearchResultsViewModel"] = () => SearchResultsViewModel,
                ["SearchResultsGridViewModel"] = () => SearchResultsGridViewModel,
                ["ShellViewModel"] = () => _context
            };

            DockableLocator = new Dictionary<string, Func<IDockable?>>
            {
            };

            HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
            {
                [nameof(IDockWindow)] = () => null
            };

            base.InitLayout(layout);
        }
    }
}
