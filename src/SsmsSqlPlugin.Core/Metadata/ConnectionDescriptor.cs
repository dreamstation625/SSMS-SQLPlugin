using System;

namespace SsmsSqlPlugin.Core.Metadata
{
    /// <summary>
    /// 当前查询窗口的连接描述。
    ///
    /// ConnectionString 里可能有明文密码：只允许在内存里传给元数据连接，
    /// **不要写日志**。日志只打 Server / Database。
    /// </summary>
    public sealed class ConnectionDescriptor
    {
        public string Server;
        public string Database;
        public string ConnectionString;
        /// <summary>宿主返回的连接对象类型（Microsoft.Data.SqlClient.SqlConnection 等）。</summary>
        public Type ConnectionType;

        public string Key
            => (Server ?? string.Empty) + "\u0001" + (Database ?? string.Empty);

        public bool IsUsable
            => ConnectionType != null && !string.IsNullOrEmpty(ConnectionString);
    }
}
