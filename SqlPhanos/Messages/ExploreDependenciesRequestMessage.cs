using CommunityToolkit.Mvvm.Messaging.Messages;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Messages;

public sealed class ExploreDependenciesRequestMessage : ValueChangedMessage<SearchResultViewModel>
{
    public ExploreDependenciesRequestMessage(SearchResultViewModel value) : base(value)
    {
    }
}
