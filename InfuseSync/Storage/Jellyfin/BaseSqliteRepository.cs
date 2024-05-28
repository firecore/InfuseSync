#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace InfuseSync.Storage
{
    public abstract class BaseSqliteRepository : IDisposable
    {
        private bool _disposed = false;

        protected ReaderWriterLockSlim WriteLock { get; }

        protected BaseSqliteRepository(ILogger logger)
        {
            _logger = logger;

            WriteLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }

        protected string DbFilePath { get; set; }

        protected ILogger _logger { get; private set; }

        protected virtual int? CacheSize => null;

        protected virtual string LockingMode => "NORMAL";

        protected virtual string JournalMode => "WAL";

        protected virtual int? JournalSizeLimit => 134_217_728; // 128MiB

        protected virtual int? PageSize => null;

        protected virtual TempStoreMode TempStore => TempStoreMode.Memory;

        protected virtual SynchronousMode? Synchronous => SynchronousMode.Normal;

        public virtual void Initialize()
        {
            // Configuration and pragmas can affect VACUUM so it needs to be last.
            using (var connection = CreateConnection())
            {
                connection.Execute("VACUUM");
            }
        }

        protected SqliteConnection CreateConnection(bool isReadOnly = false)
        {
            var connection = new SqliteConnection($"Filename={DbFilePath}");
            connection.Open();

            if (CacheSize.HasValue)
            {
                connection.Execute("PRAGMA cache_size=" + CacheSize.Value);
            }

            if (!string.IsNullOrWhiteSpace(LockingMode))
            {
                connection.Execute("PRAGMA locking_mode=" + LockingMode);
            }

            if (!string.IsNullOrWhiteSpace(JournalMode))
            {
                connection.Execute("PRAGMA journal_mode=" + JournalMode);
            }

            if (JournalSizeLimit.HasValue)
            {
                connection.Execute("PRAGMA journal_size_limit=" + JournalSizeLimit.Value);
            }

            if (Synchronous.HasValue)
            {
                connection.Execute("PRAGMA synchronous=" + (int)Synchronous.Value);
            }

            if (PageSize.HasValue)
            {
                connection.Execute("PRAGMA page_size=" + PageSize.Value);
            }

            connection.Execute("PRAGMA temp_store=" + (int)TempStore);

            return connection;
        }

        protected void RunDefaultInitialization(SqliteConnection connection)
        {
        }

        public SqliteCommand PrepareStatement(SqliteConnection connection, string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            return command;
        }

        protected bool TableExists(SqliteConnection connection, string name)
        {
            using var statement = PrepareStatement(connection, "select DISTINCT tbl_name from sqlite_master");
            foreach (var row in statement.ExecuteQuery())
            {
                if (string.Equals(name, row.GetString(0), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        protected List<string> GetColumnNames(SqliteConnection connection, string table)
        {
            var columnNames = new List<string>();

            foreach (var row in connection.Query("PRAGMA table_info(" + table + ")"))
            {
                if (row.TryGetString(1, out var columnName))
                {
                    columnNames.Add(columnName);
                }
            }

            return columnNames;
        }

        protected void AddColumn(SqliteConnection connection, string table, string columnName, string type, List<string> existingColumnNames)
        {
            if (existingColumnNames.Contains(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            connection.Execute("alter table " + table + " add column " + columnName + " " + type + " NULL");
        }

        protected void CheckDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool dispose)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
    }
}
