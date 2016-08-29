using Microsoft.SqlServer.Management.Sdk.Sfc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

using System.Data;

namespace SqlTools.Data
{
    /// <summary>
    /// An object in the database, like a Stored Procedure or Table.
    /// </summary>
    /// <remarks>
    /// So-named because in olde tymes you might write a query like "select * from sysobjects"
    /// to get a list of everything in the DB.
    /// </remarks>
    public class SysObject
    {
        public DateTime create_date { get; set; }

        [Key]
        public string db_name { get; set; }

        //[NotMapped]
        public string full_name
        {
            get
            {
                if (string.IsNullOrWhiteSpace(schema_name))
                {
                    return object_name;
                }
                return string.Format("{0}.{1}", schema_name, object_name);
            }
        }

        public bool is_encrypted { get; set; }

        public string LongDescription
        {
            get
            {
                var parentChunk = "";
                if (string.IsNullOrWhiteSpace(parent_fq_name) == false)
                {
                    parentChunk = string.Format(" ({0})", parent_fq_name);
                }
                return string.Format("«{0}» {1}::{2}{3}", server_name, db_name, full_name, parentChunk);
            }
        }

        public DateTime modify_date { get; set; }

        [Key]
        public int object_id { get; set; }

        public string object_name { get; set; }

        /// <summary>
        /// Fully qualified name of the parent object (not quoted), i.e. «schema».«name»
        /// </summary>
        //[NotMapped]
        public string parent_fq_name
        {
            get
            {
                if (string.IsNullOrWhiteSpace(parent_object_name))
                {
                    return "";
                }
                return string.Format("{0}.{1}", parent_object_schema_name, parent_object_name);
            }
        }

        public string parent_object_name { get; set; }

        public string parent_object_schema_name { get; set; }

        public string schema_name { get; set; }

        public string server_name { get; set; }

        public string type_desc { get; set; }

        public Urn Urn { get; set; }

        public static IEnumerable<SysObject> MapFrom(IDataReader rdr)
        {
            while (rdr.Read())
            {
                var rv = new SysObject
                {
                    db_name = rdr["db_name"].ToString(),
                    create_date = Convert.ToDateTime(rdr["create_date"]),
                    is_encrypted = Convert.ToBoolean(rdr["is_encrypted"]),
                    modify_date = Convert.ToDateTime(rdr["modify_date"]),
                    object_id = Convert.ToInt32(rdr["object_id"]),
                    object_name = Convert.ToString(rdr["object_name"]),
                    parent_object_name = Convert.ToString(rdr["parent_object_name"]),
                    parent_object_schema_name = Convert.ToString(rdr["parent_object_schema_name"]),
                    schema_name = Convert.ToString(rdr["schema_name"]),
                    server_name = Convert.ToString(rdr["server_name"]),
                    type_desc = Convert.ToString(rdr["type_desc"])
                    //,
                    //Urn = Convert.ToString(rdr["Urn"])
                };
                yield return rv;
            }
        }

        public override bool Equals(object obj)
        {
            if (obj is SysObject)
            {
                var theSysOb = (SysObject)obj;
                return
                     theSysOb.server_name == server_name
                    && theSysOb.db_name == db_name
                    && theSysOb.schema_name == schema_name
                    && theSysOb.object_name == object_name;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return LongDescription.GetHashCode();
        }

        public override string ToString()
        {
            return LongDescription;
        }
    }
}