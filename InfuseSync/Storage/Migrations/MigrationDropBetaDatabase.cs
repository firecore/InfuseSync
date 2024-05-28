using System.Collections.Generic;
using System.Linq;

#if EMBY
using SQLitePCL.pretty;
using DatabaseConnection = SQLitePCL.pretty.IDatabaseConnection;
#else
using Microsoft.Data.Sqlite;
using DatabaseConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

namespace InfuseSync.Storage.Migrations
{
    public class MigrationDropBetaDatabase : IDbMigration
    {
        public int DbVersion => 0;

        public void Migrate(DatabaseConnection connection)
        {
            connection.RunInTransaction(db => {
                var selectTable = "select name from sqlite_master where type='table' and name not like 'sqlite_%';";
                List<string> tables;
                using (var entitiesStatetment = db.PrepareStatement(selectTable))
                {
                    tables = entitiesStatetment.ExecuteQuery().Select(row => row.GetString(0)).ToList();
                }

                foreach (var table in tables)
                {
                    db.Execute($"drop table '{table}';");
                }

                var selectIndex = "select name from sqlite_master where type='index' and name not like 'sqlite_%';";
                List<string> indexes;
                using (var indexesStatetment = db.PrepareStatement(selectIndex))
                {
                    indexes = indexesStatetment.ExecuteQuery().Select(row => row.GetString(0)).ToList();
                }

                foreach (var index in indexes)
                {
                    db.Execute($"drop index '{index}';");
                }
            });
        }
    }
}