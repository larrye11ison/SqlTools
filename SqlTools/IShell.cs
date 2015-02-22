namespace SqlTools
{
    public interface IShell
    {
        void ActivateDocument(Scripting.ScriptedObjectDocumentViewModel firstNew);

        void OpenDocument(Scripting.ScriptedObjectDocumentViewModel firstNew);
    }
}