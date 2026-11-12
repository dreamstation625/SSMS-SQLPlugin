using System;
using System.Collections.Generic;

namespace SsmsSqlPlugin.Core.Metadata
{
    public sealed class MetadataSnapshot
    {
        public string Server;
        public string Database;
        public DateTime LoadedUtc;
        public readonly List<TableMetadata> Objects = new List<TableMetadata>();

        public static MetadataSnapshot Empty(string server = null, string database = null)
        {
            return new MetadataSnapshot
            {
                Server = server,
                Database = database,
                LoadedUtc = DateTime.MinValue,
            };
        }

        public TableMetadata Find(string schema, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            for (int i = 0; i < Objects.Count; i++)
            {
                var o = Objects[i];
                if (!string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(schema) ||
                    string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase))
                    return o;
            }
            return null;
        }

        public TableMetadata Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            for (int i = 0; i < Objects.Count; i++)
            {
                if (string.Equals(Objects[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return Objects[i];
            }
            return null;
        }
    }
}
