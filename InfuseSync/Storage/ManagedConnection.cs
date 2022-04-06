using System;
using System.Collections.Generic;
using SQLitePCL.pretty;

namespace InfuseSync.Storage
{
    public sealed class ManagedConnection : IDisposable
    {
        private readonly SQLiteDatabaseConnection db;

        public ManagedConnection(SQLiteDatabaseConnection db)
        {
            this.db = db;
        }

        public IStatement PrepareStatement(string sql)
        {
            return db.PrepareStatement(sql);
        }

        public IEnumerable<IStatement> PrepareAll(string sql)
        {
            return db.PrepareAll(sql);
        }

        public void ExecuteAll(string sql)
        {
            db.ExecuteAll(sql);
        }

        public void Execute(string sql, params object[] values)
        {
            db.Execute(sql, values);
        }

        public void RunQueries(string[] sql)
        {
            db.RunQueries(sql);
        }

        public void RunInTransaction(Action<IDatabaseConnection> action, TransactionMode mode)
        {
            db.RunInTransaction(action, mode);
        }

        public T RunInTransaction<T>(Func<IDatabaseConnection, T> action, TransactionMode mode)
        {
            return db.RunInTransaction(action, mode);
        }

        public IEnumerable<IReadOnlyList<ResultSetValue>> Query(string sql)
        {
            return db.Query(sql);
        }

        public IEnumerable<IReadOnlyList<ResultSetValue>> Query(string sql, params object[] values)
        {
            return db.Query(sql, values);
        }

        public long LastInsertedRowId
        {
            get => db.LastInsertedRowId;
        }

        public void Dispose()
        {
        }
    }
}
