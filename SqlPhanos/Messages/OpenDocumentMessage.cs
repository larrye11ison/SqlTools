using CommunityToolkit.Mvvm.Messaging.Messages;
using Dock.Model.Mvvm.Controls;

namespace SqlPhanos.Messages;

public class OpenDocumentMessage : ValueChangedMessage<Document>
{
    public OpenDocumentMessage(Document document) : base(document)
    {
    }
}