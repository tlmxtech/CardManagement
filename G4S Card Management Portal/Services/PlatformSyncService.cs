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

        // ── Devices ───────────────────────────────────────────────────────────────

        public async Task SyncDevicesAsync(int companyId)
        {
            var company = await _context.Companies
                .Include(c => c.Reseller)
                .FirstOrDefaultAsync(c => c.Id == companyId)
                ?? throw new Exception("Company not found");

            // ── Step 1: Sync Units from Mobility API ──────────────────────────────

            var authUser = await _apiService.AuthenticateAsync(company.Mobility_User1, company.Mobility_Pass);
            if (authUser.ErrorCode != "0") throw new Exception("Mobility authentication failed.");

            var unitsJson = await _apiService.GetUnitsAsync(authUser.UserIdGuid, authUser.SessionId);

            // Deduplicate existing Units before building dictionary
            var allExistingUnits = await _context.Units
                .Where(u => u.CompanyId == company.Id)
                .ToListAsync();

            var duplicateUnits = allExistingUnits
                .Where(u => !string.IsNullOrEmpty(u.Uid))
                .GroupBy(u => u.Uid!)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.OrderByDescending(u => u.Id).Skip(1))
                .ToList();

            if (duplicateUnits.Any())
            {
                var dupUnitIds = duplicateUnits.Select(u => u.Id).ToList();
                var dupDevices = await _context.Devices
                    .Where(d => dupUnitIds.Contains(d.UnitId))
                    .ToListAsync();
                if (dupDevices.Any())
                {
                    var dupDeviceIds = dupDevices.Select(d => d.Id).ToList();
                    var dupDc = await _context.DeviceCards
                        .Where(dc => dupDeviceIds.Contains(dc.DeviceId))
                        .ToListAsync();
                    _context.DeviceCards.RemoveRange(dupDc);
                    _context.Devices.RemoveRange(dupDevices);
                }
                _context.Units.RemoveRange(duplicateUnits);
                await _context.SaveChangesAsync();
                allExistingUnits = allExistingUnits.Except(duplicateUnits).ToList();
            }

            var existingUnits = allExistingUnits
                .Where(u => !string.IsNullOrEmpty(u.Uid))
                .ToDictionary(u => u.Uid!);

            var liveUnitUids = new HashSet<string>();

            foreach (var unitElement in unitsJson.EnumerateArray())
            {
                var uid = unitElement.TryGetProperty("Uid", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(uid)) continue;
                liveUnitUids.Add(uid);

                var groupName = unitElement.TryGetProperty("GroupName", out var gn) ? gn.GetString() ?? "" : "";
                var imei = unitElement.TryGetProperty("IMEI", out var im) ? im.GetString() ?? "" : "";
                var name = unitElement.TryGetProperty("Name", out var nm) ? nm.GetString() ?? "" : "";
                var status = unitElement.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "";

                if (existingUnits.TryGetValue(uid, out var unitDb))
                {
                    unitDb.GroupName = groupName;
                    unitDb.IMEI = imei;
                    unitDb.Name = name;
                    unitDb.Status = status;
                }
                else
                {
                    _context.Units.Add(new Unit
                    {
                        CompanyId = company.Id,
                        Uid = uid,
                        GroupName = groupName,
                        IMEI = imei,
                        Name = name,
                        Status = status,
                        Is_New_Device = true
                    });
                }
            }

            // Ground truth: delete local units no longer in the API
            var unitsToDelete = existingUnits.Values
                .Where(u => !liveUnitUids.Contains(u.Uid!))
                .ToList();

            if (unitsToDelete.Any())
            {
                var unitIdsToDelete = unitsToDelete.Select(u => u.Id).ToList();
                var devicesToDelete = await _context.Devices
                    .Where(d => unitIdsToDelete.Contains(d.UnitId))
                    .ToListAsync();
                if (devicesToDelete.Any())
                {
                    var deviceIdsToDelete = devicesToDelete.Select(d => d.Id).ToList();
                    var dcToDelete = await _context.DeviceCards
                        .Where(dc => deviceIdsToDelete.Contains(dc.DeviceId))
                        .ToListAsync();
                    _context.DeviceCards.RemoveRange(dcToDelete);
                    _context.Devices.RemoveRange(devicesToDelete);
                }
                _context.Units.RemoveRange(unitsToDelete);
            }

            await _context.SaveChangesAsync();

            // ── Step 2: Sync Devices (trackers) from Partner API ──────────────────

            if (company.Reseller == null) return;

            var authReseller = await _apiService.AuthenticateAsync(
                company.Reseller.Username, company.Reseller.Password);
            if (authReseller.ErrorCode != "0") return;

            var newUnits = await _context.Units
                .Where(u => u.CompanyId == company.Id
                         && u.Status != "Inactive"
                         && u.Is_New_Device)
                .ToListAsync();

            if (newUnits.Any())
            {
                var trackerResults = new ConcurrentBag<(Unit unit, JsonElement trackersJson)>();
                using var semaphore = new SemaphoreSlim(30);

                await Task.WhenAll(newUnits.Select(async unit =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var tj = await _apiService.GetPartnerDevicesAsync(
                            authReseller.UserIdGuid, authReseller.SessionId, unit.IMEI);
                        trackerResults.Add((unit, tj));
                    }
                    finally { semaphore.Release(); }
                }));

                // Deduplicate existing Devices before building dictionary
                var allExistingDevices = await _context.Devices
                    .Include(d => d.Unit)
                    .Where(d => d.Unit != null && d.Unit.CompanyId == company.Id)
                    .ToListAsync();

                var duplicateDevices = allExistingDevices
                    .Where(d => !string.IsNullOrEmpty(d.Uid))
                    .GroupBy(d => d.Uid!)
                    .Where(g => g.Count() > 1)
                    .SelectMany(g => g.OrderByDescending(d => d.Id).Skip(1))
                    .ToList();

                if (duplicateDevices.Any())
                {
                    var dupIds = duplicateDevices.Select(d => d.Id).ToList();
                    var dupDc = await _context.DeviceCards
                        .Where(dc => dupIds.Contains(dc.DeviceId))
                        .ToListAsync();
                    _context.DeviceCards.RemoveRange(dupDc);
                    _context.Devices.RemoveRange(duplicateDevices);
                    await _context.SaveChangesAsync();
                    allExistingDevices = allExistingDevices.Except(duplicateDevices).ToList();
                }

                var existingDevices = allExistingDevices
                    .Where(d => !string.IsNullOrEmpty(d.Uid))
                    .ToDictionary(d => d.Uid!);

                var liveDeviceUids = new HashSet<string>();

                foreach (var (unit, trackersJson) in trackerResults)
                {
                    foreach (var tracker in trackersJson.EnumerateArray())
                    {
                        var trackerType = tracker.TryGetProperty("TrackerTypeName", out var tt)
                            ? tt.GetString()?.ToLower() ?? "" : "";

                        if (!trackerType.Contains("jointech") && !trackerType.Contains("topfly"))
                        {
                            unit.Is_New_Device = false;
                            continue;
                        }

                        var tUid = tracker.TryGetProperty("Uid", out var tu) ? tu.GetString() ?? "" : "";
                        var trackerName = tracker.TryGetProperty("Name", out var tn) ? tn.GetString() ?? "" : "";
                        var trackerType2 = tracker.TryGetProperty("TrackerTypeName", out var ttn) ? ttn.GetString() ?? "" : "";

                        if (!string.IsNullOrEmpty(tUid)) liveDeviceUids.Add(tUid);

                        if (existingDevices.TryGetValue(tUid, out var deviceDb))
                        {
                            deviceDb.Name = trackerName;
                            deviceDb.TrackerTypeName = trackerType2;
                            deviceDb.UnitId = unit.Id;
                        }
                        else
                        {
                            _context.Devices.Add(new Device
                            {
                                Name = trackerName,
                                TrackerTypeName = trackerType2,
                                Uid = tUid,
                                UnitId = unit.Id
                            });
                        }
                        unit.Is_New_Device = false;
                    }
                }

                // Ground truth: delete devices no longer in Partner API (only for re-checked units)
                var checkedUnitIds = newUnits.Select(u => u.Id).ToHashSet();
                var devicesToRemove = existingDevices.Values
                    .Where(d => checkedUnitIds.Contains(d.UnitId)
                             && !string.IsNullOrEmpty(d.Uid)
                             && !liveDeviceUids.Contains(d.Uid!))
                    .ToList();

                if (devicesToRemove.Any())
                {
                    var removeIds = devicesToRemove.Select(d => d.Id).ToList();
                    var dcToRemove = await _context.DeviceCards
                        .Where(dc => removeIds.Contains(dc.DeviceId))
                        .ToListAsync();
                    _context.DeviceCards.RemoveRange(dcToRemove);
                    _context.Devices.RemoveRange(devicesToRemove);
                }

                await _context.SaveChangesAsync();
            }

            // ── Step 3: Background — read AdditionalDetails for ALL active devices ─
            // Capture plain value-type data only. Do NOT capture _apiService, _context,
            // or any other scoped service — they will be disposed when this request ends.
            var resellerAuthCopy = authReseller;
            var companyIdCopy = company.Id;
            var dbFactory = _dbFactory;          // singleton-like factory, safe to capture
            var httpClientFactory = _httpClientFactory;  // singleton, safe to capture

            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchAllDeviceCardListsInBackgroundAsync(
                        companyIdCopy, resellerAuthCopy, dbFactory, httpClientFactory);
                }
                catch
                {
                    // Background task — swallow exceptions, never affect the HTTP response
                }
            });
        }

        // ── Cards ─────────────────────────────────────────────────────────────────

        public async Task SyncCardsAsync(int companyId)
        {
            var company = await _context.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
                ?? throw new Exception("Company not found");

            var authUser = await _apiService.AuthenticateAsync(company.Mobility_User1, company.Mobility_Pass);
            if (authUser.ErrorCode != "0") throw new Exception("Mobility Authentication Failed");

            var driversJson = await _apiService.GetDriversAsync(authUser.UserIdGuid, authUser.SessionId);
            var activeMobilityUids = new HashSet<string>();

            // Deduplicate existing Cards
            var allExistingCards = await _context.Cards
                .Where(c => c.CompanyId == company.Id && !string.IsNullOrEmpty(c.Uid))
                .ToListAsync();

            var duplicateCards = allExistingCards
                .GroupBy(c => c.Uid!)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.OrderByDescending(c => c.Id).Skip(1))
                .ToList();

            if (duplicateCards.Any())
            {
                var dupCardIds = duplicateCards.Select(c => c.Id).ToList();
                var dupCardDc = await _context.DeviceCards
                    .Where(dc => dupCardIds.Contains(dc.CardId))
                    .ToListAsync();
                _context.DeviceCards.RemoveRange(dupCardDc);
                _context.Cards.RemoveRange(duplicateCards);
                await _context.SaveChangesAsync();
                allExistingCards = allExistingCards.Except(duplicateCards).ToList();
            }

            var existingCards = allExistingCards.ToDictionary(c => c.Uid!);

            foreach (var driver in driversJson.EnumerateArray())
            {
                var uid = driver.TryGetProperty("Uid", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(uid)) continue;
                activeMobilityUids.Add(uid);

                var dispName = driver.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? "" : "";
                var driverId = driver.TryGetProperty("DriverID", out var di) ? di.GetString() ?? "" : "";
                var firstName = driver.TryGetProperty("FirstName", out var fn) ? fn.GetString() ?? "" : "";
                var lastName = driver.TryGetProperty("LastName", out var ln) ? ln.GetString() ?? "" : "";
                var groupName = driver.TryGetProperty("GroupName", out var gn) ? gn.GetString() ?? "" : "";

                if (existingCards.TryGetValue(uid, out var cardDb))
                {
                    if (cardDb.DisplayName != dispName || cardDb.DriverID != driverId)
                    {
                        cardDb.DisplayName = dispName;
                        cardDb.DriverID = driverId;
                        cardDb.FirstName = firstName;
                        cardDb.LastName = lastName;
                        cardDb.GroupName = groupName;
                        cardDb.Is_New = true;
                    }
                }
                else
                {
                    _context.Cards.Add(new Card
                    {
                        Uid = uid,
                        DisplayName = dispName,
                        DriverID = driverId,
                        FirstName = firstName,
                        LastName = lastName,
                        GroupName = groupName,
                        CompanyId = company.Id,
                        Is_New = true,
                        Tag_ID = "",
                        Tag_Type = ""
                    });
                }
            }
            await _context.SaveChangesAsync();

            // Ground truth: delete cards no longer in Mobility, cascade DeviceCards
            var cardsToDelete = await _context.Cards
                .Where(c => c.CompanyId == company.Id
                         && !activeMobilityUids.Contains(c.Uid ?? ""))
                .ToListAsync();

            if (cardsToDelete.Any())
            {
                var deleteIds = cardsToDelete.Select(c => c.Id).ToList();
                var dcToDelete = await _context.DeviceCards
                    .Where(dc => deleteIds.Contains(dc.CardId))
                    .ToListAsync();
                _context.DeviceCards.RemoveRange(dcToDelete);
                _context.Cards.RemoveRange(cardsToDelete);
                await _context.SaveChangesAsync();
            }

            // Populate tags for new/changed cards
            var newCards = await _context.Cards
                .Where(c => c.CompanyId == company.Id && c.Is_New)
                .ToListAsync();

            if (!newCards.Any()) return;

            var tagResults = new ConcurrentBag<(Card card, JsonElement tagsJson)>();
            using var tagSem = new SemaphoreSlim(40);

            await Task.WhenAll(newCards.Select(async card =>
            {
                await tagSem.WaitAsync();
                try
                {
                    var tj = await _apiService.GetDriverTagsAsync(
                        authUser.UserIdGuid, authUser.SessionId, card.Uid ?? "");
                    tagResults.Add((card, tj));
                }
                catch { }
                finally { tagSem.Release(); }
            }));

            foreach (var (card, tagsJson) in tagResults)
            {
                var tagIds = new List<string>();
                var tagTypes = new HashSet<string>();

                foreach (var tag in tagsJson.EnumerateArray())
                {
                    var tagName = tag.TryGetProperty("Name", out var tName) ? tName.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(tagName)) continue;
                    tagIds.Add(tagName);
                    tagTypes.Add(tagName.Length switch
                    {
                        10 => "Jointech",
                        8 => "TopFly",
                        15 or 16 => "Dallas",
                        _ => "Unknown"
                    });
                }

                if (tagIds.Any())
                {
                    card.Tag_ID = string.Join(",", tagIds);
                    card.Tag_Type = string.Join(",", tagTypes);
                }
                card.Is_New = false;
            }

            await _context.SaveChangesAsync();
        }

        // ── Background AdditionalDetails fetch ────────────────────────────────────

        /// <summary>
        /// Runs entirely in a background thread using its own fresh DbContext from the factory.
        /// Queries all active devices for the company, fetches AdditionalDetails from the
        /// Partner API (GET /Units/{unit.Uid}), parses "ELOCK Authorised Cards", and
        /// overwrites DeviceCards. No command is sent to any device.
        /// </summary>
        private static async Task FetchAllDeviceCardListsInBackgroundAsync(
            int companyId,
            TrackingAuthResponse resellerAuth,
            IDbContextFactory<AppDbContext> dbFactory,
            IHttpClientFactory httpClientFactory)
        {
            // Create a brand-new TrackingApiService with its own HttpClient.
            // We MUST NOT use the injected _apiService here — it is scoped and will
            // have been disposed by the time the background task runs its async continuations.
            var httpClient = httpClientFactory.CreateClient();
            var freshApi = new TrackingApiService(httpClient);

            // Use a short-lived context just to load the device + card lists,
            // then dispose it before the parallel work begins.
            List<(int deviceId, string unitUid)> deviceInfos;
            List<(int cardId, string tagId)> allCards;

            await using (var setupDb = await dbFactory.CreateDbContextAsync())
            {
                var rawDevices = await setupDb.Devices
                    .Include(d => d.Unit)
                    .Where(d => d.Unit != null
                             && d.Unit.CompanyId == companyId
                             && d.Unit.Status != "Inactive"
                             && !string.IsNullOrEmpty(d.Unit.Uid))
                    .Select(d => new { d.Id, UnitUid = d.Unit!.Uid })
                    .ToListAsync();
                deviceInfos = rawDevices.Select(x => (x.Id, x.UnitUid!)).ToList();

                var rawCards = await setupDb.Cards
                    .Where(c => c.CompanyId == companyId && !string.IsNullOrEmpty(c.Tag_ID))
                    .Select(c => new { c.Id, c.Tag_ID })
                    .ToListAsync();
                allCards = rawCards.Select(x => (x.Id, x.Tag_ID!)).ToList();
            }

            if (!deviceInfos.Any()) return;

            // Each device gets its own independent DbContext — SQLite cannot handle
            // concurrent writes on a shared connection, so we never share contexts
            // across parallel tasks.
            using var semaphore = new SemaphoreSlim(5); // conservative for SQLite

            await Task.WhenAll(deviceInfos.Select(async info =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var details = await freshApi.GetUnitAdditionalDetailsAsync(
                        resellerAuth.UserIdGuid, resellerAuth.SessionId, info.unitUid);

                    var cardTags = ParseAuthorisedCardTags(details);
                    if (cardTags == null) return; // attribute absent — don't touch DeviceCards

                    // Match tag strings to Card IDs using the pre-loaded list (no DB needed)
                    var matchedCardIds = new HashSet<int>();
                    foreach (var tag in cardTags)
                    {
                        foreach (var (cardId, tagId) in allCards)
                        {
                            if (tagId.Split(',')
                                     .Select(t => t.Trim())
                                     .Contains(tag, StringComparer.OrdinalIgnoreCase))
                            {
                                matchedCardIds.Add(cardId);
                            }
                        }
                    }

                    // Each device writes with its own fresh context — no sharing, no disposal races
                    await using var db = await dbFactory.CreateDbContextAsync();

                    var existing = await db.DeviceCards
                        .Where(dc => dc.DeviceId == info.deviceId)
                        .ToListAsync();
                    db.DeviceCards.RemoveRange(existing);

                    foreach (var cardId in matchedCardIds)
                    {
                        db.DeviceCards.Add(new DeviceCard
                        {
                            DeviceId = info.deviceId,
                            CardId = cardId,
                            LastSynced = DateTime.UtcNow
                        });
                    }

                    await db.SaveChangesAsync();
                }
                catch { /* skip individual device failures silently */ }
                finally { semaphore.Release(); }
            }));
        }

        /// <summary>
        /// Parses "ELOCK Authorised Cards" from an AdditionalDetails JSON element.
        /// Returns null  → attribute absent (do not overwrite DeviceCards).
        /// Returns empty → attribute present but device has no cards.
        /// Returns list  → the tag IDs currently on the device.
        /// </summary>
        private static List<string>? ParseAuthorisedCardTags(JsonElement additionalDetails)
        {
            if (!additionalDetails.TryGetProperty("Attributes", out var attributes))
                return null;

            foreach (var attr in attributes.EnumerateArray())
            {
                if (attr.TryGetProperty("Name", out var n) && n.GetString() == "ELOCK Authorised Cards")
                {
                    if (!attr.TryGetProperty("Value", out var v) || string.IsNullOrWhiteSpace(v.GetString()))
                        return new List<string>();

                    return v.GetString()!
                        .Split(',')
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                }
            }

            return null;
        }
    }
}