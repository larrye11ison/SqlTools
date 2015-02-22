using SqlTools.DatabaseConnections;

namespace SqlTools.Scripting
{
    internal interface IDatabaseObjectScripter
    {
        string GetScript(SqlConnectionViewModel vm);
    }
}