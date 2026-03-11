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
        /// </summary>
        public async Task SyncCardsToDeviceAsync(int deviceId, List<int> selectedCardIds, int userId, string actionType = "Insert", bool forceSync = false)
        {
            if (actionType != "Insert" && actionType != "Replace" && actionType != "Remove")
                throw new Exception($"Invalid actionType '{actionType}'. Must be Insert, Replace, or Remove.");

            var device = await _context.Devices
                .Include(d => d.Unit)
                    .ThenInclude(u => u!.Company)
                        .ThenInclude(c => c!.Reseller)
                .Include(d => d.DeviceCards)
                .FirstOrDefaultAsync(d => d.Id == deviceId);

            if (device == null) throw new Exception("Device not found.");
            if (device.Unit == null) throw new Exception($"Device {deviceId} has no associated Unit.");

            var company = device.Unit.Company ?? throw new Exception($"Device {deviceId} has no associated Company.");
            var reseller = company.Reseller ?? throw new Exception($"Company '{company.Name}' has no associated Reseller.");

            var auth = await _trackingApi.AuthenticateAsync(reseller.Username, reseller.Password);

            var imei = device.Unit.IMEI ?? "";
            var trackerType = (device.TrackerTypeName ?? "").ToLower();
            bool isTopfly = trackerType.Contains("topfly");
            bool isJointech = trackerType.Contains("jointech");

            if (!isTopfly && !isJointech)
                throw new Exception($"Unknown tracker type '{device.TrackerTypeName}' for device {deviceId}.");

            var currentCardIds = device.DeviceCards.Select(dc => dc.CardId).ToList();
            var allCommands = new List<string>();
            var dbCardIdsToRemove = new List<int>();
            var dbCardIdsToInsert = new List<int>();

            if (actionType == "Insert")
            {
                // If forceSync is true, we ignore currentCardIds and send commands for ALL selectedCardIds
                var toInsertIds = forceSync ? selectedCardIds : selectedCardIds.Except(currentCardIds).ToList();
                var tagsToInsert = await GetTagIdsForCards(toInsertIds);

                if (tagsToInsert.Any())
                {
                    allCommands.AddRange(isTopfly
                        ? _hexService.GenerateTopflyHex(imei, "Insert", tagsToInsert)
                        : _hexService.GenerateJointechHex("Insert", tagsToInsert));
                }

                // In DB, we only track the delta to avoid duplicates
                dbCardIdsToInsert = toInsertIds.Except(currentCardIds).ToList();
            }
            else if (actionType == "Replace")
            {
                var tagsToInsert = await GetTagIdsForCards(selectedCardIds);
                allCommands.AddRange(isTopfly
                    ? _hexService.GenerateTopflyHex(imei, "Replace", tagsToInsert)
                    : _hexService.GenerateJointechHex("Replace", tagsToInsert));

                dbCardIdsToRemove = currentCardIds;
                dbCardIdsToInsert = selectedCardIds;
            }
            else // Remove
            {
                // If forceSync is true, we send removal commands for all selected cards, even if we don't think they are there
                var toRemoveIds = forceSync ? selectedCardIds : selectedCardIds.Intersect(currentCardIds).ToList();
                var tagsToRemove = await GetTagIdsForCards(toRemoveIds);

                if (tagsToRemove.Any())
                {
                    allCommands.AddRange(isTopfly
                        ? _hexService.GenerateTopflyHex(imei, "Remove", tagsToRemove)
                        : _hexService.GenerateJointechHex("Remove", tagsToRemove));
                }

                dbCardIdsToRemove = forceSync ? selectedCardIds : toRemoveIds;
            }

            if (!allCommands.Any()) return;

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

            if (errors.Count < allCommands.Count)
            {
                foreach (var id in dbCardIdsToRemove)
                {
                    var dc = device.DeviceCards.FirstOrDefault(d => d.CardId == id);
                    if (dc != null) _context.DeviceCards.Remove(dc);
                }
                foreach (var id in dbCardIdsToInsert)
                {
                    if (!device.DeviceCards.Any(dc => dc.CardId == id))
                    {
                        _context.DeviceCards.Add(new DeviceCard
                        {
                            DeviceId = device.Id,
                            CardId = id,
                            LastSynced = DateTime.UtcNow
                        });
                    }
                }
                await _context.SaveChangesAsync();
            }

            if (errors.Any())
                throw new Exception($"Sync partially failed:\n" + string.Join("\n", errors));
        }

        private async Task<List<string>> GetTagIdsForCards(List<int> cardIds)
        {
            if (!cardIds.Any()) return new List<string>();
            var tagIdStrings = await _context.Cards
                .Where(c => cardIds.Contains(c.Id) && !string.IsNullOrEmpty(c.Tag_ID))
                .Select(c => c.Tag_ID)
                .ToListAsync();

            return tagIdStrings
                .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
        }
    }
}