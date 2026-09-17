using Microsoft.Win32;
using AnythingSearch.Helper;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
/// Multi-layer country detection:
/// Layer 1: Windows GeoID API     → Instant, 100% offline, most reliable
/// Layer 2: Registry / CultureInfo → Instant fallback
/// Layer 3: ipapi.co (your current)→ Last resort with short timeout
/// </summary>
public static class CountryDetectionService
{
    #region ── Layer 1: Windows GeoID API (Native, Zero Network) ──────────────

    // Windows GeoID constants
    private const int GEOCLASS_NATION = 16;
    private const int GEO_FRIENDLYNAME = 8;
    private const int GEO_ISO2 = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserGeoID(int GeoClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetGeoInfo(int GeoId, int GeoType,
        System.Text.StringBuilder lpGeoData, int cchData, int LangId);

    /// <summary>
    /// Gets country name directly from Windows GeoID — no network required.
    /// This is what Windows uses internally for regional settings.
    /// </summary>
    public static (string CountryName, string IsoCode) GetFromWindowsGeoId()
    {
        try
        {
            int geoId = GetUserGeoID(GEOCLASS_NATION);

            if (geoId <= 0)
                return (string.Empty, string.Empty);

            // Get friendly name (e.g., "United States")
            var nameBuffer = new System.Text.StringBuilder(256);
            int nameResult = GetGeoInfo(geoId, GEO_FRIENDLYNAME, nameBuffer, 256, 0);

            // Get ISO 2-letter code (e.g., "US")
            var isoBuffer = new System.Text.StringBuilder(10);
            int isoResult = GetGeoInfo(geoId, GEO_ISO2, isoBuffer, 10, 0);

            string countryName = nameResult > 0 ? nameBuffer.ToString().Trim() : string.Empty;
            string isoCode = isoResult > 0 ? isoBuffer.ToString().Trim() : string.Empty;

            Logger.Log($"[GeoID] Country: {countryName} ({isoCode}), GeoId: {geoId}");
            return (countryName, isoCode);
        }
        catch (Exception ex)
        {
            Logger.Log($"[GeoID] Failed: {ex.Message}");
            return (string.Empty, string.Empty);
        }
    }

    #endregion

    #region ── Layer 2a: Windows Registry ────────────────────────────────────

    /// <summary>
    /// Reads country from Windows Registry — instant, offline, very reliable.
    /// HKCU\Control Panel\International\Geo stores the user's configured country.
    /// </summary>
    public static string GetFromRegistry()
    {
        try
        {
            // Primary: User's configured geographic location
            using var geoKey = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\International\Geo");

            if (geoKey != null)
            {
                string? nation = geoKey.GetValue("Nation") as string;
                if (!string.IsNullOrWhiteSpace(nation))
                {
                    // Nation is a GeoID number — convert to name
                    if (int.TryParse(nation, out int geoId))
                    {
                        var buffer = new System.Text.StringBuilder(256);
                        int result = GetGeoInfo(geoId, GEO_FRIENDLYNAME, buffer, 256, 0);
                        if (result > 0)
                        {
                            string name = buffer.ToString().Trim();
                            Logger.Log($"[Registry] Country from GeoID {geoId}: {name}");
                            return name;
                        }
                    }
                }
            }

            // Fallback: System locale country
            using var intlKey = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\International");

            if (intlKey != null)
            {
                string? localeName = intlKey.GetValue("LocaleName") as string;
                if (!string.IsNullOrWhiteSpace(localeName))
                {
                    // e.g., "en-US" → extract country from region info
                    var region = new RegionInfo(localeName);
                    Logger.Log($"[Registry] Country from LocaleName: {region.EnglishName}");
                    return region.EnglishName;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Registry] Failed: {ex.Message}");
        }

        return string.Empty;
    }

    #endregion

    #region ── Layer 2b: CultureInfo / RegionInfo ─────────────────────────────

    /// <summary>
    /// Gets country from .NET's RegionInfo — instant, always available.
    /// Uses the installed UI culture (OS language setting).
    /// </summary>
    public static string GetFromCultureInfo()
    {
        try
        {
            // Try current UI culture first (most accurate for user's region)
            var cultures = new[]
            {
                CultureInfo.CurrentCulture,
                CultureInfo.CurrentUICulture,
                CultureInfo.InstalledUICulture
            };

            foreach (var culture in cultures)
            {
                try
                {
                    if (!culture.IsNeutralCulture && culture.Name.Contains('-'))
                    {
                        var region = new RegionInfo(culture.Name);
                        if (!string.IsNullOrWhiteSpace(region.EnglishName))
                        {
                            Logger.Log(
                                $"[CultureInfo] Country: {region.EnglishName} " +
                                $"from culture: {culture.Name}");
                            return region.EnglishName;
                        }
                    }
                }
                catch { /* try next */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[CultureInfo] Failed: {ex.Message}");
        }

        return string.Empty;
    }

    #endregion

    #region ── Layer 3: IP-based API (Last Resort, Short Timeout) ─────────────

    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(3) // Short — we have better layers above
    };

    // Multiple free APIs as fallbacks
    private static readonly string[] _ipApiUrls =
    [
        "https://ipapi.co/country_name/",
        "https://api.country.is/",          // Returns JSON: {"ip":"...","country":"US"}
        "https://ipwho.is/?fields=country"  // Returns JSON: {"country":"United States"}
    ];

    public static async Task<string> GetFromIpApiAsync(
        CancellationToken cancellationToken = default)
    {
        foreach (var url in _ipApiUrls)
        {
            try
            {
                using var cts = CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3));

                var response = await _httpClient.GetStringAsync(url, cts.Token);

                if (string.IsNullOrWhiteSpace(response))
                    continue;

                // ipapi.co returns plain text
                if (url.Contains("ipapi.co"))
                {
                    var name = response.Trim();
                    if (!name.StartsWith("{") && name.Length > 2)
                    {
                        Logger.Log($"[IpApi] ipapi.co: {name}");
                        return name;
                    }
                }

                // country.is returns {"ip":"x.x.x.x","country":"US"}
                if (url.Contains("country.is"))
                {
                    var isoCode = ExtractJsonValue(response, "country");
                    if (!string.IsNullOrWhiteSpace(isoCode) && isoCode.Length == 2)
                    {
                        var countryName = IsoCodeToCountryName(isoCode);
                        Logger.Log($"[IpApi] country.is: {isoCode} → {countryName}");
                        return countryName;
                    }
                }

                // ipwho.is returns {"country":"United States",...}
                if (url.Contains("ipwho.is"))
                {
                    var name = ExtractJsonValue(response, "country");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        Logger.Log($"[IpApi] ipwho.is: {name}");
                        return name;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[IpApi] {url} failed: {ex.Message}");
            }
        }

        return string.Empty;
    }

    #endregion

    #region ── Master Method: Cascading Detection ────────────────────────────

    /// <summary>
    /// Returns country name using the fastest available method.
    /// Guaranteed to never return "Unknown (Timeout)".
    /// 
    /// Priority:
    ///   1. Windows GeoID API    → ~0ms, 100% reliable offline
    ///   2. Windows Registry     → ~0ms, very reliable
    ///   3. .NET CultureInfo     → ~0ms, always available
    ///   4. IP-based API         → ~500-3000ms, network required
    ///   5. "Unknown"            → absolute last resort
    /// </summary>
    public static async Task<string> GetCountryNameAsync(
    string? timeZoneId = null,
    string? appLanguage = null,
    string? sysLanguage = null,
    CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Layer 1: Windows GeoID (~0ms)
        var (geoName, _) = GetFromWindowsGeoId();
        if (!string.IsNullOrWhiteSpace(geoName))
        {
            Logger.Log($"[Country] GeoID → '{geoName}' in {sw.ElapsedMilliseconds}ms");
            return geoName;
        }

        // Layer 2a: Registry (~0ms)
        var regName = GetFromRegistry();
        if (!string.IsNullOrWhiteSpace(regName))
        {
            Logger.Log($"[Country] Registry → '{regName}' in {sw.ElapsedMilliseconds}ms");
            return regName;
        }

        // Layer 2b: CultureInfo (~0ms)
        var cultureName = GetFromCultureInfo();
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            Logger.Log($"[Country] CultureInfo → '{cultureName}' in {sw.ElapsedMilliseconds}ms");
            return cultureName;
        }

        // ✅ Layer 4: Timezone + Language Triangulation (~0ms, NEW)
        var triangulated = TimezoneLanguageTriangulator.GetCountry(timeZoneId, appLanguage, sysLanguage);
        if (!string.IsNullOrWhiteSpace(triangulated))
        {
            Logger.Log($"[Country] Triangulation → '{triangulated}' in {sw.ElapsedMilliseconds}ms");
            return triangulated;
        }

        // Layer 3: IP APIs (network, last resort)
        Logger.Log("[Country] All offline layers failed → trying IP APIs...");
        var ipName = await GetFromIpApiAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(ipName))
        {
            Logger.Log($"[Country] IP API → '{ipName}' in {sw.ElapsedMilliseconds}ms");
            return ipName;
        }

        Logger.Log($"[Country] All layers failed after {sw.ElapsedMilliseconds}ms");
        return "Unknown";
    }

    /// <summary>
    /// Synchronous version for non-async contexts.
    /// Uses only offline layers — guaranteed instant result.
    /// </summary>
    public static string GetCountryNameSync()
    {
        var (geoName, _) = GetFromWindowsGeoId();
        if (!string.IsNullOrWhiteSpace(geoName)) return geoName;

        var registryName = GetFromRegistry();
        if (!string.IsNullOrWhiteSpace(registryName)) return registryName;

        var cultureName = GetFromCultureInfo();
        if (!string.IsNullOrWhiteSpace(cultureName)) return cultureName;

        return "Unknown";
    }

    #endregion

    #region ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Simple JSON value extractor — avoids System.Text.Json dependency.
    /// </summary>
    private static string ExtractJsonValue(string json, string key)
    {
        try
        {
            string search = $"\"{key}\":\"";
            int start = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;

            start += search.Length;
            int end = json.IndexOf('"', start);
            return end > start ? json[start..end] : string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Converts ISO 2-letter country code to English name via .NET RegionInfo.
    /// e.g., "US" → "United States", "DE" → "Germany"
    /// </summary>
    public static string IsoCodeToCountryName(string isoCode)
    {
        if (string.IsNullOrWhiteSpace(isoCode)) return string.Empty;
        try
        {
            var region = new RegionInfo(isoCode.ToUpperInvariant());
            return region.EnglishName;
        }
        catch
        {
            return isoCode; // Return code if lookup fails
        }
    }

    #endregion
}
