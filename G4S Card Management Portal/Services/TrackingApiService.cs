// Services/TrackingApiService.cs
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace CardManagement.Services
{
    public class TrackingAuthResponse
    {
        public string ErrorCode { get; set; }
        public string UserIdGuid { get; set; }
        public string SessionId { get; set; }
    }

    public class TrackingApiService
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://api.3dtracking.net/api/v1.0";
        private const string PartnerBaseUrl = "https://partnerapi.3dtracking.net/api/v1.0";

        public TrackingApiService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<TrackingAuthResponse> AuthenticateAsync(string username, string password)
        {
            var url = $"{BaseUrl}/Authentication/UserAuthenticate?UserName={Uri.EscapeDataString(username ?? "")}&Password={Uri.EscapeDataString(password ?? "")}";

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url);
            }
            catch (Exception ex)
            {
                throw new Exception($"Network error connecting to tracking API: {ex.Message}");
            }

            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Tracking API returned {response.StatusCode}. Content: {content}");

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.TryGetProperty("Status", out var status) && status.TryGetProperty("ErrorCode", out var errorCodeElement))
                {
                    var errorCode = errorCodeElement.ToString();

                    if (errorCode == "0" && root.TryGetProperty("Result", out var result))
                    {
                        return new TrackingAuthResponse
                        {
                            ErrorCode = "0",
                            UserIdGuid = result.TryGetProperty("UserIdGuid", out var uid) ? uid.ToString() : "",
                            SessionId = result.TryGetProperty("SessionId", out var sid) ? sid.ToString() : ""
                        };
                    }

                    var errMsg = status.TryGetProperty("Message", out var m) ? m.ToString() : "Unknown Error";
                    throw new Exception($"Tracking API Authentication failed. Code: {errorCode}, Message: {errMsg}");
                }
                throw new Exception("Tracking API response missing Status or ErrorCode.");
            }
            catch (JsonException)
            {
                throw new Exception($"Tracking API returned invalid JSON. Content: {content}");
            }
        }

        /// <summary>
        /// Sends a hex command directly to a device via the 3DTracking Partner API.
        /// Mirrors: POST partnerapi.3dtracking.net/api/v1.0/Units/NewUnitMessage/{IMEI}?...&Message={hex}
        /// - Must be called with RESELLER credentials (not company credentials).
        /// - hexData is passed space-separated as-is in the Message query param.
        /// - Body is empty — all parameters go in the query string.
        /// </summary>
        public async Task SendCommandAsync(string userId, string sessionId, string imei, string hexData)
        {
            // Hex is sent space-separated as-is (e.g. "27 27 81 00 ..."), URL-encoded in the query string
            var url = $"{PartnerBaseUrl}/Units/NewUnitMessage/{Uri.EscapeDataString(imei)}" +
                      $"?UserIdGuid={Uri.EscapeDataString(userId)}" +
                      $"&SessionId={Uri.EscapeDataString(sessionId)}" +
                      $"&Message={Uri.EscapeDataString(hexData)}";

            HttpResponseMessage response;
            try
            {
                // Empty body POST — all params are in the query string
                response = await _httpClient.PostAsync(url, new StringContent(""));
            }
            catch (Exception ex)
            {
                throw new Exception($"Network error sending command to device {imei}: {ex.Message}");
            }

            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Partner API rejected command for IMEI {imei}. Status: {response.StatusCode}. Content: {content}");

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (root.TryGetProperty("Status", out var status) &&
                    status.TryGetProperty("ErrorCode", out var errorCode) &&
                    errorCode.ToString() != "0")
                {
                    var msg = status.TryGetProperty("Message", out var m) ? m.ToString() : "Unknown";
                    throw new Exception($"3DTracking rejected command for IMEI {imei}. Code: {errorCode}, Message: {msg}");
                }
            }
            catch (JsonException)
            {
                // Non-JSON response with HTTP 200 — treat as success
            }
        }

        public async Task<JsonElement> GetDriversAsync(string userId, string sessionId)
        {
            var url = $"{BaseUrl}/Driver/Driver/List?UserIdGuid={Uri.EscapeDataString(userId ?? "")}&SessionId={Uri.EscapeDataString(sessionId ?? "")}";
            return await FetchResultArraySafeAsync(url);
        }

        public async Task<JsonElement> GetDriverTagsAsync(string userId, string sessionId, string driverUid)
        {
            var url = $"{BaseUrl}/Driver/Driver/{Uri.EscapeDataString(driverUid ?? "")}/Tag/List?UserIdGuid={Uri.EscapeDataString(userId ?? "")}&SessionId={Uri.EscapeDataString(sessionId ?? "")}";
            return await FetchResultArraySafeAsync(url);
        }

        public async Task<JsonElement> GetUnitsAsync(string userId, string sessionId)
        {
            var url = $"{BaseUrl}/Units/Unit/List?UserIdGuid={Uri.EscapeDataString(userId ?? "")}&SessionId={Uri.EscapeDataString(sessionId ?? "")}";
            return await FetchResultArraySafeAsync(url);
        }

        /// <summary>
        /// Fetches the full Unit detail including AdditionalDetails (which contains
        /// the "ELOCK Authorised Cards" attribute used for card list polling).
        /// Mirrors: GET partnerapi/Units/{unitUid}?UserIdGuid=...&SessionId=...
        /// </summary>
        public async Task<JsonElement> GetUnitAdditionalDetailsAsync(string userId, string sessionId, string unitUid)
        {
            var url = $"{PartnerBaseUrl}/Units/{Uri.EscapeDataString(unitUid)}" +
                      $"?UserIdGuid={Uri.EscapeDataString(userId)}&SessionId={Uri.EscapeDataString(sessionId)}";

            var response = await _httpClient.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Partner API returned {response.StatusCode} for Unit {unitUid}. Content: {content}");

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.TryGetProperty("Status", out var status) &&
                    status.TryGetProperty("Result", out var result) &&
                    result.GetString() == "ok" &&
                    root.TryGetProperty("Result", out var unitResult))
                {
                    // Return the AdditionalDetails sub-object if present, else the full Result
                    if (unitResult.TryGetProperty("AdditionalDetails", out var details))
                        return details.Clone();
                    return unitResult.Clone();
                }

                throw new Exception($"Partner API response for Unit {unitUid} was not 'ok'. Content: {content}");
            }
            catch (JsonException)
            {
                throw new Exception($"Partner API returned invalid JSON for Unit {unitUid}. Content: {content}");
            }
        }

        public async Task<JsonElement> GetPartnerDevicesAsync(string userId, string sessionId, string imei)
        {
            var url = $"{PartnerBaseUrl}/Devices/Tracker/List?UserIdGuid={Uri.EscapeDataString(userId ?? "")}&SessionId={Uri.EscapeDataString(sessionId ?? "")}&IMEI={Uri.EscapeDataString(imei ?? "")}";
            var response = await _httpClient.GetAsync(url);

            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Partner API returned {response.StatusCode}. Content: {content}");

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.TryGetProperty("Status", out var statusElement) &&
                    statusElement.TryGetProperty("Result", out var resElement) &&
                    resElement.ToString() == "ok")
                {
                    if (root.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.Array)
                        return result.Clone();
                }
            }
            catch (JsonException)
            {
                throw new Exception($"Partner API returned invalid JSON. Content: {content}");
            }

            return JsonDocument.Parse("[]").RootElement.Clone();
        }

        private async Task<JsonElement> FetchResultArraySafeAsync(string url)
        {
            var response = await _httpClient.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"API returned {response.StatusCode} for URL {url}. Content: {content}");

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.Array)
                    return result.Clone();
            }
            catch (JsonException)
            {
                throw new Exception($"API returned invalid JSON. Content: {content}");
            }

            return JsonDocument.Parse("[]").RootElement.Clone();
        }
    }
}