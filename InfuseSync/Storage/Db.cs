using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Serialization;
using SQLitePCL.pretty;
using InfuseSync.Models;

#if EMBY
using MediaBrowser.Model.Logging;
#else
using Microsoft.Extensions.Logging;
#endif

namespace InfuseSync.Storage
{
    public class Db: BaseSqliteRepository, IDisposable
    {
        private const string CheckpointsTable = "checkpoints";
        private const string ItemsTable = "items";
        private const string UserInfoTable = "user_info";

        private readonly IJsonSerializer _serializer;

        public Db(string path, ILogger logger, IJsonSerializer jsonSerializer) : base(logger)
        {
            _serializer = jsonSerializer;
            Directory.CreateDirectory(path);
            DbFilePath = Path.Combine(path, $"infuse_sync.db");
            Initialize(File.Exists(DbFilePath));
        }

        public void Initialize(bool fileExists)
        {
            using (var connection = CreateConnection())
            {
#if EMBY
                using (var versionManager = new Migrations.DbVersionManager(_logger))
#else
               using (var versionManager = new Migrations.DbVersionManager(_logger))
#endif
                {
                    versionManager.UpdateVersion(connection, !fileExists);
                }

                RunDefaultInitialization(connection);

                string[] queries = {
                    $"create table if not exists {CheckpointsTable} (Guid GUID PRIMARY KEY, DeviceId TEXT NOT NULL, UserId TEXT NOT NULL, Timestamp INTEGER NOT NULL, SyncTimestamp INTEGER NULL)",
                    $"create index if not exists idx_{CheckpointsTable} on {CheckpointsTable}(Guid)",
                    $"create index if not exists idx_{CheckpointsTable}_device_user on {CheckpointsTable}(DeviceId, UserId)",
#if EMBY
                    $"create table if not exists {ItemsTable} (Id TEXT PRIMARY KEY, Guid GUID NOT NULL, SeriesId INTEGER NULL, Season INTEGER NULL, Status INTEGER NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL)",
                    $"create index if not exists idx_{ItemsTable} on {ItemsTable}(Id)",
                    $"create table if not exists {UserInfoTable} (Id TEXT NOT NULL, Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (Id, UserId))",
                    $"create index if not exists idx_{UserInfoTable} on {UserInfoTable}(Id, UserId)"
#else
                    $"create table if not exists {ItemsTable} (Guid GUID PRIMARY KEY, SeriesId GUID NULL, Season INTEGER NULL, Status INTEGER NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL)",
                    $"create index if not exists idx_{ItemsTable} on {ItemsTable}(Guid)",
                    $"create table if not exists {UserInfoTable} (Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (Guid, UserId))",
                    $"create index if not exists idx_{UserInfoTable} on {UserInfoTable}(Guid, UserId)"
#endif
                };

                connection.RunQueries(queries);
            }
        }

        public Checkpoint GetCheckpoint(Guid checkpointId)
        {
            using (WriteLock.Read())
            {
                using (var connection = CreateConnection(true))
                {
                    using (var statement = connection.PrepareStatement($"select * from {CheckpointsTable} where Guid=@Guid;"))
                    {
                        statement.TryBind("@Guid", checkpointId);
                        foreach (var row in statement.ExecuteQuery())
                        {
                            return new Checkpoint
                            {
                                Guid = row.GetGuid(0),
                                DeviceId = row.GetString(1),
                                UserId = row.GetString(2),
                                Timestamp = row.GetInt64(3),
                                SyncTimestamp = row.IsDBNull(4) ? null : (long?)row.GetInt64(4)
                            };
                        }
                    }

                    return null;
                }
            }
        }

        public bool HasCheckpoints()
        {
            using (WriteLock.Read())
            {
                using (var connection = CreateConnection(true))
                {
                    using (var statement = connection.PrepareStatement($"select exists(select 1 from {CheckpointsTable});"))
                    {
                        return statement.ExecuteQuery().First().GetInt(0) == 1;
                    }
                }
            }
        }

        public Checkpoint CreateCheckpoint(string deviceId, string userId)
        {
            using (WriteLock.Write())
            {
                using (var connection = CreateConnection(true))
                {
                    return connection.RunInTransaction(db =>
                    {
                        long timestamp = DateTime.UtcNow.ToFileTime();
                        using (var statement = connection.PrepareStatement($"select max(SyncTimestamp) from {CheckpointsTable} where DeviceId=@DeviceId and UserId=@UserId;"))
                        {
                            statement.TryBind("@DeviceId", deviceId);
                            statement.TryBind("@UserId", userId);
                            if (statement.MoveNext() && !statement.Current.IsDBNull(0))
                            {
                                timestamp = statement.Current.GetInt64(0);
                            }
                        }

                        using (var statement = db.PrepareStatement($"delete from {CheckpointsTable} where DeviceId=@DeviceId and UserId=@UserId;"))
                        {
                            statement.TryBind("@DeviceId", deviceId);
                            statement.TryBind("@UserId", userId);
                            statement.MoveNext();
                        }

                        var guid = Guid.NewGuid();

                        using (var statement = db.PrepareStatement($"insert into {CheckpointsTable}(Guid, DeviceId, UserId, Timestamp) values (@Guid, @DeviceId, @UserId, @Timestamp);"))
                        {
                            statement.TryBind("@Guid", guid);
                            statement.TryBind("@DeviceId", deviceId);
                            statement.TryBind("@UserId", userId);
                            statement.TryBind("@Timestamp", timestamp);
                            statement.MoveNext();
                        }

                        return new Checkpoint
                        {
                            Guid = guid,
                            DeviceId = deviceId,
                            UserId = userId,
                            Timestamp = timestamp,
                            SyncTimestamp = null
                        };
                    }, TransactionMode);
                }
            }
        }

        public void UpdateCheckpoint(Guid checkpointId, long syncTimestamp)
        {
            using (WriteLock.Write())
            {
                using (var connection = CreateConnection())
                {
                    connection.RunInTransaction(db =>
                    {
                        using (var statement = db.PrepareStatement($"update {CheckpointsTable} set SyncTimestamp=@SyncTimestamp where Guid=@Guid;"))
                        {
                            statement.TryBind("@SyncTimestamp", syncTimestamp);
                            statement.TryBind("@Guid", checkpointId);
                            statement.MoveNext();
                        }
                    }, TransactionMode);
                }
            }
        }

        public void RemoveCheckpoint(Guid checkpointId)
        {
            using (WriteLock.Write())
            {
                using (var connection = CreateConnection())
                {
                    connection.RunInTransaction(db =>
                    {
                        using (var statement = db.PrepareStatement($"delete from {CheckpointsTable} where Guid=@Guid;"))
                        {
                            statement.TryBind("@Guid", checkpointId);
                            statement.MoveNext();
                        }
                    }, TransactionMode);
                }
            }
        }

        public List<ItemRec> GetItems(
            long fromTimestamp,
            long toTimestamp,
            ItemStatus status,
            IReadOnlyCollection<string> itemTypes,
            int skip,
            int limit)
        {
            using (WriteLock.Read())
            {
                using (var connection = CreateConnection(true))
                {
                    var condition = ItemsCondition(itemTypes);
                    var sql = $"select * from {ItemsTable} where {condition} limit @Limit OFFSET @Offset;";

                    using (var statement = connection.PrepareStatement(sql))
                    {
                        statement.TryBind("@FromTimestamp", fromTimestamp);
                        statement.TryBind("@ToTimestamp", toTimestamp);
                        statement.TryBind("@Status", (int)status);
                        statement.TryBind("@Limit", limit);
                        statement.TryBind("@Offset", skip);

                        return GetItems(statement);
                    }
                }
            }
        }

        private List<ItemRec> GetItems(IStatement statement)
        {
            var result = new List<ItemRec>();

            foreach (var row in statement.ExecuteQuery())
            {
                var item = new ItemRec
                {
#if EMBY
                    ItemId = row.GetString(0),
                    Guid = row.GetGuid(1),
                    SeriesId = row.IsDBNull(2) ? null : (long?)row.GetInt64(2),
                    Season = row.IsDBNull(3) ? null : (int?)row.GetInt(3),
                    Status = (ItemStatus)row.GetInt(4),
                    LastModified = row.GetInt64(5),
                    Type = row.GetString(6)
#else
                    Guid = row.GetGuid(0),
                    SeriesId = row.IsDBNull(1) ? null : (Guid?)row.GetGuid(1),
                    Season = row.IsDBNull(2) ? null : (int?)row.GetInt(2),
                    Status = (ItemStatus)row.GetInt(3),
                    LastModified = row.GetInt64(4),
                    Type = row.GetString(5)
#endif
                };
                result.Add(item);
            }

            return result;
        }

        public int ItemsCount(
            long fromTimestamp,
            long toTimestamp,
            ItemStatus status,
            IReadOnlyCollection<string> itemTypes)
        {
            using (WriteLock.Read())
            {
                using (var connection = CreateConnection(true))
                {
                    var condition = ItemsCondition(itemTypes);
                    var sql = $"select COUNT(*) from {ItemsTable} where {condition};";

                    using (var statement = connection.PrepareStatement(sql))
                    {
                        statement.TryBind("@FromTimestamp", fromTimestamp);
                        statement.TryBind("@ToTimestamp", toTimestamp);
                        statement.TryBind("@Status", (int)status);
                        return statement.ExecuteQuery().First().GetInt(0);
                    }
                }
            }
        }

        private string ItemsCondition(IReadOnlyCollection<string> itemTypes)
        {
            var condition = $"Status = @Status and LastModified between @FromTimestamp and @ToTimestamp";
            if (itemTypes != null && itemTypes.Count > 0)
            {
                condition += $" and Type in ('{String.Join("','", itemTypes.ToArray())}')";
            }
            return condition;
        }

        public List<UserInfoRec> GetUserInfos(
            long fromTimestamp,
            long toTimestamp,
            string userId,
            IReadOnlyCollection<string> itemTypes,
            int skip,
            int limit)
        {
            using (WriteLock.Read())
            {
                using (var connection = CreateConnection(true))
                {
                    var condition = UserInfoCondition(itemTypes);
                    var sql = $"select * from {UserInfoTable} where {condition} limit @Limit OFFSET @Offset;";

                    using (var statement = connection.PrepareStatement(sql))
                    {
                        statement.TryBind("@FromTimestamp", fromTimestamp);
                        statement.TryBind("@ToTimestamp", toTimestamp);
                        statement.TryBind("@UserId", userId);
                        statement.TryBind("@Limit", limit);
                        statement.TryBind("@Offset", skip);

                        return GetUserInfos(statement);
                    }
                }
            }
        }

        private List<UserInfoRec> GetUserInfos(IStatement statement)
        {
            var result = new List<UserInfoRec>();

            foreach (var row in statement.ExecuteQuery())
            {
                var item = new UserInfoRec
                {
#if EMBY
                    ItemId = row.GetString(0),
                    Guid = row.GetGuid(1),
                    UserId = row.GetString(2),
                    LastModified = row.GetInt64(3),
                    Type = row.GetString(4)
#else
                    Guid = row.GetGuid(0),
                    UserId = row.GetString(1),
                    LastModified = row.GetInt64(2),
                    Type = row.GetString(3)
#endif
                };
                result.Add(item);
            }

            return result;
        }

        public int UserInfoCount(
            long fromTimestamp,
            long toTimestamp,
            string userId,
            IReadOnlyCollection<string> itemTypes)
        {
            using (WriteLock.Read())
            {
                using (var connection = CreateConnection(true))
                {
                    var condition = UserInfoCondition(itemTypes);
                    var sql = $"select COUNT(*) from {UserInfoTable} where {condition};";

                    using (var statement = connection.PrepareStatement(sql))
                    {
                        statement.TryBind("@FromTimestamp", fromTimestamp);
                        statement.TryBind("@ToTimestamp", toTimestamp);
                        statement.TryBind("@UserId", userId);
                        return statement.ExecuteQuery().First().GetInt(0);
                    }
                }
            }
        }

        private string UserInfoCondition(IReadOnlyCollection<string> itemTypes)
        {
            var condition = $"UserId = @UserId and LastModified between @FromTimestamp and @ToTimestamp";
            if (itemTypes != null && itemTypes.Count > 0)
            {
                condition += $" and Type in ('{String.Join("','", itemTypes.ToArray())}')";
            }
            return condition;
        }

        public void DeleteOldData(long timestamp)
        {
            using (WriteLock.Write())
            {
                using (var connection = CreateConnection())
                {
                    connection.RunInTransaction(db =>
                    {
                        using (var statement = db.PrepareStatement($"delete from {CheckpointsTable} where Timestamp < @Timestamp;"))
                        {
                            statement.TryBind("@Timestamp", timestamp);
                            statement.MoveNext();
                        }

                        bool hasSessions;
                        using (var statement = connection.PrepareStatement($"select exists(select 1 from {CheckpointsTable});"))
                        {
                            hasSessions = statement.ExecuteQuery().First().GetInt(0) == 1;
                        }

                        if (hasSessions)
                        {
                            long minTimestamp;
                            using (var statement = connection.PrepareStatement($"select MIN(Timestamp) from {CheckpointsTable};"))
                            {
                                minTimestamp = statement.ExecuteQuery().First().GetInt64(0);
                            }
                            using (var statement = db.PrepareStatement($"delete from {ItemsTable} where LastModified < @Timestamp;"))
                            {
                                statement.TryBind("@Timestamp", timestamp);
                                statement.MoveNext();
                            }
                            using (var statement = db.PrepareStatement($"delete from {UserInfoTable} where LastModified < @Timestamp;"))
                            {
                                statement.TryBind("@Timestamp", timestamp);
                                statement.MoveNext();
                            }
                        }
                        else
                        {
                            using (var statement = db.PrepareStatement($"delete from {ItemsTable};"))
                            {
                                statement.MoveNext();
                            }
                            using (var statement = db.PrepareStatement($"delete from {UserInfoTable};"))
                            {
                                statement.MoveNext();
                            }
                        }
                    }, TransactionMode);
                }
            }
        }

        public void SaveItems(IEnumerable<ItemRec> items)
        {
            using (WriteLock.Write())
            {
                using (var connection = CreateConnection())
                {
                    connection.RunInTransaction(db =>
                    {
#if EMBY
                        var sql = $"insert or replace into {ItemsTable} values (@Id, @Guid, @SeriesId, @Season, @Status, @LastModified, @Type);";
#else
                        var sql = $"insert or replace into {ItemsTable} values (@Guid, @SeriesId, @Season, @Status, @LastModified, @Type);";
#endif
                        foreach (var i in items)
                        {
                            using (var statement = db.PrepareStatement(sql))
                            {
#if EMBY
                                statement.TryBind("@Id", i.ItemId);
#endif
                                statement.TryBind("@Guid", i.Guid);
                                statement.TryBind("@SeriesId", i.SeriesId);
                                statement.TryBind("@Season", i.Season);
                                statement.TryBind("@Status", (int)i.Status);
                                statement.TryBind("@LastModified", i.LastModified);
                                statement.TryBind("@Type", i.Type);
                                statement.MoveNext();
                            }
                        }
                    }, TransactionMode);
                }
            }
        }

        public void SaveUserInfo(List<UserInfoRec> infoRecs)
        {
            using (WriteLock.Write())
            {
                using (var connection = CreateConnection())
                {
                    connection.RunInTransaction(db =>
                    {
#if EMBY
                        var sql = $"insert or replace into {UserInfoTable} values (@Id, @Guid, @UserId, @LastModified, @Type);";
#else
                        var sql = $"insert or replace into {UserInfoTable} values (@Guid, @UserId, @LastModified, @Type);";
#endif
                        foreach (var i in infoRecs)
                        {
                            using (var statement = db.PrepareStatement(sql))
                            {
#if EMBY
                                statement.TryBind("@Id", i.ItemId);
#endif
                                statement.TryBind("@Guid", i.Guid);
                                statement.TryBind("@UserId", i.UserId);
                                statement.TryBind("@LastModified", i.LastModified);
                                statement.TryBind("@Type", i.Type);
                                statement.MoveNext();
                            }
                        }
                    }, TransactionMode);
                }
            }
        }
    }
}
