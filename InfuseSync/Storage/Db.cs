using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Serialization;
using InfuseSync.Models;

#if EMBY
using SQLitePCL.pretty;
using MediaBrowser.Model.Logging;
using DatabaseConnection = SQLitePCL.pretty.IDatabaseConnection;
using Statement = SQLitePCL.pretty.IStatement;
#else
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using DatabaseConnection = Microsoft.Data.Sqlite.SqliteConnection;
using Statement = Microsoft.Data.Sqlite.SqliteCommand;
#endif

namespace InfuseSync.Storage
{
    public class Db: BaseSqliteRepository, IDisposable
    {
        private const string CheckpointsTable = "checkpoints";
        private const string ItemsTable = "items";
        private const string UserInfoTable = "user_info";
        private const string CheckpointItemsTable = "checkpoint_items";
        private const string CheckpointUserInfoTable = "checkpoint_user_info";

        public Db(string path, ILogger logger) : base(logger)
        {
            Directory.CreateDirectory(path);
            DbFilePath = Path.Combine(path, $"infuse_sync.db");
            Initialize(File.Exists(DbFilePath));
        }

        public void Initialize(bool fileExists)
        {
            using (var connection = CreateConnection())
            {
                using (var versionManager = new Migrations.DbVersionManager(_logger))
                {
                    versionManager.UpdateVersion(connection, !fileExists);
                }

                RunDefaultInitialization(connection);

                string[] queries = {
                    $"create table if not exists {CheckpointsTable} (Guid GUID PRIMARY KEY, DeviceId TEXT NOT NULL, UserId TEXT NOT NULL, Timestamp INTEGER NOT NULL, SyncTimestamp INTEGER NULL, LastActivity INTEGER NOT NULL)",
                    $"create index if not exists idx_{CheckpointsTable} on {CheckpointsTable}(Guid)",
                    $"create index if not exists idx_{CheckpointsTable}_device_user on {CheckpointsTable}(DeviceId, UserId)",
#if EMBY
                    $"create table if not exists {ItemsTable} (Id TEXT PRIMARY KEY, Guid GUID NOT NULL, SeriesId INTEGER NULL, Season INTEGER NULL, Status INTEGER NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL)",
                    $"drop index if exists idx_{ItemsTable}",
                    $"create index if not exists idx_{ItemsTable}_modified on {ItemsTable}(LastModified)",
                    $"create table if not exists {UserInfoTable} (Id TEXT NOT NULL, Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (Id, UserId))",
                    $"drop index if exists idx_{UserInfoTable}",
                    $"create index if not exists idx_{UserInfoTable}_user_modified on {UserInfoTable}(UserId, LastModified)",
                    $"create table if not exists {CheckpointItemsTable} (CheckpointId GUID NOT NULL, Id TEXT NOT NULL, Guid GUID NOT NULL, SeriesId INTEGER NULL, Season INTEGER NULL, Status INTEGER NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (CheckpointId, Id))",
                    $"create index if not exists idx_{CheckpointItemsTable}_page on {CheckpointItemsTable}(CheckpointId, Status, LastModified, Id, Type)",
                    $"create table if not exists {CheckpointUserInfoTable} (CheckpointId GUID NOT NULL, Id TEXT NOT NULL, Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (CheckpointId, Id))",
                    $"create index if not exists idx_{CheckpointUserInfoTable}_page on {CheckpointUserInfoTable}(CheckpointId, LastModified, Id, Type)"
#else
                    $"create table if not exists {ItemsTable} (Guid GUID PRIMARY KEY, SeriesId GUID NULL, Season INTEGER NULL, Status INTEGER NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL)",
                    $"drop index if exists idx_{ItemsTable}",
                    $"create index if not exists idx_{ItemsTable}_modified on {ItemsTable}(LastModified)",
                    $"create table if not exists {UserInfoTable} (Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (Guid, UserId))",
                    $"drop index if exists idx_{UserInfoTable}",
                    $"create index if not exists idx_{UserInfoTable}_user_modified on {UserInfoTable}(UserId, LastModified)",
                    $"create table if not exists {CheckpointItemsTable} (CheckpointId GUID NOT NULL, Guid GUID NOT NULL, SeriesId GUID NULL, Season INTEGER NULL, Status INTEGER NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (CheckpointId, Guid))",
                    $"create index if not exists idx_{CheckpointItemsTable}_page on {CheckpointItemsTable}(CheckpointId, Status, LastModified, Guid, Type)",
                    $"create table if not exists {CheckpointUserInfoTable} (CheckpointId GUID NOT NULL, Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (CheckpointId, Guid))",
                    $"create index if not exists idx_{CheckpointUserInfoTable}_page on {CheckpointUserInfoTable}(CheckpointId, LastModified, Guid, Type)"
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
                    using (var statement = connection.PrepareStatement($"select Guid, DeviceId, UserId, Timestamp, SyncTimestamp, LastActivity from {CheckpointsTable} where Guid=@Guid;"))
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
                                SyncTimestamp = row.IsDBNull(4) ? null : (long?)row.GetInt64(4),
                                LastActivity = row.GetInt64(5)
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
                        return statement.SelectScalarInt() == 1;
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
                        var lastActivity = DateTime.UtcNow.ToFileTime();
                        long timestamp;
                        using (var statement = db.PrepareStatement($"select max(SyncTimestamp) from {CheckpointsTable} where DeviceId=@DeviceId and UserId=@UserId;"))
                        {
                            statement.TryBind("@DeviceId", deviceId);
                            statement.TryBind("@UserId", userId);

                            timestamp = statement.SelectScalarInt64() ?? DateTime.UtcNow.ToFileTime();
                        }

                        DeleteDeviceSnapshots(db, deviceId, userId);

                        using (var statement = db.PrepareStatement($"delete from {CheckpointsTable} where DeviceId=@DeviceId and UserId=@UserId;"))
                        {
                            statement.TryBind("@DeviceId", deviceId);
                            statement.TryBind("@UserId", userId);
                            statement.ExecuteNonQuery();
                        }

                        var guid = Guid.NewGuid();

                        using (var statement = db.PrepareStatement($"insert into {CheckpointsTable}(Guid, DeviceId, UserId, Timestamp, LastActivity) values (@Guid, @DeviceId, @UserId, @Timestamp, @LastActivity);"))
                        {
                            statement.TryBind("@Guid", guid);
                            statement.TryBind("@DeviceId", deviceId);
                            statement.TryBind("@UserId", userId);
                            statement.TryBind("@Timestamp", timestamp);
                            statement.TryBind("@LastActivity", lastActivity);
                            statement.ExecuteNonQuery();
                        }

                        return new Checkpoint
                        {
                            Guid = guid,
                            DeviceId = deviceId,
                            UserId = userId,
                            Timestamp = timestamp,
                            SyncTimestamp = null,
                            LastActivity = lastActivity
                        };
                    });
                }
            }
        }

        public Checkpoint StartSync(Guid checkpointId, long syncTimestamp)
        {
            using (WriteLock.Write())
            {
                using (var connection = CreateConnection())
                {
                    return connection.RunInTransaction(db =>
                    {
                        Checkpoint checkpoint = null;
                        using (var statement = db.PrepareStatement($"select Guid, DeviceId, UserId, Timestamp, SyncTimestamp, LastActivity from {CheckpointsTable} where Guid=@Guid;"))
                        {
                            statement.TryBind("@Guid", checkpointId);
                            foreach (var row in statement.ExecuteQuery())
                            {
                                checkpoint = new Checkpoint
                                {
                                    Guid = row.GetGuid(0),
                                    DeviceId = row.GetString(1),
                                    UserId = row.GetString(2),
                                    Timestamp = row.GetInt64(3),
                                    SyncTimestamp = row.IsDBNull(4) ? null : (long?)row.GetInt64(4),
                                    LastActivity = row.GetInt64(5)
                                };
                                break;
                            }
                        }

                        if (checkpoint == null)
                        {
                            return checkpoint;
                        }

                        var lastActivity = Math.Max(checkpoint.LastActivity, DateTime.UtcNow.ToFileTime());

                        if (checkpoint.SyncTimestamp.HasValue)
                        {
                            using (var statement = db.PrepareStatement($"update {CheckpointsTable} set LastActivity=@LastActivity where Guid=@Guid;"))
                            {
                                statement.TryBind("@LastActivity", lastActivity);
                                statement.TryBind("@Guid", checkpointId);
                                statement.ExecuteNonQuery();
                            }

                            checkpoint.LastActivity = lastActivity;
                            return checkpoint;
                        }

                        CreateSnapshot(db, checkpoint, syncTimestamp);

                        using (var statement = db.PrepareStatement($"update {CheckpointsTable} set SyncTimestamp=@SyncTimestamp, LastActivity=@LastActivity where Guid=@Guid;"))
                        {
                            statement.TryBind("@SyncTimestamp", syncTimestamp);
                            statement.TryBind("@LastActivity", lastActivity);
                            statement.TryBind("@Guid", checkpointId);
                            statement.ExecuteNonQuery();
                        }

                        checkpoint.SyncTimestamp = syncTimestamp;
                        checkpoint.LastActivity = lastActivity;
                        return checkpoint;
                    });
                }
            }
        }

        private void CreateSnapshot(DatabaseConnection db, Checkpoint checkpoint, long syncTimestamp)
        {
            DeleteCheckpointSnapshots(db, checkpoint.Guid);

#if EMBY
            var insertItems = $"insert into {CheckpointItemsTable}(CheckpointId, Id, Guid, SeriesId, Season, Status, LastModified, Type) select @CheckpointId, Id, Guid, SeriesId, Season, Status, LastModified, Type from {ItemsTable} where LastModified between @FromTimestamp and @ToTimestamp;";
            var insertUserInfo = $"insert into {CheckpointUserInfoTable}(CheckpointId, Id, Guid, UserId, LastModified, Type) select @CheckpointId, Id, Guid, UserId, LastModified, Type from {UserInfoTable} where UserId=@UserId and LastModified between @FromTimestamp and @ToTimestamp;";
#else
            var insertItems = $"insert into {CheckpointItemsTable}(CheckpointId, Guid, SeriesId, Season, Status, LastModified, Type) select @CheckpointId, Guid, SeriesId, Season, Status, LastModified, Type from {ItemsTable} where LastModified between @FromTimestamp and @ToTimestamp;";
            var insertUserInfo = $"insert into {CheckpointUserInfoTable}(CheckpointId, Guid, UserId, LastModified, Type) select @CheckpointId, Guid, UserId, LastModified, Type from {UserInfoTable} where UserId=@UserId and LastModified between @FromTimestamp and @ToTimestamp;";
#endif

            using (var statement = db.PrepareStatement(insertItems))
            {
                statement.TryBind("@CheckpointId", checkpoint.Guid);
                statement.TryBind("@FromTimestamp", checkpoint.Timestamp);
                statement.TryBind("@ToTimestamp", syncTimestamp);
                statement.ExecuteNonQuery();
            }

            using (var statement = db.PrepareStatement(insertUserInfo))
            {
                statement.TryBind("@CheckpointId", checkpoint.Guid);
                statement.TryBind("@UserId", checkpoint.UserId);
                statement.TryBind("@FromTimestamp", checkpoint.Timestamp);
                statement.TryBind("@ToTimestamp", syncTimestamp);
                statement.ExecuteNonQuery();
            }
        }

        private void DeleteDeviceSnapshots(DatabaseConnection db, string deviceId, string userId)
        {
            var checkpointQuery = $"select Guid from {CheckpointsTable} where DeviceId=@DeviceId and UserId=@UserId";

            foreach (var table in new[] { CheckpointItemsTable, CheckpointUserInfoTable })
            {
                using (var statement = db.PrepareStatement($"delete from {table} where CheckpointId in ({checkpointQuery});"))
                {
                    statement.TryBind("@DeviceId", deviceId);
                    statement.TryBind("@UserId", userId);
                    statement.ExecuteNonQuery();
                }
            }
        }

        private void DeleteCheckpointSnapshots(DatabaseConnection db, Guid checkpointId)
        {
            foreach (var table in new[] { CheckpointItemsTable, CheckpointUserInfoTable })
            {
                using (var statement = db.PrepareStatement($"delete from {table} where CheckpointId=@CheckpointId;"))
                {
                    statement.TryBind("@CheckpointId", checkpointId);
                    statement.ExecuteNonQuery();
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
                        DeleteCheckpointSnapshots(db, checkpointId);

                        using (var statement = db.PrepareStatement($"delete from {CheckpointsTable} where Guid=@Guid;"))
                        {
                            statement.TryBind("@Guid", checkpointId);
                            statement.ExecuteNonQuery();
                        }
                    });
                }
            }
        }

        public List<ItemRec> GetItems(
            Guid checkpointId,
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
#if EMBY
                    var sql = $"select Id, Guid, SeriesId, Season, Status, LastModified, Type from {CheckpointItemsTable} where {condition} order by LastModified, Id limit @Limit OFFSET @Offset;";
#else
                    var sql = $"select Guid, SeriesId, Season, Status, LastModified, Type from {CheckpointItemsTable} where {condition} order by LastModified, Guid limit @Limit OFFSET @Offset;";
#endif

                    using (var statement = connection.PrepareStatement(sql))
                    {
                        statement.TryBind("@CheckpointId", checkpointId);
                        statement.TryBind("@Status", (int)status);
                        statement.TryBind("@Limit", limit);
                        statement.TryBind("@Offset", skip);

                        return GetItems(statement);
                    }
                }
            }
        }

        private List<ItemRec> GetItems(Statement statement)
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
            Guid checkpointId,
            ItemStatus status,
            IReadOnlyCollection<string> itemTypes)
        {
            using (WriteLock.Read())
            {
                using (var connection = CreateConnection(true))
                {
                    var condition = ItemsCondition(itemTypes);
                    var sql = $"select COUNT(*) from {CheckpointItemsTable} where {condition};";

                    using (var statement = connection.PrepareStatement(sql))
                    {
                        statement.TryBind("@CheckpointId", checkpointId);
                        statement.TryBind("@Status", (int)status);
                        return statement.SelectScalarInt() ?? 0;
                    }
                }
            }
        }

        private string ItemsCondition(IReadOnlyCollection<string> itemTypes)
        {
            var condition = $"CheckpointId = @CheckpointId and Status = @Status";
            if (itemTypes != null && itemTypes.Count > 0)
            {
                condition += $" and Type in ('{String.Join("','", itemTypes.ToArray())}')";
            }
            return condition;
        }

        public List<UserInfoRec> GetUserInfos(
            Guid checkpointId,
            IReadOnlyCollection<string> itemTypes,
            int skip,
            int limit)
        {
            using (WriteLock.Read())
            {
                using (var connection = CreateConnection(true))
                {
                    var condition = UserInfoCondition(itemTypes);
#if EMBY
                    var sql = $"select Id, Guid, UserId, LastModified, Type from {CheckpointUserInfoTable} where {condition} order by LastModified, Id limit @Limit OFFSET @Offset;";
#else
                    var sql = $"select Guid, UserId, LastModified, Type from {CheckpointUserInfoTable} where {condition} order by LastModified, Guid limit @Limit OFFSET @Offset;";
#endif

                    using (var statement = connection.PrepareStatement(sql))
                    {
                        statement.TryBind("@CheckpointId", checkpointId);
                        statement.TryBind("@Limit", limit);
                        statement.TryBind("@Offset", skip);

                        return GetUserInfos(statement);
                    }
                }
            }
        }

        private List<UserInfoRec> GetUserInfos(Statement statement)
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
            Guid checkpointId,
            IReadOnlyCollection<string> itemTypes)
        {
            using (WriteLock.Read())
            {
                using (var connection = CreateConnection(true))
                {
                    var condition = UserInfoCondition(itemTypes);
                    var sql = $"select COUNT(*) from {CheckpointUserInfoTable} where {condition};";

                    using (var statement = connection.PrepareStatement(sql))
                    {
                        statement.TryBind("@CheckpointId", checkpointId);
                        return statement.SelectScalarInt() ?? 0;
                    }
                }
            }
        }

        private string UserInfoCondition(IReadOnlyCollection<string> itemTypes)
        {
            var condition = $"CheckpointId = @CheckpointId";
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
                        var oldCheckpointQuery = $"select Guid from {CheckpointsTable} where LastActivity < @Timestamp";
                        foreach (var table in new[] { CheckpointItemsTable, CheckpointUserInfoTable })
                        {
                            using (var statement = db.PrepareStatement($"delete from {table} where CheckpointId in ({oldCheckpointQuery});"))
                            {
                                statement.TryBind("@Timestamp", timestamp);
                                statement.ExecuteNonQuery();
                            }
                        }

                        using (var statement = db.PrepareStatement($"delete from {CheckpointsTable} where LastActivity < @Timestamp;"))
                        {
                            statement.TryBind("@Timestamp", timestamp);
                            statement.ExecuteNonQuery();
                        }

                        foreach (var table in new[] { CheckpointItemsTable, CheckpointUserInfoTable })
                        {
                            using (var statement = db.PrepareStatement($"delete from {table} where not exists (select 1 from {CheckpointsTable} where Guid={table}.CheckpointId);"))
                            {
                                statement.ExecuteNonQuery();
                            }
                        }

                        long? oldestCheckpointTimestamp;
                        using (var statement = db.PrepareStatement($"select MIN(COALESCE(SyncTimestamp, Timestamp)) from {CheckpointsTable};"))
                        {
                            oldestCheckpointTimestamp = statement.SelectScalarInt64();
                        }

                        if (oldestCheckpointTimestamp.HasValue)
                        {
                            using (var statement = db.PrepareStatement($"delete from {ItemsTable} where LastModified < @Timestamp;"))
                            {
                                statement.TryBind("@Timestamp", oldestCheckpointTimestamp.Value);
                                statement.ExecuteNonQuery();
                            }
                            using (var statement = db.PrepareStatement($"delete from {UserInfoTable} where LastModified < @Timestamp;"))
                            {
                                statement.TryBind("@Timestamp", oldestCheckpointTimestamp.Value);
                                statement.ExecuteNonQuery();
                            }
                        }
                        else
                        {
                            using (var statement = db.PrepareStatement($"delete from {ItemsTable};"))
                            {
                                statement.ExecuteNonQuery();
                            }
                            using (var statement = db.PrepareStatement($"delete from {UserInfoTable};"))
                            {
                                statement.ExecuteNonQuery();
                            }
                        }
                    });
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
                                statement.ExecuteNonQuery();
                            }
                        }
                    });
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
                                statement.ExecuteNonQuery();
                            }
                        }
                    });
                }
            }
        }
    }
}
