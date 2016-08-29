﻿using Caliburn.Micro;
using SqlTools.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Data;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;

namespace SqlTools.DatabaseConnections
{
    /// <summary>
    /// Manages a list of DB connections.
    /// </summary>
    [Export]
    public class DatabaseConnectionsViewModel : Conductor<SqlConnectionViewModel>.Collection.OneActive, IHandle<FontFamily>
    {
        private const string storageFileName = "connections.xml";

        [Import]
        private IEventAggregator eventAggregator = null;

        private FontFamily font = null;

        public System.Windows.Input.ICommand CloseCommand
        {
            get { return null; }
        }

        public bool IsVisible { get; set; }

        public void AddNewConnection()
        {
            var newvm = IoC.Get<SqlConnectionViewModel>();
            Items.Add(newvm);
            ActivateItem(newvm);
        }

        public void ConnectionsSaveToStorage()
        {
            try
            {
                var storageScope = IsolatedStorageScope.Assembly | IsolatedStorageScope.User;
                using (var store = IsolatedStorageFile.GetStore(storageScope, null, null))
                {
                    if (store.FileExists(storageFileName))
                    {
                        store.DeleteFile(storageFileName);
                    }

                    // make sure at least one conx is in the collection
                    if (Items.Count == 0)
                    {
                        var newvm = IoC.Get<SqlConnectionViewModel>();
                        newvm.ServerAndInstance = "SERVER";
                        Items.Add(newvm);
                    }
                    using (var file = store.OpenFile(storageFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                    {
                        XmlSerializer ser = new XmlSerializer(typeof(Settings.ApplicationSettings));
                        var settings = new Settings.ApplicationSettings();
                        var ff = this.font ?? new FontFamily("Consolas");
                        settings.FontFamilyName = ff.Source;
                        settings.Connections = Items.Select(i => AutoMapper.Mapper.Map(i, new SqlConnectionDto())).ToArray();
                        ser.Serialize(file, settings);
                    }
                }
            }
            catch (Exception e)
            {
                eventAggregator.PublishOnCurrentThread(new ShellMessage { MessageText = e.ToString(), Severity = Severity.Warning });
            }
        }

        public void DeleteConnection(SqlConnectionViewModel cnvm)
        {
            var mbr = MessageBox.Show("Delete Connection?", "Are you sure you want to delete the connection?", MessageBoxButton.OKCancel);
            if (mbr == MessageBoxResult.OK)
            {
                Items.Remove(cnvm);
            }
        }

        /// <summary>
        /// Message published when user picks a new font from the Font Chooser dialogue.
        /// </summary>
        /// <param name="message"></param>
        public void Handle(FontFamily message)
        {
            this.font = message;
        }

        protected override void OnDeactivate(bool close)
        {
            ConnectionsSaveToStorage();
            base.OnDeactivate(close);
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            eventAggregator.Subscribe(this);

            AutoMapper.Mapper.Initialize(cfg =>
            {
                cfg.CreateMap<SqlConnectionViewModel, SqlConnectionDto>();
                cfg.CreateMap<SqlConnectionDto, SqlConnectionViewModel>();
                cfg.CreateMissingTypeMaps = true;
            });

            foreach (var item in ConnectionsLoadFromStorage())
            {
                Items.Add(item);
            }
            ActiveItem = Items.FirstOrDefault();
        }

        protected override void OnViewLoaded(object view)
        {
            base.OnViewLoaded(view);
            IsVisible = true;
        }

        private IEnumerable<SqlConnectionViewModel> ConnectionsLoadFromStorage()
        {
            var vmcoll = new List<SqlConnectionViewModel>();
            Settings.ApplicationSettings appSettings = null;
            try
            {
                using (var store = IsolatedStorageFile.GetStore(IsolatedStorageScope.Assembly | IsolatedStorageScope.User, null, null))
                {
                    using (var file = store.OpenFile(storageFileName, FileMode.Open, FileAccess.Read))
                    {
                        var ser = new XmlSerializer(typeof(Settings.ApplicationSettings));
                        appSettings = (Settings.ApplicationSettings)ser.Deserialize(file);
                    }
                }
                // guarantee that we have at least ONE connection, even if it's fake
                if (appSettings.Connections == null || appSettings.Connections.Length == 0)
                {
                    var placeholderConnections = new SqlConnectionDto[] { new SqlConnectionDto { ServerAndInstance = "SERVER" } };
                    appSettings.Connections = placeholderConnections;
                }
                foreach (var item in appSettings.Connections)
                {
                    SqlConnectionViewModel vm = IoC.Get<SqlConnectionViewModel>();
                    AutoMapper.Mapper.Map(item, vm);
                    vmcoll.Add(vm);
                }

                // make sure the stuff handling the SQL Code knows which font to use
                var fam = new FontFamily(appSettings.FontFamilyName);
                eventAggregator.PublishOnCurrentThread(fam);
            }
            catch (Exception e)
            {
                eventAggregator.PublishOnCurrentThread(new ShellMessage { MessageText = e.ToString(), Severity = Severity.Warning });
                vmcoll.Clear();
                var newvm = IoC.Get<SqlConnectionViewModel>();
                newvm.ServerAndInstance = "SERVER";
                vmcoll.Add(newvm);
            }
            return vmcoll;
        }
    }
}