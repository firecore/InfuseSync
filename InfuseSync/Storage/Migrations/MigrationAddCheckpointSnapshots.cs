#if EMBY
using SQLitePCL.pretty;
using DatabaseConnection = SQLitePCL.pretty.IDatabaseConnection;
#else
using Microsoft.Data.Sqlite;
using DatabaseConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

namespace InfuseSync.Storage.Migrations
{
    public class MigrationAddCheckpointSnapshots : IDbMigration
    {
        public int DbVersion => 2;

        public void Migrate(DatabaseConnection connection)
        {
            connection.RunInTransaction(db =>
            {
#if EMBY
                db.Execute("create table if not exists checkpoint_items (CheckpointId GUID NOT NULL, Id TEXT NOT NULL, Guid GUID NOT NULL, SeriesId INTEGER NULL, Season INTEGER NULL, Status INTEGER NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (CheckpointId, Id))");
                db.Execute("create index if not exists idx_checkpoint_items_page on checkpoint_items(CheckpointId, Status, LastModified, Id, Type)");
                db.Execute("create table if not exists checkpoint_user_info (CheckpointId GUID NOT NULL, Id TEXT NOT NULL, Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (CheckpointId, Id))");
                db.Execute("create index if not exists idx_checkpoint_user_info_page on checkpoint_user_info(CheckpointId, LastModified, Id, Type)");
#else
                db.Execute("create table if not exists checkpoint_items (CheckpointId GUID NOT NULL, Guid GUID NOT NULL, SeriesId GUID NULL, Season INTEGER NULL, Status INTEGER NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (CheckpointId, Guid))");
                db.Execute("create index if not exists idx_checkpoint_items_page on checkpoint_items(CheckpointId, Status, LastModified, Guid, Type)");
                db.Execute("create table if not exists checkpoint_user_info (CheckpointId GUID NOT NULL, Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (CheckpointId, Guid))");
                db.Execute("create index if not exists idx_checkpoint_user_info_page on checkpoint_user_info(CheckpointId, LastModified, Guid, Type)");
#endif

                if (!db.TableExists("checkpoints") || !db.TableExists("items") || !db.TableExists("user_info"))
                {
                    return;
                }

#if EMBY
                db.Execute("insert or ignore into checkpoint_items(CheckpointId, Id, Guid, SeriesId, Season, Status, LastModified, Type) select c.Guid, i.Id, i.Guid, i.SeriesId, i.Season, i.Status, i.LastModified, i.Type from checkpoints c join items i on i.LastModified between c.Timestamp and c.SyncTimestamp where c.SyncTimestamp is not null");
                db.Execute("insert or ignore into checkpoint_user_info(CheckpointId, Id, Guid, UserId, LastModified, Type) select c.Guid, u.Id, u.Guid, u.UserId, u.LastModified, u.Type from checkpoints c join user_info u on u.UserId = c.UserId and u.LastModified between c.Timestamp and c.SyncTimestamp where c.SyncTimestamp is not null");
#else
                db.Execute("insert or ignore into checkpoint_items(CheckpointId, Guid, SeriesId, Season, Status, LastModified, Type) select c.Guid, i.Guid, i.SeriesId, i.Season, i.Status, i.LastModified, i.Type from checkpoints c join items i on i.LastModified between c.Timestamp and c.SyncTimestamp where c.SyncTimestamp is not null");
                db.Execute("insert or ignore into checkpoint_user_info(CheckpointId, Guid, UserId, LastModified, Type) select c.Guid, u.Guid, u.UserId, u.LastModified, u.Type from checkpoints c join user_info u on u.UserId = c.UserId and u.LastModified between c.Timestamp and c.SyncTimestamp where c.SyncTimestamp is not null");
#endif
            });
        }
    }
}
