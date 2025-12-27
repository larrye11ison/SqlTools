using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SqlPhanos.Enums;
using SqlPhanos.Messages;
using SqlPhanos.Services;
using Dock.Model.Mvvm.Controls;

namespace SqlPhanos.ViewModels;

public partial class SearchViewModel : Tool, IRecipient<ScriptObjectRequestMessage>
{
    private readonly SqlSearchService _searchService = new();

    [ObservableProperty]
    private ObservableCollection<SqlConnectionViewModel> _connections = new();

    [ObservableProperty]
    private string _definitionQuery = "";

    [ObservableProperty]
    private SqlConnectionViewModel? _editingConnection;

    private bool _isAddingNew;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _objectNameQuery = "";

    [ObservableProperty]
    private string _schemaQuery = "";

    [ObservableProperty]
    private SqlConnectionViewModel? _selectedConnection;

    [ObservableProperty]
    private bool _showDeleteConfirmation;

    public SearchViewModel()
    {
        Id = "Search";
        Title = "Search";

        // Add some dummy connections for testing
        Connections.Add(new SqlConnectionViewModel { ServerAndInstance = "LOCALHOST", UseWindowsAuth = true });
        Connections.Add(new SqlConnectionViewModel { ServerAndInstance = "PROD-DB-01", UseWindowsAuth = false, UserName = "sa" });

        SelectedConnection = Connections.Count > 0 ? Connections[0] : null;

        WeakReferenceMessenger.Default.Register<ScriptObjectRequestMessage>(this);
    }

    public void Receive(ScriptObjectRequestMessage message)
    {
        _ = ScriptObjectInternalAsync(message.Value);
    }

    [RelayCommand]
    private void AddConnection()
    {
        EditingConnection = new SqlConnectionViewModel { ServerAndInstance = "New Server", UseWindowsAuth = true, TrustServerCertificate = true };
        _isAddingNew = true;
        IsEditing = true;
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
        EditingConnection = null;
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (SelectedConnection != null)
        {
            Connections.Remove(SelectedConnection);
            SelectedConnection = Connections.Count > 0 ? Connections[0] : null;
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

        // Clone the selected connection for editing
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
    }

    [RelayCommand]
    private void SaveConnection()
    {
        if (EditingConnection == null) return;

        if (_isAddingNew)
        {
            Connections.Add(EditingConnection);
            SelectedConnection = EditingConnection;
        }
        else if (SelectedConnection != null)
        {
            // Update existing connection
            SelectedConnection.ServerAndInstance = EditingConnection.ServerAndInstance;
            SelectedConnection.UseWindowsAuth = EditingConnection.UseWindowsAuth;
            SelectedConnection.UserName = EditingConnection.UserName;
            SelectedConnection.Password = EditingConnection.Password;
            SelectedConnection.TrustServerCertificate = EditingConnection.TrustServerCertificate;
        }

        IsEditing = false;
        EditingConnection = null;
    }

    private async Task ScriptObjectInternalAsync(SearchResultViewModel result)
    {
        if (SelectedConnection == null) return;

        try
        {
            var script = await _searchService.ScriptObjectAsync(SelectedConnection.ConnectionString, result);

            var doc = new SqlDocumentViewModel(
                result.ObjectName, // FilePath placeholder
                script,
                $"{result.SchemaName}.{result.ObjectName}");

            WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(doc));
        }
        catch (Exception ex)
        {
            // Handle error
            System.Diagnostics.Debug.WriteLine($"Scripting error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (SelectedConnection == null || IsSearching) return;

        IsSearching = true;
        try
        {
            // Clear existing results
            WeakReferenceMessenger.Default.Send(new SearchResultsMessage(new List<SearchResultViewModel>()));

            var connectionString = SelectedConnection.ConnectionString;
            var databases = await _searchService.GetDatabasesAsync(connectionString);

            var allResults = new List<SearchResultViewModel>();

            foreach (var db in databases)
            {
                try
                {
                    var results = await _searchService.SearchDatabaseAsync(
                        connectionString,
                        db,
                        ObjectNameQuery,
                        SchemaQuery,
                        DefinitionQuery);

                    allResults.AddRange(results);
                }
                catch (Exception)
                {
                    // Log or handle individual database search failure
                }
            }

            WeakReferenceMessenger.Default.Send(new SearchResultsMessage(allResults));
        }
        catch (Exception ex)
        {
            // Handle connection or general errors
            System.Diagnostics.Debug.WriteLine($"Search error: {ex.Message}");
        }
        finally
        {
            IsSearching = false;
        }
    }
}