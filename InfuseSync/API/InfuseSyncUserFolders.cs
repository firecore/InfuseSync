using System.Linq;
using System;
using System.Collections.Generic;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace InfuseSync.API
{
    [Route("/InfuseSync/UserFolders/{UserID}", "GET", Summary = "Get the list of user libraries and folders")]
    [Authenticated]
    public class GetUserFolders : IReturn<List<VirtualFolderInfo>>
    {
        [ApiMember(Name = "UserID", Description = "User identifier", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "GET")]
        public string UserID { get; set; }
    }

    public class InfuseSyncUserFolders : IService
    {
#if EMBY
        private readonly ILogger_logger;
#else
        private readonly ILogger<InfuseSyncUserFolders> _logger;
#endif
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;

#if EMBY
        public InfuseSyncUserFolders(ILogger logger, IUserManager userManager, ILibraryManager libraryManager)
#else
        public InfuseSyncUserFolders(ILogger<InfuseSyncUserFolders> logger, IUserManager userManager, ILibraryManager libraryManager)
#endif
        {
            _logger = logger;
            _userManager = userManager;
            _libraryManager = libraryManager;
        }

        public List<VirtualFolderInfo> Get(GetUserFolders request)
        {
            _logger.LogDebug($"InfuseSync: User folders requested for UserID '{request.UserID}'");

            var user = _userManager.GetUserById(Guid.Parse(request.UserID));
            if (user == null)
            {
                throw new ResourceNotFoundException($"User with ID '{request.UserID}' not found.");
            }

            return _libraryManager.GetVirtualFolders()
                .Where(f =>
                {
                    var item = _libraryManager.GetItemById(f.ItemId);
                    return item != null && item.IsVisibleStandalone(user);
                })
                .ToList();
        }
    }
}