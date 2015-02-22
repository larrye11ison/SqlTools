namespace SqlTools.Settings
{
    /// <summary>
    /// Settings that the application persists to the database on exit and reads back in at launch.
    /// </summary>
    public class ApplicationSettings
    {
        public DatabaseConnections.SqlConnectionDto[] Connections;

        public string FontFamilyName { get; set; }
    }
}