#if EMBY
using DatabaseConnection = SQLitePCL.pretty.IDatabaseConnection;
#else
using DatabaseConnection = InfuseSync.Storage.ManagedConnection;
#endif

namespace InfuseSync.Storage.Migrations
{
    public interface IDbMigration
    {
        int DbVersion { get; }
        void Migrate(DatabaseConnection connection);
    }
}