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
            var searchDock = new ToolDock
            {
                Id = "SearchDock",
                Title = "Search",
                Proportion = 0.25,
                ActiveDockable = _searchViewModel,
                VisibleDockables = CreateList<IDockable>(_searchViewModel)
            };

            var searchResultsDock = new ToolDock
            {
                Id = "SearchResultsDock",
                Title = "Search Results",
                Proportion = 0.25,
                ActiveDockable = _searchResultsViewModel,
                VisibleDockables = CreateList<IDockable>(_searchResultsViewModel)
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

            var topLayout = new ProportionalDock
            {
                Id = "TopLayout",
                Orientation = Orientation.Horizontal,
                Proportion = double.NaN,
                VisibleDockables = CreateList<IDockable>
                (
                    searchDock,
                    new ProportionalDockSplitter(),
                    documentDock
                )
            };

            var mainLayout = new ProportionalDock
            {
                Id = "MainLayout",
                Title = "MainLayout",
                Proportion = double.NaN,
                Orientation = Orientation.Vertical,
                ActiveDockable = null,
                VisibleDockables = CreateList<IDockable>
                (
                    topLayout,
                    new ProportionalDockSplitter(),
                    searchResultsDock
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
                ["SqlDocumentViewModel"] = () => new SqlDocumentViewModel(),
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