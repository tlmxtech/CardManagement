// Services/CardListPollingService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using CardManagement.Data;
using CardManagement.Models;

namespace CardManagement.Services
{
    public class CardListPollingService
    {
        private readonly AppDbContext _context;
        private readonly TrackingApiService _trackingApi;
        private readonly HexProtocolService _hexService;

        public CardListPollingService(AppDbContext context, TrackingApiService trackingApi, HexProtocolService hexService)
        {
            _context = context;
            _trackingApi = trackingApi;
            _hexService = hexService;
        }

        /// <summary>
        /// Sends the "Ask" hex command to the device and creates a PollJob to track the response.
        /// If a Pending job already exists for this device it is reused (idempotent).
        /// Returns the PollJob so the controller can return its ID to the frontend.
        /// </summary>
        public async Task<PollJob> RequestCardListAsync(int deviceId)
        {
            var device = await _context.Devices
                .Include(d => d.Unit)
                    .ThenInclude(u => u!.Company)
                        .ThenInclude(c => c!.Reseller)
                .FirstOrDefaultAsync(d => d.Id == deviceId)
                ?? throw new Exception("Device not found.");

            if (device.Unit == null) throw new Exception("Device has no associated Unit.");
            var company = device.Unit.Company ?? throw new Exception("Device has no associated Company.");
            var reseller = company.Reseller ?? throw new Exception($"Company '{company.Name}' has no Reseller.");

            // Reuse an existing Pending job rather than stacking duplicates
            var existingJob = await _context.PollJobs
                .FirstOrDefaultAsync(p => p.DeviceId == deviceId && p.Status == "Pending");
            if (existingJob != null) return existingJob;

            // Build and send the Ask hex command (same as CardSync.txt logic)
            var imei = device.Unit.IMEI ?? "";
            var trackerType = (device.TrackerTypeName ?? "").ToLower();
            string askHex;

            if (trackerType.Contains("jointech"))
            {
                askHex = "28 50 34 31 2C 30 2C 31 29"; // (P41,0,1) — request card list
            }
            else if (trackerType.Contains("topfly"))
            {
                // Build NFCIDL# command: "27 27 81 00 07 00 01 {imei} 01 4E 46 43 49 44 4C 23"
                var formattedImei = FormatImei(imei);
                askHex = $"27 27 81 00 07 00 01 {formattedImei} 01 4E 46 43 49 44 4C 23";
            }
            else
            {
                throw new Exception($"Unknown tracker type '{device.TrackerTypeName}'. Cannot request card list.");
            }

            var auth = await _trackingApi.AuthenticateAsync(reseller.Username, reseller.Password);
            await _trackingApi.SendCommandAsync(auth.UserIdGuid, auth.SessionId, imei, askHex);

            var job = new PollJob
            {
                DeviceId = deviceId,
                RequestedAt = DateTime.UtcNow,
                Status = "Pending"
            };
            _context.PollJobs.Add(job);
            await _context.SaveChangesAsync();
            return job;
        }

        /// <summary>
        /// Checks the 3DTracking AdditionalDetails for the device.
        /// If the "ELOCK Authorised Cards" attribute was updated AFTER RequestedAt,
        /// overwrites DeviceCards with the live list and marks the job Completed.
        /// Called by the controller on a frontend timer (every ~2 minutes).
        /// </summary>
        public async Task<PollJob> CheckPollJobAsync(int jobId)
        {
            var job = await _context.PollJobs
                .Include(p => p.Device)
                    .ThenInclude(d => d!.Unit)
                        .ThenInclude(u => u!.Company)
                            .ThenInclude(c => c!.Reseller)
                .FirstOrDefaultAsync(p => p.Id == jobId)
                ?? throw new Exception("PollJob not found.");

            if (job.Status != "Pending")
                return job; // Already resolved — nothing to do

            var device = job.Device!;
            var company = device.Unit!.Company!;
            var reseller = company.Reseller ?? throw new Exception("No reseller on company.");

            try
            {
                var auth = await _trackingApi.AuthenticateAsync(reseller.Username, reseller.Password);
                var unitUid = device.Unit.Uid ?? throw new Exception("Unit has no Uid.");

                var additionalDetails = await _trackingApi.GetUnitAdditionalDetailsAsync(
                    auth.UserIdGuid, auth.SessionId, unitUid);

                // Parse "ELOCK Authorised Cards" attribute exactly as the Deluge script does
                var (cardTagList, lastUpdated) = ParseAuthorisedCards(additionalDetails);

                // Only accept the result if the device updated AFTER we sent the Ask command
                if (lastUpdated == null || lastUpdated <= job.RequestedAt)
                    return job; // Data is stale — keep polling

                // Overwrite DeviceCards with the live list from the device
                await ReconcileDeviceCardsAsync(device.Id, company.Id, cardTagList);

                job.Status = "Completed";
                job.CompletedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                job.Status = "Failed";
                job.ErrorMessage = ex.Message;
                job.CompletedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            return job;
        }

        /// <summary>
        /// Overwrites the local DeviceCards table for this device with the tag list
        /// returned from the 3DTracking API. Matches tags to Card records by Tag_ID.
        /// </summary>
        private async Task ReconcileDeviceCardsAsync(int deviceId, int companyId, List<string> liveTagIds)
        {
            // Remove all existing DeviceCard entries for this device
            var existing = await _context.DeviceCards.Where(dc => dc.DeviceId == deviceId).ToListAsync();
            _context.DeviceCards.RemoveRange(existing);

            if (liveTagIds.Any())
            {
                // Match each tag ID to a Card record in the company
                // Tag_ID may be comma-separated so we check contains
                var allCards = await _context.Cards
                    .Where(c => c.CompanyId == companyId && !string.IsNullOrEmpty(c.Tag_ID))
                    .Select(c => new { c.Id, c.Tag_ID })
                    .ToListAsync();

                var matchedCardIds = new HashSet<int>();
                foreach (var tag in liveTagIds)
                {
                    foreach (var card in allCards)
                    {
                        if (card.Tag_ID!.Split(',').Select(t => t.Trim()).Contains(tag))
                            matchedCardIds.Add(card.Id);
                    }
                }

                foreach (var cardId in matchedCardIds)
                {
                    _context.DeviceCards.Add(new DeviceCard
                    {
                        DeviceId = deviceId,
                        CardId = cardId,
                        LastSynced = DateTime.UtcNow
                    });
                }
            }

            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Parses the AdditionalDetails JSON to extract the "ELOCK Authorised Cards"
        /// attribute value and its LastUpdatedDate — mirrors the Deluge script logic exactly.
        /// </summary>
        private static (List<string> tags, DateTime? lastUpdated) ParseAuthorisedCards(JsonElement additionalDetails)
        {
            if (!additionalDetails.TryGetProperty("Attributes", out var attributes))
                return (new List<string>(), null);

            foreach (var attribute in attributes.EnumerateArray())
            {
                var name = attribute.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (name != "ELOCK Authorised Cards") continue;

                DateTime? lastUpdated = null;
                if (attribute.TryGetProperty("LastUpdatedDate", out var dateEl))
                {
                    // Format from API: "2024-01-15T14:30:00" (UTC)
                    if (DateTime.TryParse(dateEl.GetString(), out var parsed))
                        lastUpdated = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                }

                var tags = new List<string>();
                if (attribute.TryGetProperty("Value", out var valueEl) && valueEl.GetString() is string val && !string.IsNullOrWhiteSpace(val))
                {
                    // Value is a comma-separated list of tag IDs e.g. "1A2B3C4D5E, 9F8E7D6C5B"
                    tags = val.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                }

                return (tags, lastUpdated);
            }

            return (new List<string>(), null);
        }

        private static string FormatImei(string imei)
        {
            if (string.IsNullOrEmpty(imei)) return "00 00 00 00 00 00 00 00";
            if (imei.Length % 2 != 0) imei = "0" + imei;
            return string.Join(" ", Enumerable.Range(0, imei.Length / 2).Select(i => imei.Substring(i * 2, 2)));
        }
    }
}