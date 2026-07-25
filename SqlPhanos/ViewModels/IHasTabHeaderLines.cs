namespace SqlPhanos.ViewModels;

/// <summary>
/// Implemented by MDI document ViewModels so a single shared DocumentTabStrip.HeaderTemplate
/// (set in ShellView.axaml) can render a consistent two-line tab header regardless of which
/// kind of document it is, instead of every document type repeating identifying text in its
/// own body content.
/// </summary>
public interface IHasTabHeaderLines
{
	/// <summary>Top line of the tab header - typically the database/connection.</summary>
	string TabHeaderLine1 { get; }

	/// <summary>Bottom line of the tab header - typically schema.objectname.</summary>
	string TabHeaderLine2 { get; }
}
