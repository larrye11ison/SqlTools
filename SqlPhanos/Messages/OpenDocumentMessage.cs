using CommunityToolkit.Mvvm.Messaging.Messages;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Messages;

public class OpenDocumentMessage : ValueChangedMessage<SqlDocumentViewModel>
{
    public OpenDocumentMessage(SqlDocumentViewModel document) : base(document)
    {
    }
}