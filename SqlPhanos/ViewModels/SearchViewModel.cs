using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.Messages;
using SqlPhanos.ScriptDatabases;
using SqlPhanos.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SqlPhanos.ViewModels
{
	public partial class SearchViewModel : Tool, IRecipient<ScriptObjectRequestMessage>
	{
		private readonly SqlSearchService _searchService = new();
		private readonly ConnectionProfileStoreService _connectionProfileStoreService = new();

		[ObservableProperty]
		private ObservableCollection<SqlConnectionViewModel> _connections = new();

		[ObservableProperty]
		private string _definitionQuery = "";

		[ObservableProperty]
		private SqlConnectionViewModel _editingConnection = new();

		private bool _isAddingNew;

		[ObservableProperty]
		[NotifyPropertyChangedFor(nameof(CanSearch))]
		[NotifyCanExecuteChangedFor(nameof(SearchCommand))]
		private bool _isSearching;

		[ObservableProperty]
		private bool _isEditing;

		[ObservableProperty]
		private string _objectNameQuery = "";

		[ObservableProperty]
		private string _schemaQuery = "";

		[ObservableProperty]
		[NotifyPropertyChangedFor(nameof(CanDeleteSelectedConnection))]
		[NotifyPropertyChangedFor(nameof(CanEditSelectedConnection))]
		[NotifyPropertyChangedFor(nameof(CanSearch))]
		[NotifyPropertyChangedFor(nameof(CanOpenQuery))]
		[NotifyCanExecuteChangedFor(nameof(DeleteConnectionCommand))]
		[NotifyCanExecuteChangedFor(nameof(EditConnectionCommand))]
		[NotifyCanExecuteChangedFor(nameof(SearchCommand))]
		[NotifyCanExecuteChangedFor(nameof(OpenQueryXLeratorCommand))]
		[NotifyCanExecuteChangedFor(nameof(OpenScriptDatabasesCommand))]
		private SqlConnectionViewModel? _selectedConnection;

		[ObservableProperty]
		private bool _showDeleteConfirmation;

		public bool CanDeleteSelectedConnection => SelectedConnection is not null;

		public bool CanEditSelectedConnection => SelectedConnection is not null;

		public bool CanSearch => !IsSearching && SelectedConnection is not null;

		public bool CanOpenQuery => SelectedConnection is not null;

		public SearchViewModel()
		{
			Id = "Search";
			Title = "Search";

			LoadConnections();
			WeakReferenceMessenger.Default.Register<ScriptObjectRequestMessage>(this);
		}

		public void Receive(ScriptObjectRequestMessage message)
		{
			if (SelectedConnection == null)
			{
				PublishStatus("Select a connection before scripting an object.");
				return;
			}

			var result = message.Value;
			var doc = new SqlDocumentViewModel(result, SelectedConnection.ConnectionString, _searchService);
			WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(doc));
			PublishStatus($"Scripting {result.SchemaName}.{result.ObjectName}...");
		}

		[RelayCommand]
		private void AddConnection()
		{
			EditingConnection = new SqlConnectionViewModel { ServerAndInstance = "", UseWindowsAuth = true, TrustServerCertificate = true };
			_isAddingNew = true;
			IsEditing = true;
			PublishStatus("Enter SQL Server connection details.");
		}

		[RelayCommand]
		private void CancelDelete()
		{
			ShowDeleteConfirmation = false;
		}

		[RelayCommand]
		private void CancelEdit()
		{
			IsEditing = false;
			EditingConnection = new SqlConnectionViewModel();
			PublishStatus("Connection edit cancelled.");
		}

		[RelayCommand]
		private void ConfirmDelete()
		{
			if (SelectedConnection != null)
			{
				Connections.Remove(SelectedConnection);
				SelectedConnection = Connections.Count > 0 ? Connections[0] : null;
				SaveConnections();
				PublishStatus("Connection removed.");
			}
			ShowDeleteConfirmation = false;
		}

		[RelayCommand]
		private void DeleteConnection()
		{
			if (SelectedConnection != null)
			{
				ShowDeleteConfirmation = true;
			}
		}

		[RelayCommand]
		private void EditConnection()
		{
			if (SelectedConnection == null) return;

			EditingConnection = new SqlConnectionViewModel
			{
				ServerAndInstance = SelectedConnection.ServerAndInstance,
				UseWindowsAuth = SelectedConnection.UseWindowsAuth,
				UserName = SelectedConnection.UserName,
				Password = SelectedConnection.Password,
				TrustServerCertificate = SelectedConnection.TrustServerCertificate
			};
			_isAddingNew = false;
			IsEditing = true;
			PublishStatus($"Editing connection '{SelectedConnection.ServerAndInstance}'.");
		}

		[RelayCommand(CanExecute = nameof(CanOpenQuery))]
		private void OpenQueryXLerator()
		{
			if (SelectedConnection == null) return;

			var doc = new QueryXLeratorDocumentViewModel(SelectedConnection.ConnectionString, SelectedConnection.ServerAndInstance);
			WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(doc));
			PublishStatus($"Opened new query session against '{SelectedConnection.ServerAndInstance}'.");
		}

		[RelayCommand(CanExecute = nameof(CanOpenQuery))]
		private void OpenScriptDatabases()
		{
			if (SelectedConnection == null) return;

			var doc = new ScriptDatabasesDocumentViewModel(SelectedConnection.ConnectionString, SelectedConnection.ServerAndInstance);
			WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(doc));
			PublishStatus($"Opened database scripting session against '{SelectedConnection.ServerAndInstance}'.");
		}

		[RelayCommand]
		private void SaveConnection()
		{
			if (string.IsNullOrWhiteSpace(EditingConnection.ServerAndInstance))
			{
				PublishStatus("Server name is required.");
				return;
			}

			if (_isAddingNew)
			{
				Connections.Add(EditingConnection);
				SelectedConnection = EditingConnection;
				SaveConnections();
				PublishStatus($"Added connection '{EditingConnection.ServerAndInstance}'.");
			}
			else if (SelectedConnection != null)
			{
				SelectedConnection.ServerAndInstance = EditingConnection.ServerAndInstance;
				SelectedConnection.UseWindowsAuth = EditingConnection.UseWindowsAuth;
				SelectedConnection.UserName = EditingConnection.UserName;
				SelectedConnection.Password = EditingConnection.Password;
				SelectedConnection.TrustServerCertificate = EditingConnection.TrustServerCertificate;
				SaveConnections();
				PublishStatus($"Updated connection '{SelectedConnection.ServerAndInstance}'.");
			}

			IsEditing = false;
			EditingConnection = new SqlConnectionViewModel();
		}

		private void LoadConnections()
		{
			var savedConnections = _connectionProfileStoreService.LoadConnections();
			Connections = new ObservableCollection<SqlConnectionViewModel>(savedConnections);
			SelectedConnection = Connections.Count > 0 ? Connections[0] : null;
		}

		private void SaveConnections()
		{
			_connectionProfileStoreService.SaveConnections(Connections);
		}

		[RelayCommand]
		private async Task SearchAsync()
		{
			if (SelectedConnection == null)
			{
				PublishStatus("Add and select a connection before searching.");
				return;
			}

			if (string.IsNullOrWhiteSpace(SelectedConnection.ServerAndInstance))
			{
				PublishStatus("Selected connection is missing a server name.");
				return;
			}

			IsSearching = true;
			try
			{
				PublishStatus($"Searching server '{SelectedConnection.ServerAndInstance}'...");
				WeakReferenceMessenger.Default.Send(new SearchResultsMessage(new List<SearchResultViewModel>()));

				var connectionString = SelectedConnection.ConnectionString;
				var databases = await _searchService.GetDatabasesAsync(connectionString);
				var allResults = new ConcurrentBag<SearchResultViewModel>();
				var failedDatabases = new ConcurrentBag<string>();

				// Searching one database at a time here was a regression from the old SqlTools
				// app, which ran every selected database's search concurrently - reuses the same
				// concurrency cap Script Databases already uses (ScriptingDefaults) rather than
				// firing one task per database unbounded, since a connection can have far more
				// databases than are safe to query all at once.
				await Parallel.ForEachAsync(
					databases,
					new ParallelOptions { MaxDegreeOfParallelism = ScriptingDefaults.MaxConcurrentObjectScripts },
					async (db, cancellationToken) =>
					{
						try
						{
							var results = await _searchService.SearchDatabaseAsync(
								connectionString,
								db,
								ObjectNameQuery,
								SchemaQuery,
								DefinitionQuery);

							foreach (var result in results)
							{
								allResults.Add(result);
							}
						}
						catch (Exception ex)
						{
							failedDatabases.Add(db);
							System.Diagnostics.Debug.WriteLine($"Search error in database '{db}': {ex}");
						}
					});

				WeakReferenceMessenger.Default.Send(new SearchResultsMessage(allResults.ToList()));

				if (failedDatabases.Count > 0)
				{
					PublishStatus($"Found {allResults.Count} result(s). {failedDatabases.Count} database(s) failed.");
				}
				else if (allResults.Count == 0)
				{
					PublishStatus("Search completed. No matching objects found.");
				}
				else
				{
					PublishStatus($"Search completed. Found {allResults.Count} result(s).");
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"Search error: {ex}");
				PublishStatus($"Search failed: {ex.Message}");
			}
			finally
			{
				IsSearching = false;
			}
		}

		private static void PublishStatus(string status)
		{
			WeakReferenceMessenger.Default.Send(new StatusMessage(status));
		}
	}
}