using System;
using System.Collections.Generic;
using SQLitePCL.pretty;

#if EMBY
using ResultSet = SQLitePCL.pretty.IResultSet;
#else
using ResultSet = System.Collections.Generic.IReadOnlyList<SQLitePCL.pretty.ResultSetValue>;
#endif

namespace InfuseSync.Storage
{
    public static class SqliteExtensions
    {
        private static readonly string[] _datetimeFormats = new string[] {
            "THHmmssK",
            "THHmmK",
            "HH:mm:ss.FFFFFFFK",
            "HH:mm:ssK",
            "HH:mmK",
            "yyyy-MM-dd HH:mm:ss.FFFFFFFK", /* NOTE: UTC default (5). */
            "yyyy-MM-dd HH:mm:ssK",
            "yyyy-MM-dd HH:mmK",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
            "yyyy-MM-ddTHH:mmK",
            "yyyy-MM-ddTHH:mm:ssK",
            "yyyyMMddHHmmssK",
            "yyyyMMddHHmmK",
            "yyyyMMddTHHmmssFFFFFFFK",
            "THHmmss",
            "THHmm",
            "HH:mm:ss.FFFFFFF",
            "HH:mm:ss",
            "HH:mm",
            "yyyy-MM-dd HH:mm:ss.FFFFFFF", /* NOTE: Non-UTC default (19). */
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyyMMddHHmmss",
            "yyyyMMddHHmm",
            "yyyyMMddTHHmmssFFFFFFF",
            "yyyy-MM-dd",
            "yyyyMMdd",
            "yy-MM-dd"
        };

        private static string _datetimeFormatUtc = _datetimeFormats[5];
        private static string _datetimeFormatLocal = _datetimeFormats[19];

        public static void RunQueries(this IDatabaseConnection connection, string[] queries)
        {
            connection.RunInTransaction(conn =>
            {
                conn.ExecuteAll(string.Join(";", queries));
            }, TransactionMode.Deferred);
        }

        public static bool TableExists(this IDatabaseConnection connection, string tableName)
        {
            using (var statement = connection.PrepareStatement($"select 1 from sqlite_master where tbl_name = '{tableName}'"))
            {
                return statement.MoveNext();
            }
        }

        public static ReadOnlySpan<byte> ToGuidBlob(this Guid guid)
        {
            return guid.ToByteArray().AsSpan();
        }

        public static Guid GetGuid(this ResultSet result, int index)
        {
#if EMBY
#if NETCOREAPP
            return new Guid(result.GetBlob(index));
#else
            return new Guid(result.GetBlob(index).ToArray());
#endif
#else
            return new Guid(result[index].ToBlob().ToArray());
#endif
        }

#if JELLYFIN
        public static bool IsDBNull(this IReadOnlyList<ResultSetValue> result, int index)
        {
            return result[index].SQLiteType == SQLiteType.Null;
        }

        public static string GetString(this IReadOnlyList<ResultSetValue> result, int index)
        {
            return result[index].ToString();
        }

        public static bool GetBoolean(this IReadOnlyList<ResultSetValue> result, int index)
        {
            return result[index].ToBool();
        }

        public static int GetInt(this IReadOnlyList<ResultSetValue> result, int index)
        {
            return result[index].ToInt();
        }

        public static long GetInt64(this IReadOnlyList<ResultSetValue> result, int index)
        {
            return result[index].ToInt64();
        }

        public static float GetFloat(this IReadOnlyList<ResultSetValue> result, int index)
        {
            return result[index].ToFloat();
        }
#endif

        private static void ThrowInvalidParamName(IStatement statement, string name)
        {
#if DEBUG
            throw new Exception("Invalid param name: " + name + ". SQL: " + statement.SQL);
#endif
        }

        public static void TryBind(this IStatement statement, int index, double value)
        {
            IBindParameter bindParam = statement.BindParameters[index];
            bindParam.Bind(value);
        }

        public static void TryBind(this IStatement statement, string name, double value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                bindParam.Bind(value);
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, string name, string value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                if (value == null)
                {
                    bindParam.BindNull();
                }
                else
                {
#if EMBY
                    bindParam.Bind(value.AsSpan());
#else
                    bindParam.Bind(value);
#endif
                }
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, int index, string value)
        {
            IBindParameter bindParam = statement.BindParameters[index];
            if (value == null)
            {
                bindParam.BindNull();
            }
            else
            {
#if EMBY
                    bindParam.Bind(value.AsSpan());
#else
                    bindParam.Bind(value);
#endif
            }
        }

        public static void TryBind(this IStatement statement, int index, bool value)
        {
            IBindParameter bindParam = statement.BindParameters[index];
            bindParam.Bind(value);
        }

        public static void TryBind(this IStatement statement, int index, bool? value)
        {
            IBindParameter bindParam = statement.BindParameters[index];
            if (value == null)
            {
                bindParam.BindNull();
            }
            else
            {
                bindParam.Bind(value.Value);
            }
        }

        public static void TryBind(this IStatement statement, string name, bool value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                bindParam.Bind(value);
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, int index, int value)
        {
            IBindParameter bindParam = statement.BindParameters[index];
            bindParam.Bind(value);
        }

        public static void TryBind(this IStatement statement, string name, int value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                bindParam.Bind(value);
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, string name, int? value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                if (value == null)
                {
                    bindParam.BindNull();
                }
                else
                {
                    bindParam.Bind(value.Value);
                }
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, int index, Guid value)
        {
            IBindParameter bindParam = statement.BindParameters[index];
            bindParam.Bind(value.ToGuidBlob());
        }

        public static void TryBind(this IStatement statement, string name, Guid value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                bindParam.Bind(value.ToGuidBlob());
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, string name, Guid? value)
        {
            if (value.HasValue)
            {
                TryBind(statement, name, value.Value);
            }
            else
            {
                TryBindNull(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, string name, ReadOnlySpan<byte> value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                bindParam.Bind(value);
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, int index, DateTimeOffset? value)
        {
            if (value.HasValue)
            {
                TryBind(statement, index, value.Value);
            }
            else
            {
                TryBindNull(statement, index);
            }
        }

        public static void TryBind(this IStatement statement, int index, DateTimeOffset value, bool enableMsPrecision)
        {
            IBindParameter bindParam = statement.BindParameters[index];

            if (enableMsPrecision)
            {
                bindParam.Bind(value.ToUnixTimeMilliseconds());
            }
            else
            {
                bindParam.Bind(value.ToUnixTimeSeconds());
            }
        }

        public static void TryBind(this IStatement statement, string name, DateTimeOffset value, bool enableMsPrecision)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                if (enableMsPrecision)
                {
                    bindParam.Bind(value.ToUnixTimeMilliseconds());
                }
                else
                {
                    bindParam.Bind(value.ToUnixTimeSeconds());
                }
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, int index, DateTimeOffset value)
        {
            TryBind(statement, index, value, false);
        }

        public static void TryBind(this IStatement statement, string name, DateTimeOffset value)
        {
            TryBind(statement, name, value, false);
        }

        public static void TryBind(this IStatement statement, int index, long value)
        {
            IBindParameter bindParam = statement.BindParameters[index];

            bindParam.Bind(value);
        }

        public static void TryBind(this IStatement statement, string name, long value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                bindParam.Bind(value);
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, string name, long? value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                if (value == null)
                {
                    bindParam.BindNull();
                }
                else
                {
                    bindParam.Bind(value.Value);
                }
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, int index, ReadOnlySpan<byte> value)
        {
            IBindParameter bindParam = statement.BindParameters[index];
            bindParam.Bind(value);
        }

        public static void TryBindNull(this IStatement statement, int index)
        {
            IBindParameter bindParam = statement.BindParameters[index];

            bindParam.BindNull();
        }

        public static void TryBindNull(this IStatement statement, string name)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                bindParam.BindNull();
            }
            else
            {
                ThrowInvalidParamName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, int index, double? value)
        {
            if (value.HasValue)
            {
                TryBind(statement, index, value.Value);
            }
            else
            {
                TryBindNull(statement, index);
            }
        }

        public static void TryBind(this IStatement statement, int index, int? value)
        {
            if (value.HasValue)
            {
                TryBind(statement, index, value.Value);
            }
            else
            {
                TryBindNull(statement, index);
            }
        }

        public static void TryBind(this IStatement statement, string name, bool? value)
        {
            if (value.HasValue)
            {
                TryBind(statement, name, value.Value);
            }
            else
            {
                TryBindNull(statement, name);
            }
        }

        public static IEnumerable<ResultSet> ExecuteQuery(this IStatement statement)
        {
            while (statement.MoveNext())
            {
                yield return statement.Current;
            }
        }
    }
}
