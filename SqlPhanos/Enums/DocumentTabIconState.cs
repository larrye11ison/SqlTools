namespace SqlPhanos.Enums;

/// <summary>
/// The status icon shown in a document tab's header - the tab itself decides which state
/// applies based on its own busy/error condition (see IHasTabHeaderLines.TabIconState).
/// </summary>
public enum DocumentTabIconState
{
    /// <summary>No icon - normal/OK.</summary>
    None,

    /// <summary>A static (non-animated) busy indicator - an async process is running.</summary>
    Busy,

    /// <summary>A red error indicator - the tab's process failed.</summary>
    Error
}
