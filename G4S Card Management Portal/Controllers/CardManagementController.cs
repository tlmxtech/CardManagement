// Controllers/CardManagementController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using CardManagement.Data;
using CardManagement.Services;

namespace CardManagement.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CardManagementController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly DeviceSyncService _syncService;
        private readonly PlatformSyncService _platformSyncService;
        private readonly CardListPollingService _pollingService;

        public CardManagementController(AppDbContext context, DeviceSyncService syncService,
            PlatformSyncService platformSyncService, CardListPollingService pollingService)
        {
            _context = context;
            _syncService = syncService;
            _platformSyncService = platformSyncService;
            _pollingService = pollingService;
        }

        [HttpGet("devices")]
        public async Task<IActionResult> GetDevices([FromQuery] int companyId)
        {
            // Include the latest PollJob status for each device so the UI can show badges
            var devices = await _context.Devices
                .Include(d => d.Unit)
                .Include(d => d.PollJobs)
                .Where(d => d.Unit.CompanyId == companyId)
                .Select(d => new {
                    d.Id,
                    Name = d.Unit.Name,
                    IMEI = d.Unit.IMEI,
                    d.TrackerTypeName,
                    GroupName = d.Unit.GroupName,
                    // Surface the most recent poll job status (null = never polled)
                    PollStatus = d.PollJobs != null && d.PollJobs.Any()
                        ? d.PollJobs.OrderByDescending(p => p.RequestedAt).First().Status
                        : null,
                    PollJobId = d.PollJobs != null && d.PollJobs.Any()
                        ? (int?)d.PollJobs.OrderByDescending(p => p.RequestedAt).First().Id
                        : null
                })
                .ToListAsync();
            return Ok(devices);
        }

        [HttpGet("cards")]
        public async Task<IActionResult> GetCards([FromQuery] int companyId)
        {
            var cards = await _context.Cards
                .Where(c => c.CompanyId == companyId)
                .Select(c => new {
                    c.Id,
                    c.DriverID,
                    c.DisplayName,
                    c.GroupName,
                    c.Tag_ID,
                    c.Tag_Type
                })
                .ToListAsync();
            return Ok(cards);
        }

        /// <summary>
        /// Returns the Card IDs currently tracked locally for a device.
        /// Called by the frontend when the user selects a device to pre-check its cards.
        /// </summary>
        [HttpGet("device-cards/{deviceId}")]
        public async Task<IActionResult> GetCardsForDevice(int deviceId)
        {
            var cardIds = await _context.DeviceCards
                .Where(dc => dc.DeviceId == deviceId)
                .Select(dc => dc.CardId)
                .ToListAsync();
            return Ok(cardIds);
        }

        /// <summary>
        /// Sends the "Ask" hex command to the device and creates a PollJob.
        /// The frontend should then call poll-status/{jobId} every ~2 minutes.
        /// </summary>
        [HttpPost("request-card-list/{deviceId}")]
        public async Task<IActionResult> RequestCardList(int deviceId)
        {
            try
            {
                var job = await _pollingService.RequestCardListAsync(deviceId);
                return Ok(new { jobId = job.Id, status = job.Status });
            }
            catch (System.Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Checks whether the device has responded with a fresh card list since the Ask was sent.
        /// If yes, overwrites DeviceCards and returns status = "Completed".
        /// Frontend polls this endpoint every 2 minutes while status is "Pending".
        /// </summary>
        [HttpGet("poll-status/{jobId}")]
        public async Task<IActionResult> PollStatus(int jobId)
        {
            try
            {
                var job = await _pollingService.CheckPollJobAsync(jobId);
                return Ok(new
                {
                    jobId = job.Id,
                    deviceId = job.DeviceId,
                    status = job.Status,
                    completedAt = job.CompletedAt,
                    error = job.ErrorMessage
                });
            }
            catch (System.Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        public class SyncRequest
        {
            public List<int> DeviceIds { get; set; }
            public List<int> CardIds { get; set; }
            public int UserId { get; set; }
            public string ActionType { get; set; } = "Insert";
        }

        [HttpPost("sync")]
        public async Task<IActionResult> SyncCardsToDevices([FromBody] SyncRequest request)
        {
            if (request.DeviceIds == null || request.CardIds == null)
                return BadRequest("Invalid request.");

            var validActions = new[] { "Insert", "Replace", "Remove" };
            if (!validActions.Contains(request.ActionType))
                return BadRequest($"Invalid ActionType '{request.ActionType}'. Must be Insert, Replace, or Remove.");

            var results = new List<object>();
            bool anyFailure = false;

            foreach (var deviceId in request.DeviceIds)
            {
                try
                {
                    await _syncService.SyncCardsToDeviceAsync(deviceId, request.CardIds, request.UserId, request.ActionType);
                    results.Add(new { deviceId, status = "Sent" });
                }
                catch (System.Exception ex)
                {
                    anyFailure = true;
                    results.Add(new { deviceId, status = "Failed", error = ex.Message });
                }
            }

            if (anyFailure && results.All(r => r.GetType().GetProperty("status")?.GetValue(r)?.ToString() == "Failed"))
                return BadRequest(new { Message = "All devices failed to sync.", results });

            return Ok(new
            {
                Message = anyFailure
                    ? $"Sync completed with some errors. {results.Count(r => r.GetType().GetProperty("status")?.GetValue(r)?.ToString() == "Sent")} of {request.DeviceIds.Count} devices succeeded."
                    : $"{request.ActionType} commands sent to {request.DeviceIds.Count} device(s).",
                results
            });
        }

        [HttpPost("pull-devices")]
        public async Task<IActionResult> PullDevices([FromQuery] int companyId)
        {
            try
            {
                await _platformSyncService.SyncDevicesAsync(companyId);
                return Ok(new { Message = "Devices pulled and updated successfully." });
            }
            catch (System.Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("pull-cards")]
        public async Task<IActionResult> PullCards([FromQuery] int companyId)
        {
            try
            {
                await _platformSyncService.SyncCardsAsync(companyId);
                return Ok(new { Message = "Cards pulled and updated successfully." });
            }
            catch (System.Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}