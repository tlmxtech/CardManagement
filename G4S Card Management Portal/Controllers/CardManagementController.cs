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

        [HttpGet("device-cards/{deviceId}")]
        public async Task<IActionResult> GetCardsForDevice(int deviceId)
        {
            var cardIds = await _context.DeviceCards
                .Where(dc => dc.DeviceId == deviceId)
                .Select(dc => dc.CardId)
                .ToListAsync();
            return Ok(cardIds);
        }

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
            public bool ForceSync { get; set; } = false; // Added for bypassing local cache
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
                    await _syncService.SyncCardsToDeviceAsync(deviceId, request.CardIds, request.UserId, request.ActionType, request.ForceSync);
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
