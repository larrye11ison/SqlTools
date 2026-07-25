using System;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// Builds the decorated comment header that precedes every scripted object's text, shared by
/// both scripting paths. Created/Last Mod are the one difference the two paths are meant to
/// have: Script Databases supplies them (they drive Delta-mode change detection), the
/// single-object interactive path omits them (dates) since there's no on-disk comparison to
/// support - Scripted is still always shown, since that's just "now," not database metadata.
/// </summary>
public static class ObjectScriptHeaderBuilder
{
    public static string Build(
        string objectName,
        string serverName,
        string databaseName,
        DateTimeOffset scriptedOn,
        ObjectDates? dates = null)
    {
        string nl = Environment.NewLine;
        string quotedDatabase = databaseName.Replace("]", "]]", StringComparison.Ordinal);

        string datesBlock = dates is { } d
            ? $"     Created:    {ObjectCatalogue.FormatCatalogueDate(d.Created)}{nl}" +
              $"     Last Mod:   {ObjectCatalogue.FormatCatalogueDate(d.LastModified)}{nl}"
            : "";

        return
            $"/*-------------------------------------------------------------------{nl}" +
            $"     Object:     {objectName}{nl}" +
            $"     Server:     {serverName}{nl}" +
            $"     Database:   {databaseName}{nl}" +
            $"     Scripted:   {ObjectCatalogue.FormatScriptedDate(scriptedOn)}{nl}" +
            datesBlock +
            $"---------------------------------------------------------------------*/{nl}" +
            $"USE [{quotedDatabase}]{nl}" +
            $"GO{nl}{nl}";
    }
}
