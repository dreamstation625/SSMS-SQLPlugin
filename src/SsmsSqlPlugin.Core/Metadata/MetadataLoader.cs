using System;
using System.Collections.Generic;
using System.Data;

namespace SsmsSqlPlugin.Core.Metadata
{
    /// <summary>sys.* / INFORMATION_SCHEMA 元数据抓取。</summary>
    public static class MetadataLoader
    {
        private const int TimeoutSeconds = 10;

        public static MetadataSnapshot Load(ConnectionDescriptor descriptor, out string error)
        {
            error = null;
            var snapshot = MetadataSnapshot.Empty(descriptor?.Server, descriptor?.Database);

            if (descriptor == null || !descriptor.IsUsable)
            {
                error = "没有可用的数据库连接";
                return snapshot;
            }

            try
            {
                LoadFromSys(descriptor, snapshot);
                return snapshot;
            }
            catch (Exception sysEx)
            {
                string firstError = sysEx.Message;

                try
                {
                    var fallback = MetadataSnapshot.Empty(descriptor.Server, descriptor.Database);
                    LoadFromInformationSchema(descriptor, fallback);
                    error = "sys.* 失败（" + firstError + "），已降级 INFORMATION_SCHEMA";
                    return fallback;
                }
                catch (Exception infoEx)
                {
                    error = "元数据加载失败：" + firstError + " / " + infoEx.Message;
                    return snapshot;
                }
            }
        }

        private static IDbConnection Open(ConnectionDescriptor descriptor)
        {
            var connection = (IDbConnection)Activator.CreateInstance(
                descriptor.ConnectionType, new object[] { descriptor.ConnectionString });
            connection.Open();
            return connection;
        }

        private static void LoadFromSys(ConnectionDescriptor descriptor, MetadataSnapshot snapshot)
        {
            const string sql = @"
SELECT s.name AS SchemaName, o.name AS ObjectName, o.type AS ObjectType,
       c.name AS ColumnName, c.column_id, t.name AS DataType,
       c.is_nullable
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.columns c ON c.object_id = o.object_id
LEFT JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE o.type IN ('U','V','TF','IF','P','PC') AND o.is_ms_shipped = 0
ORDER BY s.name, o.name, c.column_id";

            using (var connection = Open(descriptor))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.CommandTimeout = TimeoutSeconds;

                using (var reader = command.ExecuteReader())
                {
                    var map = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);

                    while (reader.Read())
                    {
                        string schema = AsString(reader, 0);
                        string name = AsString(reader, 1);
                        // sys.objects.type 是 char(2)，表/视图/过程会带尾空格
                        string kind = (AsString(reader, 2) ?? string.Empty).Trim();
                        if (string.IsNullOrEmpty(name)) continue;

                        string key = schema + "\u0001" + name;
                        TableMetadata table;
                        if (!map.TryGetValue(key, out table))
                        {
                            table = new TableMetadata { Schema = schema, Name = name, Kind = kind };
                            map[key] = table;
                            snapshot.Objects.Add(table);
                        }

                        string column = AsString(reader, 3);
                        if (string.IsNullOrEmpty(column)) continue;

                        table.Columns.Add(new ColumnMetadata
                        {
                            Name = column,
                            Ordinal = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
                            DataType = AsString(reader, 5),
                            IsNullable = !reader.IsDBNull(6) && Convert.ToBoolean(reader.GetValue(6)),
                        });
                    }
                }
            }

            snapshot.LoadedUtc = DateTime.UtcNow;
        }

        private static void LoadFromInformationSchema(ConnectionDescriptor descriptor, MetadataSnapshot snapshot)
        {
            const string sql = @"
SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE, COLUMN_NAME, ORDINAL_POSITION,
       DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";

            using (var connection = Open(descriptor))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.CommandTimeout = TimeoutSeconds;

                using (var reader = command.ExecuteReader())
                {
                    var map = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);

                    while (reader.Read())
                    {
                        string schema = AsString(reader, 0);
                        string name = AsString(reader, 1);
                        string tableType = AsString(reader, 2);
                        if (string.IsNullOrEmpty(name)) continue;

                        string key = schema + "\u0001" + name;
                        TableMetadata table;
                        if (!map.TryGetValue(key, out table))
                        {
                            table = new TableMetadata
                            {
                                Schema = schema,
                                Name = name,
                                Kind = string.Equals(tableType, "VIEW", StringComparison.OrdinalIgnoreCase) ? "V" : "U",
                            };
                            map[key] = table;
                            snapshot.Objects.Add(table);
                        }

                        string column = AsString(reader, 3);
                        if (string.IsNullOrEmpty(column)) continue;

                        table.Columns.Add(new ColumnMetadata
                        {
                            Name = column,
                            Ordinal = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
                            DataType = AsString(reader, 5),
                            IsNullable = string.Equals(AsString(reader, 6), "YES", StringComparison.OrdinalIgnoreCase),
                        });
                    }
                }
            }

            snapshot.LoadedUtc = DateTime.UtcNow;
        }

        private static string AsString(IDataRecord reader, int index)
            => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index));
    }
}
