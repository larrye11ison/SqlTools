using Caliburn.Micro;
using SqlTools.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Data;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        public DatabaseConnectionsViewModel()
        {
        }

        public System.Windows.Input.ICommand CloseCommand
        {
            get { return null; }
        }

        public bool IsVisible { get; set; }

        public void AddNewConnection()
        {
            var newvm = IoC.Get<SqlConnectionViewModel>();
            Items.Add(newvm);
            _ = ActivateItemAsync(newvm, CancellationToken.None);
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
                        // Use manual mapping to convert to DTOs
                        settings.Connections = Items.Select(ToDto).ToArray();
                        ser.Serialize(file, settings);
                    }
                }
            }
            catch (Exception e)
            {
                _ = eventAggregator.PublishOnUIThreadAsync(new ShellMessage { MessageText = e.ToString(), Severity = Severity.Warning });
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
        /// <param name="cancellationToken"></param>
        public Task HandleAsync(FontFamily message, CancellationToken cancellationToken)
        {
            this.font = message;
            return Task.CompletedTask;
        }

        protected override Task OnDeactivateAsync(bool close, CancellationToken cancellationToken)
        {
            ConnectionsSaveToStorage();
            return base.OnDeactivateAsync(close, cancellationToken);
        }

        protected override Task OnInitializedAsync(CancellationToken cancellationToken)
        {
            eventAggregator.SubscribeOnPublishedThread(this);

            foreach (var item in ConnectionsLoadFromStorage())
            {
                Items.Add(item);
            }
            ActiveItem = Items.FirstOrDefault();

            return base.OnInitializedAsync(cancellationToken);
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
                    // Use manual mapping to populate viewmodel from DTO
                    MapToViewModel(item, vm);
                    vmcoll.Add(vm);
                }

                // make sure the stuff handling the SQL Code knows which font to use
                var fam = new FontFamily(appSettings.FontFamilyName);
                _ = eventAggregator.PublishOnUIThreadAsync(fam);
            }
            catch (Exception e)
            {
                _ = eventAggregator.PublishOnUIThreadAsync(new ShellMessage { MessageText = e.ToString(), Severity = Severity.Warning });
                vmcoll.Clear();
                var newvm = IoC.Get<SqlConnectionViewModel>();
                newvm.ServerAndInstance = "SERVER";
                vmcoll.Add(newvm);
            }
            return vmcoll;
        }

        /// <summary>
        /// Converts a SqlConnectionViewModel to SqlConnectionDto for serialization.
        /// </summary>
        private static SqlConnectionDto ToDto(SqlConnectionViewModel vm)
        {
            return new SqlConnectionDto
            {
                CommandTimeout = vm.CommandTimeout,
                ObjectDefinitionQuery = vm.ObjectDefinitionQuery,
                ObjectNameQuery = vm.ObjectNameQuery,
                ObjectSchemaQuery = vm.ObjectSchemaQuery,
                ServerAndInstance = vm.ServerAndInstance,
                UserName = vm.UserName
            };
        }

        /// <summary>
        /// Maps properties from SqlConnectionDto to an existing SqlConnectionViewModel instance.
        /// </summary>
        private static void MapToViewModel(SqlConnectionDto dto, SqlConnectionViewModel vm)
        {
            vm.CommandTimeout = dto.CommandTimeout;
            vm.ObjectDefinitionQuery = dto.ObjectDefinitionQuery;
            vm.ObjectNameQuery = dto.ObjectNameQuery;
            vm.ObjectSchemaQuery = dto.ObjectSchemaQuery;
            vm.ServerAndInstance = dto.ServerAndInstance;
            vm.UserName = dto.UserName;
        }
    }
}