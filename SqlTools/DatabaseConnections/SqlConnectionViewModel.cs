using Caliburn.Micro;
using SqlTools.Data;
using SqlTools.Models.Shell;
using SqlTools.UI;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Data.SqlClient;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SqlTools.DatabaseConnections
{
    /// <summary>
    /// A single connection to a SQL Server DB.
    /// </summary>
    [Export]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class SqlConnectionViewModel : Screen
    {
        private IMainControlFocusable focusable = null;

        public SqlConnectionViewModel()
        {
            Databases = new ObservableCollection<DatabaseViewModel>();
            CalculateValidity();
            Status = ConnectionStatus.Dormant;
            //SearchAcrossAllDatabases = true;
        }

        public bool CanEnumerateDatabases
        {
            get
            {
                bool rv = IsValid && Status == ConnectionStatus.Dormant;
                return rv;
            }
        }

        public bool CanEnumerateObjects
        {
            get
            {
                bool rv = IsValid && Status == ConnectionStatus.Dormant;
                return rv;
            }
        }

        public bool ClearObjectsBeforeLoadingResults { get; set; }

        public int? CommandTimeout { get; set; }

        public ObservableCollection<DatabaseViewModel> Databases { get; private set; }

        public string ErrorMessage { get; set; }

        public Visibility ErrorVisibility
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ErrorMessage))
                {
                    return Visibility.Hidden;
                }
                return Visibility.Visible;
            }
        }

        [Import]
        public IEventAggregator EventAggregator { get; private set; }

        public bool IsEnabled
        {
            get
            {
                return Status != ConnectionStatus.Dormant;
            }
        }

        public bool IsUsingWindowsAuth
        {
            get
            {
                return IsValid && string.IsNullOrWhiteSpace(UserName) && string.IsNullOrWhiteSpace(Password);
            }
        }

        public bool IsValid { get; set; }

        public string ObjectDefinitionQuery { get; set; }

        public string ObjectNameQuery { get; set; }

        public string ObjectSchemaQuery { get; set; }

        public string Password { get; set; }

        public string ServerAndInstance { get; set; }

        public ConnectionStatus Status { get; set; }

        public string UserName { get; set; }

        public void CheckAllDatabases()
        {
            foreach (var item in this.Databases)
            {
                item.IsSelected = true;
            }
        }

        public string ConnectionString(string dbName = "master")
        {
            Contract.Requires(string.IsNullOrWhiteSpace(dbName) == false);
            var builder = new SqlConnectionStringBuilder();
            builder.ConnectTimeout = 500;
            builder.InitialCatalog = dbName;
            builder.DataSource = ServerAndInstance;

            if (IsUsingWindowsAuth)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.UserID = UserName;
                builder.Password = Password;
            }
            return builder.ConnectionString;
        }

        public async Task EnumerateDatabases()
        {
            try
            {
                ErrorMessage = "";
                Status = ConnectionStatus.GettingDatabases;
                var context = new Data.SchemaDBContext(this);
                var contextGetDatabases = await Data.SchemaDBContext.GetDatabases(this);
                Databases.Clear();
                foreach (var item in contextGetDatabases)
                {
                    item.IsSelected = true;
                    this.Databases.Add(item);
                }
            }
            catch (Exception e)
            {
                ErrorMessage = e.ToString();
            }
            finally
            {
                Status = ConnectionStatus.Dormant;
            }
        }

        public async void EnumerateObjects()
        {
            if (this.Databases == null || this.Databases.Count == 0)
            {
                Status = ConnectionStatus.GettingDatabases;
                await EnumerateDatabases();
            }
            ErrorMessage = "";
            try
            {
                Status = ConnectionStatus.SearchingForObjects;
                var ctx = new SchemaDBContext(this);
                //var dbeez = Databases.Where(db => SearchAcrossAllDatabases || db.IsSelected);
                var dbeez = Databases.Where(db => db.IsSelected);
                Status = ConnectionStatus.SearchingForObjects;
                if (ClearObjectsBeforeLoadingResults)
                {
                    this.EventAggregator.PublishOnUIThread(new ClearDBObjectsResultsMessage());
                }
                await ctx.EnumerateObjectsInDatabases(this, dbeez, ObjectNameQuery, ObjectSchemaQuery, ObjectDefinitionQuery, EventAggregator);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.ToString();
            }
            finally
            {
                Status = ConnectionStatus.Dormant;
            }
        }

        public void InitiateNewObjectSearchOnDatabase(bool clearSearchParameterFields = true)
        {
            if (clearSearchParameterFields)
            {
                ObjectNameQuery = "";
                ObjectDefinitionQuery = "";
                ObjectSchemaQuery = "";
            }
            if (focusable != null)
            {
                focusable.SetFocusOnMainControl();
            }
        }

        public void PasswordChanged(RoutedEventArgs rea)
        {
            Password = (rea.OriginalSource as PasswordBox).Password;
            NotifyOfPropertyChange(() => Password);
            CalculateValidity();
        }

        public void UncheckAllDatabases()
        {
            foreach (var item in this.Databases)
            {
                item.IsSelected = false;
            }
        }

        protected override void OnViewLoaded(object view)
        {
            base.OnViewLoaded(view);
            focusable = view as IMainControlFocusable;
        }

        private void CalculateValidity()
        {
            bool userBlank = string.IsNullOrWhiteSpace(UserName);
            bool passwordBlank = string.IsNullOrWhiteSpace(Password);

            if (userBlank && passwordBlank)
            {
                IsValid = true;
                return;
            }
            if (userBlank || passwordBlank)
            {
                IsValid = false;
            }
            else
            {
                IsValid = true;
            }
        }
    }
}