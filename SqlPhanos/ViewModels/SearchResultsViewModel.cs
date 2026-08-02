using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
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
    private ObservableCollection<SearchResultViewModel> _filteredResults = new();

    // Drives the one-line summary shown in both results views: "Showing all X results." or,
    // while a filter is active, "Showing X of Y results." IsFiltered mirrors whether any filter
    // field currently has text, so the views can give the summary a subtle highlight only then.
    [ObservableProperty]
    private string _resultsSummaryText = "Showing all 0 results.";

    [ObservableProperty]
    private bool _isFiltered;

    public SearchResultsViewModel()
    {
        Id = "SearchResults";
        Title = "Search Results";

        WeakReferenceMessenger.Default.Register(this);
        UpdateFilteredResults();
    }

    public void Receive(SearchResultsMessage message)
    {
        _allResults.Clear();
        foreach (var item in message.Value)
        {
            _allResults.Add(item);
        }
        UpdateFilteredResults();

        // Only on an actual search completing - not on every filter-box edit, which also
        // calls UpdateFilteredResults() and would otherwise steal focus while typing.
        if (FilteredResults.Count > 0)
        {
            WeakReferenceMessenger.Default.Send(new FocusFirstSearchResultMessage());
        }
    }

    partial void OnFilterDbChanged(string value) => UpdateFilteredResults();
    partial void OnFilterGeneralChanged(string value) => UpdateFilteredResults();
    partial void OnFilterNameChanged(string value) => UpdateFilteredResults();
    partial void OnFilterSchemaChanged(string value) => UpdateFilteredResults();
    partial void OnFilterTypeChanged(string value) => UpdateFilteredResults();

    private bool Matches(string? value, string filter)
    {
        if (string.IsNullOrEmpty(value)) return false;

        var negate = false;
        if (filter.StartsWith("!"))
        {
            negate = true;
            filter = filter.Substring(1);
        }

        var contains = value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        return negate ? !contains : contains;
    }

    [RelayCommand]
    private async Task ScriptObjectAsync(SearchResultViewModel? item)
    {
        if (item == null) return;

        WeakReferenceMessenger.Default.Send(new ScriptObjectRequestMessage(item));
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ExploreDependencies(SearchResultViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        WeakReferenceMessenger.Default.Send(new ExploreDependenciesRequestMessage(item));
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
                var matchesGeneral =
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

        FilteredResults = new ObservableCollection<SearchResultViewModel>(filtered);

        IsFiltered = !string.IsNullOrWhiteSpace(FilterGeneral) ||
                     !string.IsNullOrWhiteSpace(FilterName) ||
                     !string.IsNullOrWhiteSpace(FilterSchema) ||
                     !string.IsNullOrWhiteSpace(FilterDb) ||
                     !string.IsNullOrWhiteSpace(FilterType);

        ResultsSummaryText = IsFiltered
            ? $"Showing {FilteredResults.Count} of {_allResults.Count} results."
            : $"Showing all {_allResults.Count} results.";
    }
}