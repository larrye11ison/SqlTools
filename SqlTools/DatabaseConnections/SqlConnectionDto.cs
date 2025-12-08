namespace SqlTools.DatabaseConnections
{
    /// <summary>
    /// Used to persist DB connection information.
    /// </summary>
    public class SqlConnectionDto
    {
        public int? CommandTimeout { get; set; }

        public string ObjectDefinitionQuery { get; set; }

        public string ObjectNameQuery { get; set; }

        public string ObjectSchemaQuery { get; set; }

        public string ServerAndInstance { get; set; }

        public string UserName { get; set; }
    }
}