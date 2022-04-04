using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

#if EMBY
using InfuseSync.Logging;
using ILogger = MediaBrowser.Model.Logging.ILogger;
#else
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger<InfuseSync.ScheduledTasks.HousekeepingTask>;
#endif

namespace InfuseSync.ScheduledTasks
{
    public class HousekeepingTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public HousekeepingTask(ILogger logger)
        {
            _logger = logger;
            _logger.LogInformation("Infuse housekeeping task scheduled.");
        }

        public string Key => "InfuseHousekeepingTask";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[] {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromMinutes(1).Ticks
                }
            };
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var expirationDays = Plugin.Instance.Configuration.CacheExpirationDays;
            if (expirationDays == 0) {
                return Task.CompletedTask;
            }

            var dateTime = DateTime.UtcNow.AddDays(-expirationDays);
            Plugin.Instance.Db.DeleteOldData(dateTime.ToFileTime());

            return Task.CompletedTask;
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            return Execute(cancellationToken, progress);
        }

        public string Name => "Remove Old Cached Data";
        public string Category => "Infuse Sync";
        public string Description => "Removes old sync records based on 'Delete unused cache data' setting.";
    }
}
