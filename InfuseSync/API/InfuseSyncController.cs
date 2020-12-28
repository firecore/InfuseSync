#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Mime;
using InfuseSync.Models;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace InfuseSync.API
{
    /// <summary>
    /// ASP.NET Core MVC controller for Jellyfin plugin.
    /// Wraps <see cref="InfuseSyncService"/>.
    /// </summary>
    [ApiController]
    [Authorize(Policy = "DefaultAuthorization")]
    [Produces(MediaTypeNames.Application.Json)]
    public class InfuseSyncController : ControllerBase
    {
        private readonly InfuseSyncService _service;

        public InfuseSyncController(
            ILogger<InfuseSyncController> logger,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            IDtoService dtoService)
        {
            _service = new InfuseSyncService
            (
                logger,
                userManager,
                userDataManager,
                libraryManager,
                dtoService
            );
        }

        /// <summary>
        /// Creates new synchronization checkpoint and removes previous device checkpoints.
        /// </summary>
        /// <param name="deviceId">Unique device identifier.</param>
        /// <param name="userId">User identifier.</param>
        /// <returns>A <see cref="CheckpointId"/>.</returns>
        [HttpPost("InfuseSync/Checkpoint")]
        public ActionResult<CheckpointId> CreateCheckpoint(
            [FromQuery] string deviceId,
            [FromQuery] string userId)
        {
            var request = new CreateCheckpoint
            {
                DeviceID = deviceId,
                UserID = userId
            };
            return _service.Post(request);
        }

        /// <summary>
        /// Starts synchronization session for a checkpoint and returns items statistics.
        /// </summary>
        /// <param name="checkpointID">Checkpoint identifier.</param>
        /// <returns>A <see cref="SyncStats"/>.</returns>
        [HttpPost("InfuseSync/Checkpoint/{checkpointID}/StartSync")]
        public ActionResult<SyncStats> StartCheckpointSync([FromRoute] Guid checkpointID)
        {
            var request = new StartCheckpointSync
            {
                CheckpointID = checkpointID
            };
            return _service.Post(request);
        }

        /// <summary>
        /// Get updated items for {checkpointId}.
        /// </summary>
        /// <param name="checkpointID">The checkpoint ID.</param>
        /// <param name="includeItemTypes">List of item types to include in the result.</param>
        /// <param name="fields">Additional fields of information to return in the output. This allows multiple, comma delimeted values.</param>
        /// <param name="startIndex">Offset for items to fetch.</param>
        /// <param name="limit">Maximum number of items to fetch.</param>
        /// <returns>The <see cref="QueryResult"/> with the list of <see cref="BaseItemDto"/> for updated items.</returns>
        [HttpGet("InfuseSync/Checkpoint/{checkpointID}/UpdatedItems")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<QueryResult<BaseItemDto>> GetUpdatedItemsQuery(
            [FromRoute] Guid checkpointID,
            [FromQuery] string? includeItemTypes,
            [FromQuery] string fields,
            [FromQuery] int? startIndex,
            [FromQuery] int? limit)
        {
            var request = new GetUpdatedItemsQuery
            {
                CheckpointID = checkpointID,
                IncludeItemTypes = includeItemTypes,
                Fields = fields,
                StartIndex = startIndex,
                Limit = limit
            };

            try
            {
                return _service.Get(request);
            }
            catch (ArgumentException e)
            {
                return BadRequest(e.Message);
            }
            catch (ResourceNotFoundException e)
            {
                return NotFound(e.Message);
            }
        }

        /// <summary>
        /// Get removed item IDs for {checkpointID}.
        /// </summary>
        /// <param name="checkpointID">The checkpoint ID.</param>
        /// <param name="includeItemTypes">List of item types to include in the result.</param>
        /// <param name="startIndex">Offset for items to fetch.</param>
        /// <param name="limit">Maximum number of items to fetch.</param>
        /// <returns>The <see cref="QueryResult"/> with the list of <see cref="RemovedItem"/>.</returns>
        [HttpGet("InfuseSync/Checkpoint/{checkpointID}/RemovedItems")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<QueryResult<RemovedItem>> GetRemovedItemsQuery(
            [FromRoute] Guid checkpointID,
            [FromQuery] string? includeItemTypes,
            [FromQuery] int? startIndex,
            [FromQuery] int? limit)
        {
            var request = new GetRemovedItemsQuery
            {
                CheckpointID = checkpointID,
                IncludeItemTypes = includeItemTypes,
                StartIndex = startIndex,
                Limit = limit
            };

            try
            {
                return _service.Get(request);
            }
            catch (ArgumentException e)
            {
                return BadRequest(e.Message);
            }
            catch (ResourceNotFoundException e)
            {
                return NotFound(e.Message);
            }
        }

        /// <summary>
        /// Get updated user data for {checkpointID}.
        /// </summary>
        /// <param name="checkpointID">The checkpoint ID.</param>
        /// <param name="includeItemTypes">List of item types to include in the result.</param>
        /// <param name="startIndex">Offset for items to fetch.</param>
        /// <param name="limit">Maximum number of items to fetch.</param>
        /// <returns>The <see cref="QueryResult"/> with the list of <see cref="RemovedItem"/>.</returns>
        [HttpGet("InfuseSync/Checkpoint/{checkpointID}/UserData")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<QueryResult<UserItemDataDto>> GetUserDataQuery(
            [FromRoute] Guid checkpointID,
            [FromQuery] string? includeItemTypes,
            [FromQuery] int? startIndex,
            [FromQuery] int? limit)
        {
            var request = new GetUserDataQuery
            {
                CheckpointID = checkpointID,
                IncludeItemTypes = includeItemTypes,
                StartIndex = startIndex,
                Limit = limit
            };

            try
            {
                return _service.Get(request);
            }
            catch (ArgumentException e)
            {
                return BadRequest(e.Message);
            }
            catch (ResourceNotFoundException e)
            {
                return NotFound(e.Message);
            }
        }

        /// <summary>
        /// Returns the list of user libraries and folders.
        /// </summary>
        /// <param name="userID">User identifier.</param>
        /// <returns>List of <see cref="VirtualFolderInfo"/>.</returns>
        [HttpGet("InfuseSync/UserFolders/{userID}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<List<VirtualFolderInfo>> GetUserFolders([FromRoute] string userID)
        {
            var request = new GetUserFolders
            {
                UserID = userID
            };

            try
            {
                return _service.Get(request);
            }
            catch (ResourceNotFoundException e)
            {
                return NotFound(e.Message);
            }
        }
    }
}