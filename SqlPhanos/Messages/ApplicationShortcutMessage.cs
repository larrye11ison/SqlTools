using CommunityToolkit.Mvvm.Messaging.Messages;
using Dock.Model.Core;
using SqlPhanos.Enums;

namespace SqlPhanos.Messages;

public class ApplicationShortcutMessage : ValueChangedMessage<ApplicationShortcut>
{
    public ApplicationShortcutMessage(ApplicationShortcut shortcut, IDockable? context) : base(shortcut)
    {
        Context = context;
    }

    public IDockable? Context { get; }
}