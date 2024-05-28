#if EMBY
using DatabaseConnection = SQLitePCL.pretty.IDatabaseConnection;
#else
using DatabaseConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

namespace InfuseSync.Storage.Migrations
{
    public interface IDbMigration
    {
        int DbVersion { get; }
        void Migrate(DatabaseConnection connection);
    }
}