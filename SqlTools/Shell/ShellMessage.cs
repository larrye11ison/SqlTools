namespace SqlTools.Shell
{
    public enum Severity
    {
        Info,
        Warning,
        Fatal
    }

    /// <summary>
    /// Used to notify the Shell of important messages, such as errors.
    /// </summary>
    public class ShellMessage
    {
        public string MessageText { get; set; }
        public Severity Severity { get; set; }
    }
}