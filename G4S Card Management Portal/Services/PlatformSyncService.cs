// Services/PlatformSyncService.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using CardManagement.Data;
using CardManagement.Models;

namespace CardManagement.Services
{
    public class PlatformSyncService
    {
        private readonly AppDbContext _context;
        private readonly TrackingApiService _apiService;
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly IHttpClientFactory _httpClientFactory;

        public PlatformSyncService(
            AppDbContext context,
            TrackingApiService apiService,
            IDbContextFactory<AppDbContext> dbFactory,
            IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _apiService = apiService;
            _dbFactory = dbFactory;
            _httpClientFactory = httpClientFactory;
        }

        public async Task SyncDevicesAsync(int companyId)
        {
            var company = await _context.Companies
                .Include(c => c.Reseller)
                .FirstOrDefaultAsync(c => c.Id == companyId)
                ?? throw new Exception("Company not found");

            // 1. Sync Units
            var authUser = await _apiService.AuthenticateAsync(company.Mobility_User1, company.Mobility_Pass);
            var unitsJson = await _apiService.GetUnitsAsync(authUser.UserIdGuid, authUser.SessionId);

            // Null check Uid to prevent ToDictionary failure
            var existingUnits = await _context.Units
                .Where(u => u.CompanyId == company.Id && u.Uid != null)
                .ToDictionaryAsync(u => u.Uid!);

            var liveUnitUids = new HashSet<string>();

            foreach (var unitElement in unitsJson.EnumerateArray())
            {
                var uid = unitElement.TryGetProperty("Uid", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(uid)) continue;
                liveUnitUids.Add(uid);

                if (existingUnits.TryGetValue(uid, out var unitDb))
                {
                    unitDb.IMEI = unitElement.TryGetProperty("IMEI", out var im) ? im.GetString() ?? "" : "";
                    unitDb.Name = unitElement.GetProperty("Name").GetString() ?? "";
                    unitDb.Status = unitElement.GetProperty("Status").GetString() ?? "";
                    unitDb.GroupName = unitElement.TryGetProperty("GroupName", out var gn) ? gn.GetString() ?? "" : "";
                }
                else
                {
                    _context.Units.Add(new Unit
                    {
                        CompanyId = company.Id,
                        Uid = uid,
                        IMEI = unitElement.TryGetProperty("IMEI", out var im) ? im.GetString() ?? "" : "",
                        Name = unitElement.GetProperty("Name").GetString() ?? "",
                        Status = unitElement.GetProperty("Status").GetString() ?? "",
                        GroupName = unitElement.TryGetProperty("GroupName", out var gn) ? gn.GetString() ?? "" : "",
                        Is_New_Device = true
                    });
                }
            }
            await _context.SaveChangesAsync();

            // 2. Sync Devices (Trackers)
            if (company.Reseller == null) return;
            var authReseller = await _apiService.AuthenticateAsync(company.Reseller.Username, company.Reseller.Password);

            var newUnits = await _context.Units.Where(u => u.CompanyId == company.Id && u.Is_New_Device).ToListAsync();
            var addedDevices = new List<Device>();

            if (newUnits.Any())
            {
                foreach (var unit in newUnits)
                {
                    try
                    {
                        var trackersJson = await _apiService.GetPartnerDevicesAsync(authReseller.UserIdGuid, authReseller.SessionId, unit.IMEI);
                        foreach (var tracker in trackersJson.EnumerateArray())
                        {
                            var tUid = tracker.GetProperty("Uid").GetString() ?? "";
                            var trackerType = tracker.GetProperty("TrackerTypeName").GetString() ?? "";

                            var device = new Device
                            {
                                Name = tracker.GetProperty("Name").GetString() ?? "",
                                TrackerTypeName = trackerType,
                                Uid = tUid,
                                UnitId = unit.Id
                            };
                            _context.Devices.Add(device);
                            addedDevices.Add(device);
                        }
                    }
                    catch { /* Skip units that fail API check */ }
                    unit.Is_New_Device = false;
                }
                await _context.SaveChangesAsync();
            }

            // 3. Trigger Background Fetch
            var resellerAuthCopy = authReseller;
            var companyIdCopy = company.Id;
            var dbFactory = _dbFactory;
            var httpClientFactory = _httpClientFactory;
            var targetDeviceUids = addedDevices.Select(d => d.Uid).ToList();

            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchDeviceCardListsInBackgroundAsync(
                        companyIdCopy, resellerAuthCopy, dbFactory, httpClientFactory, targetDeviceUids);
                }
                catch { /* Background task swallows own errors */ }
            });
        }

        public async Task SyncCardsAsync(int companyId)
        {
            var company = await _context.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
                ?? throw new Exception("Company not found");

            var authUser = await _apiService.AuthenticateAsync(company.Mobility_User1, company.Mobility_Pass);
            var driversJson = await _apiService.GetDriversAsync(authUser.UserIdGuid, authUser.SessionId);
            var existingCards = await _context.Cards.Where(c => c.CompanyId == company.Id && c.Uid != null).ToDictionaryAsync(c => c.Uid!);
            var activeMobilityUids = new HashSet<string>();

            foreach (var driver in driversJson.EnumerateArray())
            {
                var uid = driver.GetProperty("Uid").GetString() ?? "";
                if (string.IsNullOrEmpty(uid)) continue;
                activeMobilityUids.Add(uid);

                if (existingCards.TryGetValue(uid, out var cardDb))
                {
                    cardDb.DisplayName = driver.GetProperty("DisplayName").GetString() ?? "";
                    cardDb.DriverID = driver.GetProperty("DriverID").GetString() ?? "";
                }
                else
                {
                    _context.Cards.Add(new Card
                    {
                        Uid = uid,
                        DisplayName = driver.GetProperty("DisplayName").GetString() ?? "",
                        DriverID = driver.GetProperty("DriverID").GetString() ?? "",
                        CompanyId = company.Id,
                        Is_New = true,
                        Tag_ID = ""
                    });
                }
            }
            await _context.SaveChangesAsync();

            var newCards = await _context.Cards.Where(c => c.CompanyId == company.Id && c.Is_New).ToListAsync();
            foreach (var card in newCards)
            {
                try
                {
                    var tagsJson = await _apiService.GetDriverTagsAsync(authUser.UserIdGuid, authUser.SessionId, card.Uid ?? "");
                    var tagIds = new List<string>();
                    foreach (var tag in tagsJson.EnumerateArray()) tagIds.Add(tag.GetProperty("Name").GetString() ?? "");
                    card.Tag_ID = string.Join(",", tagIds);
                }
                catch { }
                card.Is_New = false;
            }
            await _context.SaveChangesAsync();
        }

        private static async Task FetchDeviceCardListsInBackgroundAsync(
            int companyId,
            TrackingAuthResponse resellerAuth,
            IDbContextFactory<AppDbContext> dbFactory,
            IHttpClientFactory httpClientFactory,
            List<string>? specificDeviceUids = null)
        {
            var httpClient = httpClientFactory.CreateClient();
            var freshApi = new TrackingApiService(httpClient);

            List<(int deviceId, string unitUid)> devicesToProcess;
            List<(int cardId, string tagId)> allCards;

            await using (var setupDb = await dbFactory.CreateDbContextAsync())
            {
                var query = setupDb.Devices.Include(d => d.Unit).Where(d => d.Unit != null && d.Unit.CompanyId == companyId);

                if (specificDeviceUids != null && specificDeviceUids.Any())
                    query = query.Where(d => specificDeviceUids.Contains(d.Uid));

                var rawDevices = await query.Select(d => new { d.Id, UnitUid = d.Unit!.Uid }).ToListAsync();
                devicesToProcess = rawDevices.Where(x => !string.IsNullOrEmpty(x.UnitUid)).Select(x => (x.Id, x.UnitUid!)).ToList();

                var rawCards = await setupDb.Cards.Where(c => c.CompanyId == companyId && !string.IsNullOrEmpty(c.Tag_ID))
                    .Select(c => new { c.Id, c.Tag_ID }).ToListAsync();
                allCards = rawCards.Select(x => (x.Id, x.Tag_ID!)).ToList();
            }

            using var semaphore = new SemaphoreSlim(5);
            await Task.WhenAll(devicesToProcess.Select(async info =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var details = await freshApi.GetUnitAdditionalDetailsAsync(resellerAuth.UserIdGuid, resellerAuth.SessionId, info.unitUid);
                    var cardTags = ParseAuthorisedCardTags(details);
                    if (cardTags == null) return;

                    var matchedCardIds = new HashSet<int>();
                    foreach (var tag in cardTags)
                    {
                        foreach (var (cardId, tagId) in allCards)
                        {
                            if (tagId.Split(',').Select(t => t.Trim()).Contains(tag, StringComparer.OrdinalIgnoreCase))
                                matchedCardIds.Add(cardId);
                        }
                    }

                    await using var db = await dbFactory.CreateDbContextAsync();
                    var existing = await db.DeviceCards.Where(dc => dc.DeviceId == info.deviceId).ToListAsync();
                    db.DeviceCards.RemoveRange(existing);

                    foreach (var cardId in matchedCardIds)
                    {
                        db.DeviceCards.Add(new DeviceCard { DeviceId = info.deviceId, CardId = cardId, LastSynced = DateTime.UtcNow });
                    }
                    await db.SaveChangesAsync();
                }
                catch { }
                finally { semaphore.Release(); }
            }));
        }

        private static List<string>? ParseAuthorisedCardTags(JsonElement additionalDetails)
        {
            if (!additionalDetails.TryGetProperty("Attributes", out var attributes)) return null;
            foreach (var attr in attributes.EnumerateArray())
            {
                if (attr.TryGetProperty("Name", out var n) && n.GetString() == "ELOCK Authorised Cards")
                {
                    var val = attr.TryGetProperty("Value", out var v) ? v.GetString() : null;
                    if (string.IsNullOrWhiteSpace(val)) return new List<string>();
                    return val.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                }
            }
            return null;
        }
    }
}