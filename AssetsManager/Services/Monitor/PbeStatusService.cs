using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using Newtonsoft.Json.Linq;

namespace AssetsManager.Services.Monitor
{
    public class PbeStatusService
    {
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private const string PbeStatusUrl = "https://lol.secure.dyn.riotcdn.net/channels/public/x/status/pbe.json";

        public event Action StatusChecked;

        // Dictionary to map common timezone abbreviations to their UTC offsets.
        private static readonly Dictionary<string, TimeSpan> TimeZoneAbbreviations = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            { "PDT", TimeSpan.FromHours(-7) },
            { "PST", TimeSpan.FromHours(-8) },
            { "UTC", TimeSpan.FromHours(0) },
            // Add more as needed
        };

        public PbeStatusService(HttpClient httpClient, LogService logService, AppSettings appSettings)
        {
            _httpClient = httpClient;
            _logService = logService;
            _appSettings = appSettings;
        }

        public async Task<string> CheckPbeStatusAsync()
        {
            try
            {
                var response = await _httpClient.GetStringAsync(PbeStatusUrl);
                string currentStatus = ExtractStatus(response);

                if (!string.IsNullOrEmpty(currentStatus) && currentStatus != _appSettings.LastPbeStatusMessage)
                {
                    _appSettings.LastPbeStatusMessage = currentStatus;
                    AppSettings.SaveSettings(_appSettings);
                    return $"PBE Status: {currentStatus}"; // Return the message
                }
                else if (string.IsNullOrEmpty(currentStatus) && !string.IsNullOrEmpty(_appSettings.LastPbeStatusMessage))
                {
                    _appSettings.LastPbeStatusMessage = "";
                    AppSettings.SaveSettings(_appSettings);
                    return "PBE Status: Maintenance ended."; // Return the message
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to check PBE status.");
            }
            finally
            {
                StatusChecked?.Invoke();
            }
            return null; // No change or error
        }

        private string ExtractStatus(string jsonContent)
        {
            try
            {
                if (string.IsNullOrEmpty(jsonContent)) return string.Empty;

                var data = JObject.Parse(jsonContent);
                var maintenances = data["maintenances"] as JArray;

                if (maintenances == null || maintenances.Count == 0) return string.Empty;

                var firstMaintenance = maintenances[0];
                var updates = firstMaintenance?["updates"] as JArray;

                if (updates == null || updates.Count == 0) return string.Empty;

                var latestUpdate = updates[0];
                var translations = latestUpdate?["translations"] as JArray;

                if (translations == null) return string.Empty;

                var enTranslation = translations.FirstOrDefault(t => t["locale"]?.ToString() == "en_US") ?? translations.FirstOrDefault(t => t["locale"]?.ToString().StartsWith("en_") ?? false);
                string originalContent = enTranslation?["content"]?.ToString();

                if (string.IsNullOrEmpty(originalContent)) return string.Empty;

                // 1. Parse Maintenance Start Time
                var match = Regex.Match(originalContent, @"(\d{2}/\d{2}/\d{4})\s*(\d{1,2}:\d{2})\s*([A-Z]{3})", RegexOptions.IgnoreCase);

                if (!match.Success) return originalContent; // Return original message if no time is found

                string dateStr = match.Groups[1].Value;
                string timeStr = match.Groups[2].Value;
                string tzAbbr = match.Groups[3].Value;

                if (!DateTime.TryParseExact($"{dateStr} {timeStr}", "MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var maintenanceDateTime))
                {
                    return originalContent; // Return original if parsing fails
                }

                if (!TimeZoneAbbreviations.TryGetValue(tzAbbr, out var offset))
                {
                    return originalContent; // Return original if timezone is unknown
                }

                var maintenanceStartTime = new DateTimeOffset(maintenanceDateTime, offset);

                // 2. Parse Maintenance Duration
                TimeSpan duration = TimeSpan.FromHours(3); // Default grace period of 3 hours
                var durationMatch = Regex.Match(originalContent, @"for approximately (\d+)\s+(hour|minute)s?", RegexOptions.IgnoreCase);

                if (durationMatch.Success)
                {
                    if (int.TryParse(durationMatch.Groups[1].Value, out int durationValue))
                    {
                        string unit = durationMatch.Groups[2].Value.ToLower();
                        if (unit.StartsWith("hour"))
                        {
                            duration = TimeSpan.FromHours(durationValue);
                        }
                        else if (unit.StartsWith("minute"))
                        {
                            duration = TimeSpan.FromMinutes(durationValue);
                        }
                    }
                }

                // 3. Calculate End Time and Check if Expired
                var maintenanceEndTime = maintenanceStartTime.Add(duration);

                if (maintenanceEndTime.ToUniversalTime() < DateTime.UtcNow)
                {
                    return string.Empty; // Maintenance window has passed
                }

                // 4. Format the final message by injecting the user's local time.
                var localMaintenanceTime = maintenanceStartTime.ToLocalTime();
                string newDateTimeString = $"{match.Value} ({localMaintenanceTime:HH:mm} your timezone)";

                return originalContent.Replace(match.Value, newDateTimeString);

            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse PBE status JSON.");
                return "Failed to parse PBE status information.";
            }
        }
    }
}
