using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SqlPhanos.Messages;

public sealed class StatusMessage : ValueChangedMessage<string>
{
    public StatusMessage(string value) : base(value)
    {
    }
}
