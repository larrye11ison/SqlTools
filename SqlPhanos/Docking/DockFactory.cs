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
        private readonly SearchViewModel _searchViewModel;
        private readonly SearchResultsViewModel _searchResultsViewModel;

        public DockFactory(object context)
        {
            _context = context;
            _searchViewModel = new SearchViewModel();
            _searchResultsViewModel = new SearchResultsViewModel();
        }

        public override IRootDock CreateLayout()
        {
            // Cast to IDockable for Dock.Model compatibility
            var searchViewModelDockable = (IDockable)_searchViewModel;
            var searchResultsViewModelDockable = (IDockable)_searchResultsViewModel;

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
                ActiveDockable = searchViewModelDockable,
                VisibleDockables = CreateList<IDockable>(searchViewModelDockable)
            };

            var searchResultsDock = new ToolDock
            {
                Id = "SearchResultsDock",
                Title = "Search Results",
                Proportion = 0.65,
                ActiveDockable = searchResultsViewModelDockable,
                VisibleDockables = CreateList<IDockable>(searchResultsViewModelDockable)
            };

            var documentDock = new DocumentDock
            {
                Id = "Documents",
                Title = "Documents",
                IsCollapsable = false,
                Proportion = double.NaN,
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
                VisibleDockables = CreateList<IDockable>
                (
                    searchDock,
                    new ProportionalDockSplitter(),
                    searchResultsDock
                )
            };

            var mainLayout = new ProportionalDock
            {
                Id = "MainLayout",
                Title = "MainLayout",
                Proportion = double.NaN,
                Orientation = Orientation.Horizontal,
                ActiveDockable = null,
                VisibleDockables = CreateList<IDockable>
                (
                    leftPanel,
                    new ProportionalDockSplitter(),
                    documentDock
                )
            };

            var rootDock = new RootDock
            {
                Id = "Root",
                Title = "Root",
                ActiveDockable = mainLayout,
                DefaultDockable = mainLayout,
                VisibleDockables = CreateList<IDockable>(mainLayout)
            };

            return rootDock;
        }

        public override void InitLayout(IDockable layout)
        {
            ContextLocator = new Dictionary<string, Func<object?>>
            {
                ["SearchViewModel"] = () => _searchViewModel,
                ["SearchResultsViewModel"] = () => _searchResultsViewModel,
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