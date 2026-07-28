using Microsoft.SqlServer.Management.Smo;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// The single canonical set of SMO ScriptingOptions shared by both "Script Databases" (bulk,
/// whole-database) and the single-object interactive scripting path - the two used to carry
/// independently-drifted option sets, which is how DriIncludeSystemNames ended up missing from
/// one of them. The only setting that legitimately varies between the two call sites is whether
/// a table/view's own triggers get embedded inline (see the parameter doc below).
/// </summary>
public static class ScriptingOptionsFactory
{
    /// <param name="includeTriggersInTableScript">
    /// Script Databases needs this true: SqlObjectFilters.GetScriptedObjectTypes() has no
    /// separate Trigger entry, so a table's triggers only ever appear at all if they're
    /// embedded in the table's own script. The single-object path needs this false when
    /// scripting a table/view (triggers are independently viewable as their own search
    /// result/tab there, so embedding them too would duplicate content) - it still scripts a
    /// trigger opened directly as its own standalone object, which doesn't go through this flag.
    /// </param>
    public static ScriptingOptions Create(bool includeTriggersInTableScript)
    {
        return new ScriptingOptions
        {
            DriAll = true,
            // Without this, SMO silently omits the CONSTRAINT [name] clause for any constraint
            // that was never explicitly named - it just scripts the bare "PRIMARY KEY (...)"/
            // "DEFAULT (...)"/etc, since (from SMO's point of view) the system-generated name
            // isn't meaningful. Always on rather than conditional: this app has limited usage
            // and is still in beta, so the simpler, more complete/faithful script (always
            // showing the real current name) wins over guarding the default output against a
            // diff-noise scenario that may never come up for most users.
            DriIncludeSystemNames = true,
            Indexes = true,
            ClusteredIndexes = true,
            // False, not true: ObjectScriptHeaderBuilder already writes its own "USE [db]" / GO
            // ahead of every scripted object's body, for both this factory's callers. Leaving
            // this true made SMO's Scripter *also* emit its own "USE [db]" as the first line of
            // the body it returns, producing two USE statements back to back in every script.
            IncludeDatabaseContext = false,
            IncludeDatabaseRoleMemberships = true,
            Triggers = includeTriggersInTableScript,
            Permissions = true,
            ScriptBatchTerminator = true,
            SchemaQualify = true,
            ScriptSchema = true,
            ScriptData = false,
            ScriptDrops = false,
            IncludeIfNotExists = false,
            ScriptForCreateOrAlter = true,
            EnforceScriptingOptions = true
        };
    }
}
