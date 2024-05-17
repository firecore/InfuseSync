using System;
using System.Collections.Generic;
using System.Linq;

#if EMBY
using MediaBrowser.Model.Logging;
using InfuseSync.Logging;
using SQLitePCL.pretty;
using DatabaseConnection = SQLitePCL.pretty.IDatabaseConnection;
#else
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using DatabaseConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

namespace InfuseSync.Storage.Migrations
{
    public class DbVersionManager: IDisposable
    {
        private const int DbVersion = 2;

        public readonly Dictionary<int, IDbMigration> migrations;

        private readonly ILogger _logger;

        public DbVersionManager(ILogger logger)
        {
            _logger = logger;

            migrations = new IDbMigration[] {
                new MigrationDropBetaDatabase(),
                new MigrationChangeUserDataPrimaryKey()
            }
            .ToDictionary(m => m.DbVersion, m => m);
        }

        public void UpdateVersion(DatabaseConnection connection, bool dbJustCreated)
        {
            if (dbJustCreated)
            {
                connection.Execute($"PRAGMA user_version = {DbVersion};");
                return;
            }

            int version;
            using (var versionStatement = connection.PrepareStatement("PRAGMA user_version;"))
            {
                var userVersion = versionStatement.SelectScalarInt();
                if (userVersion == null)
                {
                    connection.Execute($"PRAGMA user_version = {DbVersion};");
                }
                version = userVersion ?? DbVersion;
            }

            if (version >= DbVersion)
            {
                return;
            }

            _logger.LogInformation($"InfuseSync: DB version {version} is outdated. Will migrate to version {DbVersion}.");

            for (var migrateVersion = version; migrateVersion < DbVersion; ++migrateVersion)
            {
                var migration = migrations[migrateVersion];
                if (migration != null)
                {
                    _logger.LogInformation($"InfuseSync: Performing migration for DB version {migrateVersion}.");
                    migration.Migrate(connection);
                }
                else
                {
                    _logger.LogInformation($"InfuseSync: Migration not found for DB version {migrateVersion}.");
                }
            }

            connection.Execute($"PRAGMA user_version = {DbVersion};");

            _logger.LogInformation($"InfuseSync: DB migration finished.");
        }

        public void Dispose()
        {
        }
    }
}