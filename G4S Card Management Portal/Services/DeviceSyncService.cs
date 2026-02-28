// Services/DeviceSyncService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using CardManagement.Data;
using CardManagement.Models;

namespace CardManagement.Services
{
    public class DeviceSyncService
    {
        private readonly AppDbContext _context;
        private readonly HexProtocolService _hexService;
        private readonly TrackingApiService _trackingApi;

        public DeviceSyncService(AppDbContext context, HexProtocolService hexService, TrackingApiService trackingApi)
        {
            _context = context;
            _hexService = hexService;
            _trackingApi = trackingApi;
        }

        /// <summary>
        /// Sends card commands to a device IMMEDIATELY via the 3DTracking Partner API.
        /// 
        /// actionType behaviour:
        ///   "Insert"  — delta sync: only sends commands for cards not already on the device
        ///   "Replace" — clears ALL cards on the device first, then inserts the selected cards
        ///   "Remove"  — removes only the selected cards from the device
        /// </summary>
        public async Task SyncCardsToDeviceAsync(int deviceId, List<int> selectedCardIds, int userId, string actionType = "Insert")
        {
            if (actionType != "Insert" && actionType != "Replace" && actionType != "Remove")
                throw new Exception($"Invalid actionType '{actionType}'. Must be Insert, Replace, or Remove.");

            // 1. Load device with its unit (for IMEI) and current card state
            var device = await _context.Devices
                .Include(d => d.Unit)
                    .ThenInclude(u => u!.Company)
                        .ThenInclude(c => c!.Reseller)
                .Include(d => d.DeviceCards)
                    .ThenInclude(dc => dc.Card)
                .FirstOrDefaultAsync(d => d.Id == deviceId);

            if (device == null) throw new Exception("Device not found.");
            if (device.Unit == null) throw new Exception($"Device {deviceId} has no associated Unit.");
            if (device.Unit.Company == null) throw new Exception($"Device {deviceId} has no associated Company.");

            var company = device.Unit.Company;

            // 2. Authenticate with RESELLER credentials (required by the Partner API send endpoint)
            //    This matches the Deluge SendConfiguration_Batch behaviour exactly.
            var reseller = company.Reseller
                ?? throw new Exception($"Company '{company.Name}' has no associated Reseller. Cannot send commands.");

            var auth = await _trackingApi.AuthenticateAsync(reseller.Username, reseller.Password);

            var imei = device.Unit.IMEI ?? "";
            var trackerType = (device.TrackerTypeName ?? "").ToLower();
            bool isTopfly = trackerType.Contains("topfly");
            bool isJointech = trackerType.Contains("jointech");

            if (!isTopfly && !isJointech)
                throw new Exception($"Unknown tracker type '{device.TrackerTypeName}' for device {deviceId}. Cannot generate commands.");

            // 3. Determine which tag lists to generate commands for, based on actionType
            var currentCardIds = device.DeviceCards.Select(dc => dc.CardId).ToList();
            var allCommands = new List<string>();

            // DB changes to apply after successful send
            var dbCardIdsToRemove = new List<int>();
            var dbCardIdsToInsert = new List<int>();

            if (actionType == "Insert")
            {
                // Delta: only cards not already on the device
                var toInsertIds = selectedCardIds.Except(currentCardIds).ToList();
                var tagsToInsert = await GetTagIdsForCards(toInsertIds);

                if (tagsToInsert.Any())
                {
                    allCommands.AddRange(isTopfly
                        ? _hexService.GenerateTopflyHex(imei, "Insert", tagsToInsert)
                        : _hexService.GenerateJointechHex("Insert", tagsToInsert));
                }

                dbCardIdsToInsert = toInsertIds;
            }
            else if (actionType == "Replace")
            {
                // Send a clear-all command first, then insert the selected cards
                var tagsToInsert = await GetTagIdsForCards(selectedCardIds);

                allCommands.AddRange(isTopfly
                    ? _hexService.GenerateTopflyHex(imei, "Replace", tagsToInsert)
                    : _hexService.GenerateJointechHex("Replace", tagsToInsert));

                // DB: remove everything currently tracked, then add the selected set
                dbCardIdsToRemove = currentCardIds;
                dbCardIdsToInsert = selectedCardIds.Except(currentCardIds).ToList();
            }
            else // Remove
            {
                // Only remove the selected cards that are actually on the device
                var toRemoveIds = selectedCardIds.Intersect(currentCardIds).ToList();
                var tagsToRemove = await GetTagIdsForCards(toRemoveIds);

                if (tagsToRemove.Any())
                {
                    allCommands.AddRange(isTopfly
                        ? _hexService.GenerateTopflyHex(imei, "Remove", tagsToRemove)
                        : _hexService.GenerateJointechHex("Remove", tagsToRemove));
                }

                dbCardIdsToRemove = toRemoveIds;
            }

            if (!allCommands.Any())
                return; // Nothing to do — device already matches desired state

            // 4. Send each command IMMEDIATELY to the 3DTracking API
            var errors = new List<string>();
            foreach (var hexCommand in allCommands)
            {
                try
                {
                    await _trackingApi.SendCommandAsync(auth.UserIdGuid, auth.SessionId, imei, hexCommand);
                }
                catch (Exception ex)
                {
                    errors.Add($"Command failed for IMEI {imei}: {ex.Message}");
                }
            }

            // 5. Update local DeviceCards table to reflect the new state
            //    Only update DB if at least some commands went through
            if (errors.Count < allCommands.Count)
            {
                foreach (var id in dbCardIdsToRemove)
                {
                    var dc = device.DeviceCards.FirstOrDefault(d => d.CardId == id);
                    if (dc != null) _context.DeviceCards.Remove(dc);
                }
                foreach (var id in dbCardIdsToInsert)
                {
                    _context.DeviceCards.Add(new DeviceCard
                    {
                        DeviceId = device.Id,
                        CardId = id,
                        LastSynced = DateTime.UtcNow
                    });
                }
                await _context.SaveChangesAsync();
            }

            // 6. Surface any errors to the caller
            if (errors.Any())
                throw new Exception($"Sync partially failed ({errors.Count}/{allCommands.Count} commands failed):\n" + string.Join("\n", errors));
        }

        // Resolves card IDs to their flat list of Tag_ID strings (handles comma-separated multi-tag cards)
        private async Task<List<string>> GetTagIdsForCards(List<int> cardIds)
        {
            if (!cardIds.Any()) return new List<string>();

            var tagIdStrings = await _context.Cards
                .Where(c => cardIds.Contains(c.Id) && !string.IsNullOrEmpty(c.Tag_ID))
                .Select(c => c.Tag_ID)
                .ToListAsync();

            // Flatten: each Tag_ID field may contain comma-separated tags
            return tagIdStrings
                .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
        }
    }
}