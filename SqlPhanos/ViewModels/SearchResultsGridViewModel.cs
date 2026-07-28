using Dock.Model.Mvvm.Controls;

namespace SqlPhanos.ViewModels;

/// <summary>
/// The dockable "identity" for the DataGrid results view - a thin wrapper rather than its own
/// independent results/filter state, since the card view (<see cref="SearchResultsViewModel"/>)
/// and this grid view are two alternate renderings of exactly the same search results and
/// filters. Only one of the two is ever showing at a time (see
/// ShellViewModel.ApplyResultsViewMode), so there is no risk of them drifting apart - both
/// bind straight through to the same shared <see cref="SearchResultsViewModel"/> instance.
/// Named to match Dock.Avalonia's default view locator convention (strips "ViewModel", appends
/// "View" -> SearchResultsGridView).
/// </summary>
public sealed class SearchResultsGridViewModel : Tool
{
    // Parameterless constructor exists only for the XAML Design.DataContext tag, matching the
    // same pattern SqlDocumentViewModel/ScriptDatabasesDocumentViewModel already use.
    public SearchResultsGridViewModel() : this(new SearchResultsViewModel())
    {
    }

    public SearchResultsGridViewModel(SearchResultsViewModel searchResults)
    {
        Id = "SearchResultsGrid";
        Title = "Search Results (Grid)";
        SearchResults = searchResults;
    }

    public SearchResultsViewModel SearchResults { get; }
}
