using CommunityToolkit.Mvvm.Messaging.Messages;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Messages;

public class ScriptObjectRequestMessage : ValueChangedMessage<SearchResultViewModel>
{
    public ScriptObjectRequestMessage(SearchResultViewModel result) : base(result)
    {
    }
}