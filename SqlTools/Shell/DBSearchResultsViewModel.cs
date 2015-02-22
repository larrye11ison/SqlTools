﻿using Caliburn.Micro;
using LinqKit;
using SqlTools.DatabaseConnections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics.Contracts;
using System.Linq;

namespace SqlTools.Shell
{
    /// <summary>
    /// The results of the initial DB Object search from the server.
    /// </summary>
    [Export]
    public class DBSearchResultsViewModel : Screen,
        IHandle<EnumerateObjectsInDatabaseMessage>,
        IHandle<ObjectEnumerationStartingMessage>,
        IHandle<ClearDBObjectsResultsMessage>
    {
        private readonly List<DBObjectViewModel> actualList = new List<DBObjectViewModel>();

        private readonly IEventAggregator eventAgg;

        private readonly object gawd = new object();

        [ImportingConstructor]
        public DBSearchResultsViewModel(IEventAggregator eventAggregator)
        {
            Contract.Ensures(DatabaseObjects != null);
            DatabaseObjects = new BindableCollection<DBObjectViewModel>();
            eventAgg = eventAggregator;
            eventAggregator.Subscribe(this);
        }

        public System.Windows.Input.ICommand CloseCommand
        {
            get { return null; }
        }

        public BindableCollection<DBObjectViewModel> DatabaseObjects { get; private set; }

        public bool IsVisible { get; set; }

        public string ResultsFilter { get; set; }

        public static void ScriptTheObject(DBObjectViewModel vm)
        {
            vm.ScriptObject();
        }

        public void ClearResults()
        {
            lock (gawd)
            {
                actualList.Clear();
                ExecuteFilter();
            }
        }

        public void Handle(EnumerateObjectsInDatabaseMessage dbMessage)
        {
            Contract.Requires(dbMessage != null, "dbMessage is null");
            Contract.Requires(dbMessage.DBObjects != null, "dbMessage.DBObjects is null");

            var newDBObjects = dbMessage.DBObjects;
            var connectionForNewObjects = dbMessage.ConnectionViewModel;

            if (newDBObjects == null)
            {
                return;
            }
            lock (gawd)
            {
                foreach (var item in newDBObjects)
                {
                    // don't add it if it's already there
                    if (actualList.Any(dbo => dbo.SysObject.Equals(item)) == false)
                    {
                        var shell = IoC.Get<IShell>();
                        var newDBObjectViewModel = new DBObjectViewModel(item, connectionForNewObjects, eventAgg, shell);
                        actualList.Add(newDBObjectViewModel);
                    }
                }
            }
            ExecuteFilter();
        }

        public void Handle(ObjectEnumerationStartingMessage message)
        {
            actualList.Clear();
        }

        public void Handle(ClearDBObjectsResultsMessage message)
        {
            ClearResults();
        }

        public void ResultsFilterChanged(string filterText)
        {
            ExecuteFilter();
        }

        protected override void OnViewLoaded(object view)
        {
            base.OnViewLoaded(view);
            IsVisible = true;
        }

        private void ExecuteFilter()
        {
            lock (gawd)
            {
                IEnumerable<DBObjectViewModel> filteredList;
                if (string.IsNullOrWhiteSpace(ResultsFilter))
                {
                    // if there's no filter, just use the plain ol' list
                    filteredList = actualList;
                }
                else
                {
                    var pred = DBObjectSearch.BuildPredicateFromSearchText(ResultsFilter);

                    filteredList = from item in actualList
                                   where pred.Invoke(item)
                                   select item;
                }
                foreach (var found in filteredList)
                {
                    DatabaseObjects.Add(found);
                }
                Execute.OnUIThread(() =>
                {
                    DatabaseObjects.Clear();
                    DatabaseObjects.AddRange(filteredList);
                });
            }
        }
    }
}