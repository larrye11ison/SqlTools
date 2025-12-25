using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using ReactiveUI;
using SqlPhanos.Messages;
using SqlPhanos.Services;

namespace SqlPhanos.ViewModels;

public partial class SearchResultsViewModel : Tool, IRecipient<SearchResultsMessage>
{
    private readonly ObservableCollection<SearchResultViewModel> _allResults = new();

    private readonly SqlSearchService _searchService = new();

    [ObservableProperty]
    private string _filterDb = "";

    [ObservableProperty]
    private string _filterGeneral = "";

    [ObservableProperty]
    private string _filterName = "";

    [ObservableProperty]
    private string _filterSchema = "";

    [ObservableProperty]
    private string _filterType = "";

    [ObservableProperty]
    private FlatTreeDataGridSource<SearchResultViewModel> _source;

    public SearchResultsViewModel()
    {
        Id = "SearchResults";
        Title = "Search Results";

        // Register for messages
        WeakReferenceMessenger.Default.Register(this);

        // Initial population (empty)
        UpdateFilteredResults();

        // Setup live filtering
        this.WhenAnyValue(
                x => x.FilterName,
                x => x.FilterSchema,
                x => x.FilterDb,
                x => x.FilterType,
                x => x.FilterGeneral)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateFilteredResults());
    }

    public void Receive(SearchResultsMessage message)
    {
        _allResults.Clear();
        foreach (var item in message.Value)
        {
            _allResults.Add(item);
        }
        UpdateFilteredResults();
    }

    private FlatTreeDataGridSource<SearchResultViewModel> CreateSource(IEnumerable<SearchResultViewModel> items)
    {
        return new FlatTreeDataGridSource<SearchResultViewModel>(items)
        {
            Columns =
            {
                new TemplateColumn<SearchResultViewModel>(
                    "Script",
                    "ScriptCellTemplate",
                    null,
                    new GridLength(80)),
                new TextColumn<SearchResultViewModel, string>(
                    "Server",
                    x => x.ServerName,
                    new GridLength(1, GridUnitType.Star)),
                new TextColumn<SearchResultViewModel, string>(
                    "DB",
                    x => x.DbName,
                    new GridLength(1, GridUnitType.Star)),
                new TextColumn<SearchResultViewModel, string>(
                    "Type",
                    x => x.TypeDesc,
                    new GridLength(1, GridUnitType.Star)),
                new TextColumn<SearchResultViewModel, string>(
                    "Schema",
                    x => x.SchemaName,
                    new GridLength(1, GridUnitType.Star)),
                new TextColumn<SearchResultViewModel, string>(
                    "Name",
                    x => x.ObjectName,
                    new GridLength(2, GridUnitType.Star)),
                new TextColumn<SearchResultViewModel, string>(
                    "Parent",
                    x => x.ParentFqName,
                    new GridLength(1, GridUnitType.Star)),
                new CheckBoxColumn<SearchResultViewModel>(
                    "Encrypted",
                    x => x.IsEncrypted,
                    null,
                    new GridLength(90))
            }
        };
    }

    private bool Matches(string? value, string filter)
    {
        if (string.IsNullOrEmpty(value)) return false;

        bool negate = false;
        if (filter.StartsWith("!"))
        {
            negate = true;
            filter = filter.Substring(1);
        }

        bool contains = value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        return negate ? !contains : contains;
    }

    [RelayCommand]
    private async Task ScriptObjectAsync(SearchResultViewModel? item)
    {
        if (item == null) return;

        // We need the connection string.
        // Ideally, the item should carry the connection info or we get it from SearchViewModel.
        // For now, let's assume we can get it from the SearchViewModel via a message or shared service.
        // Or better, let's pass the connection string in the SearchResultViewModel or SearchResultsMessage.
        // But SearchResultViewModel is just data.

        // Let's request the current connection from SearchViewModel via a RequestMessage or similar?
        // Or simply, let's assume the SearchViewModel is the source of truth and we can ask it.
        // Actually, since we are in a decoupled architecture, maybe we should send a "ScriptRequestMessage"
        // and let SearchViewModel handle the scripting?
        // That seems cleaner as SearchViewModel holds the connection state.

        WeakReferenceMessenger.Default.Send(new ScriptObjectRequestMessage(item));
    }

    private void UpdateFilteredResults()
    {
        var filtered = _allResults.Where(item =>
        {
            if (!string.IsNullOrWhiteSpace(FilterName) && !Matches(item.ObjectName, FilterName)) return false;
            if (!string.IsNullOrWhiteSpace(FilterSchema) && !Matches(item.SchemaName, FilterSchema)) return false;
            if (!string.IsNullOrWhiteSpace(FilterDb) && !Matches(item.DbName, FilterDb)) return false;
            if (!string.IsNullOrWhiteSpace(FilterType) && !Matches(item.TypeDesc, FilterType)) return false;

            if (!string.IsNullOrWhiteSpace(FilterGeneral))
            {
                // General filter searches across multiple fields
                bool matchesGeneral =
                    Matches(item.ObjectName, FilterGeneral) ||
                    Matches(item.SchemaName, FilterGeneral) ||
                    Matches(item.DbName, FilterGeneral) ||
                    Matches(item.TypeDesc, FilterGeneral) ||
                    Matches(item.ServerName, FilterGeneral) ||
                    Matches(item.ParentFqName, FilterGeneral);

                if (!matchesGeneral) return false;
            }

            return true;
        });

        Source = CreateSource(filtered.ToList());
    }
}