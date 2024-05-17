using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using SQLitePCL.pretty;
using System.Linq;
using MediaBrowser.Model.Logging;
using InfuseSync.Logging;

namespace InfuseSync.Storage
{
    public abstract class BaseSqliteRepository : IDisposable
    {
        protected string DbFilePath { get; set; }
        protected ReaderWriterLockSlim WriteLock { get; }

        protected ILogger _logger { get; private set; }
        private static bool _versionLogged;

        protected BaseSqliteRepository(ILogger logger)
        {
            _logger = logger;
            WriteLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }

        protected TransactionMode TransactionMode => TransactionMode.Deferred;

        protected TransactionMode ReadTransactionMode => TransactionMode.Deferred;

        internal static int ThreadSafeMode { get; set; }

        private string _defaultWal;

        protected IDatabaseConnection _connection;

        protected virtual bool EnableSingleConnection => true;

        protected virtual bool EnableTempStoreMemory => false;

        protected virtual int? CacheSize => null;

        protected IDatabaseConnection CreateConnection(bool isReadOnly = false)
        {
            if (_connection != null)
            {
                return _connection.Clone(false);
            }

            lock (WriteLock)
            {
                if (!_versionLogged)
                {
                    _versionLogged = true;
                }

                ConnectionFlags connectionFlags;

                if (isReadOnly)
                {
                    connectionFlags = ConnectionFlags.Create;
                    connectionFlags |= ConnectionFlags.ReadWrite;
                }
                else
                {
                    connectionFlags = ConnectionFlags.Create;
                    connectionFlags |= ConnectionFlags.ReadWrite;
                }

                if (EnableSingleConnection)
                {
                    connectionFlags |= ConnectionFlags.PrivateCache;
                }
                else
                {
                    connectionFlags |= ConnectionFlags.SharedCached;
                }

                connectionFlags |= ConnectionFlags.NoMutex;

                var db = SQLite3.Open(DbFilePath, connectionFlags, null, false);

                try
                {
                    if (string.IsNullOrWhiteSpace(_defaultWal))
                    {
                        var query = "PRAGMA journal_mode";

                        using (var statement = PrepareStatement(db, query))
                        {
                            foreach (var row in statement.ExecuteQuery())
                            {
                                _defaultWal = row.GetString(0);
                                break;
                            }
                        }
                    }

                    var queries = new List<string>
                    {
                        "PRAGMA synchronous=Normal"
                    };

                    if (CacheSize.HasValue)
                    {
                        queries.Add("PRAGMA cache_size=" + CacheSize.Value.ToString(CultureInfo.InvariantCulture));
                    }

                    if (EnableTempStoreMemory)
                    {
                        queries.Add("PRAGMA temp_store = memory");
                    }
                    else
                    {
                        queries.Add("PRAGMA temp_store = file");
                    }

                    db.ExecuteAll(string.Join(";", queries.ToArray()));
                }
                catch
                {
                    db.Dispose();
                    throw;
                }

                _connection = db;
                return db;
            }
        }

        public IStatement PrepareStatement(IDatabaseConnection connection, string sql)
        {
            return connection.PrepareStatement(sql);
        }

        public IStatement[] PrepareAll(IDatabaseConnection connection, List<string> sql)
        {
            var length = sql.Count;
            var result = new IStatement[length];

            for (var i = 0; i < length; i++)
            {
                result[i] = connection.PrepareStatement(sql[i]);
            }

            return result;
        }

        protected void RunDefaultInitialization(IDatabaseConnection db)
        {
            var queries = new List<string>
            {
                "PRAGMA journal_mode=WAL",
                "PRAGMA page_size=4096",
                "PRAGMA synchronous=Normal"
            };

            if (EnableTempStoreMemory)
            {
                queries.AddRange(new List<string>
                {
                    "pragma default_temp_store = memory",
                    "pragma temp_store = memory"
                });
            }
            else
            {
                queries.AddRange(new List<string>
                {
                    "pragma temp_store = file"
                });
            }

            db.ExecuteAll(string.Join(";", queries.ToArray()));
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private readonly object _disposeLock = new object();
        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                DisposeConnection();
            }

            _disposed = true;
        }

        private void DisposeConnection()
        {
            try
            {
                lock (_disposeLock)
                {
                    using (WriteLock.Write())
                    {
                        _connection?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error disposing database: {ex}");
            }
        }

        protected List<string> GetColumnNames(IDatabaseConnection connection, string table)
        {
            var list = new List<string>();

            using (var statement = PrepareStatement(connection, "PRAGMA table_info(" + table + ")"))
            {
                foreach (var row in statement.ExecuteQuery())
                {
                    if (!row.IsDBNull(1))
                    {
                        var name = row.GetString(1);

                        list.Add(name);
                    }
                }
            }

            return list;
        }

        protected bool AddColumn(IDatabaseConnection connection, string table, string columnName, string type, List<string> existingColumnNames)
        {
            if (existingColumnNames.Contains(columnName, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            connection.Execute("alter table " + table + " add column " + columnName + " " + type + " NULL");
            return true;
        }
    }
}
