using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SqlPhanos.Messages;

/// <summary>True means the grid (DataGrid) results view is now the active one; false means card view.</summary>
public class ResultsViewModeChangedMessage : ValueChangedMessage<bool>
{
    public ResultsViewModeChangedMessage(bool asGrid) : base(asGrid)
    {
    }
}
