namespace SsmsSqlPlugin.Storage
{
    internal static class StorageSchema
    {
        public static readonly string[] Statements =
        {
            @"CREATE TABLE IF NOT EXISTS Contents (
                ContentId BLOB PRIMARY KEY,
                SqlBytes BLOB NOT NULL,
                Length INTEGER NOT NULL,
                LastSeenUtc TEXT NOT NULL
            )",

            @"CREATE TABLE IF NOT EXISTS Executions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StartedUtc TEXT NOT NULL,
                DurationMs INTEGER NOT NULL DEFAULT 0,
                ServerName TEXT,
                DatabaseName TEXT,
                LoginName TEXT,
                ContentId BLOB NOT NULL,
                RowsAffected INTEGER NOT NULL DEFAULT 0,
                Succeeded INTEGER NOT NULL DEFAULT 0,
                ErrorNumber INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT,
                SourcePath TEXT,
                EditorMoniker TEXT,
                IsSelectionOnly INTEGER NOT NULL DEFAULT 0,
                DeletedUtc TEXT,
                Kind TEXT NOT NULL DEFAULT 'execute'
            )",

            "CREATE INDEX IF NOT EXISTS IX_Executions_StartedUtc ON Executions(StartedUtc)",
            "CREATE INDEX IF NOT EXISTS IX_Executions_ServerDb_Time ON Executions(ServerName, DatabaseName, StartedUtc)",

            @"CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FullPath TEXT NOT NULL UNIQUE,
                FirstSeenUtc TEXT,
                LastOpenedUtc TEXT,
                LastSavedUtc TEXT,
                OpenCount INTEGER NOT NULL DEFAULT 0
            )",

            @"CREATE TABLE IF NOT EXISTS FileRevisions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FileId INTEGER NOT NULL,
                ContentId BLOB NOT NULL,
                CapturedUtc TEXT NOT NULL,
                Kind TEXT
            )",

            "CREATE INDEX IF NOT EXISTS IX_FileRevisions_File_Time ON FileRevisions(FileId, CapturedUtc)",

            @"CREATE TABLE IF NOT EXISTS FileContexts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FileId INTEGER NOT NULL,
                ServerName TEXT,
                DatabaseName TEXT,
                LastUsedUtc TEXT
            )",

            @"CREATE TABLE IF NOT EXISTS Favorites (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT,
                ContentId BLOB,
                ServerName TEXT,
                DatabaseName TEXT
            )",

            @"CREATE TABLE IF NOT EXISTS SchemaCache (
                ServerName TEXT NOT NULL,
                DatabaseName TEXT NOT NULL,
                FetchedUtc TEXT,
                Payload BLOB,
                PRIMARY KEY (ServerName, DatabaseName)
            )",

            @"CREATE TABLE IF NOT EXISTS Meta (
                Key TEXT PRIMARY KEY,
                Value TEXT
            )",
        };
    }
}
