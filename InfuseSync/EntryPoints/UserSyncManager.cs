using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using InfuseSync.Models;

#if EMBY
using MediaBrowser.Model.Logging;
using InfuseSync.Logging;
#else
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
#endif

namespace InfuseSync.EntryPoints
{
    public class UserSyncManager : IServerEntryPoint
    {
#if EMBY
        private readonly ILogger _logger;
#else
        private readonly ILogger<UserSyncManager> _logger;
#endif
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;

        private readonly object _syncLock = new object();
        private Timer UpdateTimer { get; set; }
        private const int UpdateDuration = 500;

        private readonly Dictionary<Guid, List<BaseItem>> _changedItems = new Dictionary<Guid, List<BaseItem>>();

#if EMBY
        public UserSyncManager(IUserDataManager userDataManager, ILogger logger, IUserManager userManager)
#else
        public UserSyncManager(IUserDataManager userDataManager, ILogger<UserSyncManager> logger, IUserManager userManager)
#endif
        {
            _userDataManager = userDataManager;
            _logger = logger;
            _userManager = userManager;
        }

#if EMBY
        public void Run()
        {
            _userDataManager.UserDataSaved += UserDataSaved;
        }
#else
        public Task RunAsync()
        {
            _userDataManager.UserDataSaved += UserDataSaved;
            return Task.CompletedTask;
        }
#endif

        void UserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (e.SaveReason == UserDataSaveReason.PlaybackProgress)
            {
                return;
            }

            lock (_syncLock)
            {
                if (e.Item != null)
                {
                    if (!Shared.ShouldSyncUpdatedItem(e.Item))
                    {
                        return;
                    }

                    if (UpdateTimer == null)
                    {
                        UpdateTimer = new Timer(
                            TimerCallback,
                            null,
                            UpdateDuration,
                            Timeout.Infinite
                        );
                    }
                    else
                    {
                        UpdateTimer.Change(UpdateDuration, Timeout.Infinite);
                    }
#if EMBY
                    var userId = e.User.Id;
#else
                    var userId = e.UserId;
#endif
                    if (!_changedItems.TryGetValue(userId, out var keys))
                    {
                        keys = new List<BaseItem>();
                        _changedItems[userId] = keys;
                    }

                    keys.Add(e.Item);
                }
            }
        }

        private void TimerCallback(object state)
        {
            lock (_syncLock)
            try
            {
                var changes = _changedItems.ToList();
                _changedItems.Clear();

                SendNotifications(changes);

                if (UpdateTimer != null)
                {
                    UpdateTimer.Dispose();
                    UpdateTimer = null;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"An Error Has Occurred in TimerCallback: {e}");
            }
        }

        private void SendNotifications(IEnumerable<KeyValuePair<Guid, List<BaseItem>>> changes)
        {
            var options = new DtoOptions();
            var infoRecs = changes
                .SelectMany(change => change.Value
                    .GroupBy(i => i.Id)
                    .Select(i => i.First())
                    .Select(i => {
                        return new UserInfoRec {
                            Guid = i.Id,
#if EMBY
                            ItemId = i.GetClientId(),
#endif
                            UserId = change.Key.ToString("N", CultureInfo.InvariantCulture),
                            LastModified = DateTime.UtcNow.ToFileTime(),
                            Type = i.GetClientTypeName()
                        };
                    })
                ).ToList();

            Plugin.Instance.Db.SaveUserInfo(infoRecs);
        }


        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                if (UpdateTimer != null)
                {
                    UpdateTimer.Dispose();
                    UpdateTimer = null;
                }

                _userDataManager.UserDataSaved -= UserDataSaved;
            }

            _disposed = true;
        }
    }
}
