namespace SqlTools.Models.Shell
{
    public enum ConnectionStatus
    {
        Dormant,
        Connecting,
        GettingDatabases,
        SearchingForObjects,
        CreatingObjectScripts,
        AddingToLocalCollection
    }
}