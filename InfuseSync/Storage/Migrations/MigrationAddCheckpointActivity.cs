using System;

#if EMBY
using SQLitePCL.pretty;
using DatabaseConnection = SQLitePCL.pretty.IDatabaseConnection;
#else
using Microsoft.Data.Sqlite;
using DatabaseConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

namespace InfuseSync.Storage.Migrations
{
    public class MigrationAddCheckpointActivity : IDbMigration
    {
        public int DbVersion => 3;

        public void Migrate(DatabaseConnection connection)
        {
            connection.RunInTransaction(db =>
            {
                if (!db.TableExists("checkpoints"))
                {
                    return;
                }

                var hasLastActivity = false;
                using (var statement = db.PrepareStatement("pragma table_info(checkpoints);"))
                {
                    foreach (var row in statement.ExecuteQuery())
                    {
                        if (string.Equals(row.GetString(1), "LastActivity", StringComparison.OrdinalIgnoreCase))
                        {
                            hasLastActivity = true;
                            break;
                        }
                    }
                }

                if (!hasLastActivity)
                {
                    db.Execute("alter table checkpoints add column LastActivity INTEGER NOT NULL DEFAULT 0");
                }

                using (var statement = db.PrepareStatement("update checkpoints set LastActivity=@LastActivity where LastActivity=0;"))
                {
                    statement.TryBind("@LastActivity", DateTime.UtcNow.ToFileTime());
                    statement.ExecuteNonQuery();
                }
            });
        }
    }
}
