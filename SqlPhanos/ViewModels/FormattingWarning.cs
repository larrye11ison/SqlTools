namespace SqlPhanos.ViewModels;

/// <summary>
/// One object that failed SqlCanonicalizationService's round-trip safety check during a bulk
/// scripting run (see ScriptDatabasesDocumentViewModel.FormatSqlText). OriginalText and
/// ProblematicText are each just a few lines of context around the actual mismatch (see
/// SqlFormatResult.OriginalTextSnippet/RejectedTextSnippet), not the whole object script - the
/// full, correct original is what actually gets written to disk unchanged; ProblematicText was
/// never written anywhere, it exists only to help diagnose the formatter bug.
/// </summary>
public sealed record FormattingWarning(string DatabaseName, string ObjectName, string OriginalText, string ProblematicText);
