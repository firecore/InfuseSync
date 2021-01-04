using SQLitePCL.pretty;

#if EMBY
using DatabaseConnection = SQLitePCL.pretty.IDatabaseConnection;
#else
using DatabaseConnection = InfuseSync.Storage.ManagedConnection;
#endif

namespace InfuseSync.Storage.Migrations
{
    public class MigrationChangeUserDataPrimaryKey : IDbMigration
    {
        public int DbVersion => 1;

        public void Migrate(DatabaseConnection connection)
        {
            connection.RunInTransaction(db =>
            {
                if (!db.TableExists("user_info"))
                {
                    return;
                }

                string createQuery =
#if EMBY
                    "create table if not exists user_info_tmp(Id TEXT NOT NULL, Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (Id, UserId))";
#else
                    "create table if not exists user_info_tmp(Guid GUID NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (Guid, UserId))";
#endif
                db.Execute(createQuery);
                db.Execute("insert into user_info_tmp select * from user_info");
                db.Execute("drop table user_info");
                db.Execute("alter table user_info_tmp rename to user_info");

                string createIndex =
#if EMBY
                    "create index if not exists idx_user_info on user_info(Id, UserId)";
#else
                    "create index if not exists idx_user_info on user_info(Guid, UserId)";
#endif
                db.Execute(createIndex);
            }, TransactionMode.Deferred);
        }
    }
}