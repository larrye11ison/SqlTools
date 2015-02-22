using System;
using System.Diagnostics.Contracts;

namespace SqlTools.Shell
{
    /// <summary>
    /// ViewModel for a database object that's been scripted out from MSSQL.
    /// </summary>
    public class ScriptedObjectInfo
    {
        private const string emptyString = "__________________________________empty_";

        private static ScriptedObjectInfo _empty = new ScriptedObjectInfo("Object is loading...", new Data.SysObject
        {
            server_name = emptyString,
            db_name = emptyString,
            object_name = emptyString,
            schema_name = emptyString
        });

        public ScriptedObjectInfo(string objectDefinition,
            Data.SysObject sysObject)
        {
            Contract.Requires(string.IsNullOrWhiteSpace(objectDefinition) != true);
            Contract.Requires(sysObject != null);
            this.ObjectDefinition = objectDefinition;
            this.DbObject = sysObject;
        }

        public static ScriptedObjectInfo Empty
        {
            get
            {
                return _empty;
            }
        }

        public Data.SysObject DbObject { get; set; }

        public string ObjectDefinition { get; set; }

        public override bool Equals(object obj)
        {
            if ((obj is ScriptedObjectInfo) == false)
            {
                return false;
            }
            var other = (ScriptedObjectInfo)obj;
            return this.DbObject == other.DbObject;
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

        public override string ToString()
        {
            int dbObjectHashCode;
            if (DbObject != null)
                dbObjectHashCode = DbObject.GetHashCode();
            else
                dbObjectHashCode = 0;
            return String.Format("Scripted Object: {0}", dbObjectHashCode);
        }
    }
}