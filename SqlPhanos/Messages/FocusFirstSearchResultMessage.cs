namespace SqlPhanos.Messages;

/// <summary>
/// Sent once a search actually completes (not on every filter-box edit) so the results
/// view can select and focus the first row, letting Enter script it immediately.
/// </summary>
public sealed class FocusFirstSearchResultMessage
{
}
