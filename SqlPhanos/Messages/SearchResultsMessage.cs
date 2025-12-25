using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging.Messages;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Messages;

public class SearchResultsMessage : ValueChangedMessage<List<SearchResultViewModel>>
{
    public SearchResultsMessage(List<SearchResultViewModel> results) : base(results)
    {
    }
}