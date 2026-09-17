using AnythingSearch.Helper;
using System.Diagnostics;
using System.Globalization;

/// <summary>
/// Triangulates the most likely country from Windows TimeZone + Language tags.
/// Zero network calls — 100% offline, instant results.
/// 
/// Usage:
///   string country = TimezoneLanguageTriangulator.GetCountry();
///   string country = TimezoneLanguageTriangulator.GetCountry("Pakistan Standard Time", "en-US", "en-PK");
/// </summary>
public static class TimezoneLanguageTriangulator
{
    #region ── Public Entry Points ───────────────────────────────────────────

    /// <summary>
    /// Auto-detects country using current system timezone and language settings.
    /// </summary>
    public static string GetCountry()
    {
        return GetCountry(
            timeZoneId: TimeZoneInfo.Local.Id,
            appLanguage: CultureInfo.CurrentUICulture.Name,
            sysLanguage: CultureInfo.InstalledUICulture.Name
        );
    }

    /// <summary>
    /// Triangulates country from the provided timezone and language signals.
    /// </summary>
    /// <param name="timeZoneId">Windows TZ ID or display name, e.g. "Pakistan Standard Time"</param>
    /// <param name="appLanguage">BCP-47 app language tag, e.g. "en-US"</param>
    /// <param name="sysLanguage">BCP-47 OS language tag, e.g. "en-PK"</param>
    /// <returns>English country name, or empty string if undetermined.</returns>
    public static string GetCountry(
        string? timeZoneId,
        string? appLanguage,
        string? sysLanguage)
    {
        timeZoneId = NormalizeInput(timeZoneId);
        appLanguage = NormalizeInput(appLanguage);
        sysLanguage = NormalizeInput(sysLanguage);

        Logger.Log(
            $"[Triangulator] TZ='{timeZoneId}' " +
            $"App='{appLanguage}' Sys='{sysLanguage}'");

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ── Signal 1: TimeZone (strongest — weight 3) ──────────────────────
        // A timezone often maps to a single country (e.g. "Nepal Standard Time")
        foreach (var country in ResolveTimezone(timeZoneId))
            Score(scores, country, Weight.Timezone);

        // ── Signal 2: System language region tag (weight 2) ────────────────
        // OS language is configured by IT/user for their actual region
        foreach (var country in ResolveLanguageTag(sysLanguage))
            Score(scores, country, Weight.SystemLanguage);

        // ── Signal 3: App language region tag (weight 1) ───────────────────
        // App language may differ from user's country (e.g. en-US on a PK device)
        foreach (var country in ResolveLanguageTag(appLanguage))
            Score(scores, country, Weight.AppLanguage);

        if (scores.Count == 0)
        {
            Logger.Log("[Triangulator] No candidates found");
            return string.Empty;
        }

        // Pick highest score; on tie, prefer timezone candidate
        var best = scores
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => IsTzCandidate(kvp.Key, timeZoneId) ? 0 : 1)
            .First();

        Logger.Log(
            $"[Triangulator] Scores: " +
            $"{string.Join(", ", scores.OrderByDescending(s => s.Value).Select(s => $"{s.Key}={s.Value}"))}");
        Logger.Log(
            $"[Triangulator] ✅ Result: '{best.Key}' (score {best.Value})");

        return best.Key;
    }

    /// <summary>
    /// Returns all candidate countries with their confidence scores.
    /// Useful for diagnostics or when you want to inspect the full ranking.
    /// </summary>
    public static IReadOnlyList<(string Country, int Score)> GetRankedCandidates(
        string? timeZoneId = null,
        string? appLanguage = null,
        string? sysLanguage = null)
    {
        timeZoneId = NormalizeInput(timeZoneId ?? TimeZoneInfo.Local.Id);
        appLanguage = NormalizeInput(appLanguage ?? CultureInfo.CurrentUICulture.Name);
        sysLanguage = NormalizeInput(sysLanguage ?? CultureInfo.InstalledUICulture.Name);

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in ResolveTimezone(timeZoneId))
            Score(scores, c, Weight.Timezone);

        foreach (var c in ResolveLanguageTag(sysLanguage))
            Score(scores, c, Weight.SystemLanguage);

        foreach (var c in ResolveLanguageTag(appLanguage))
            Score(scores, c, Weight.AppLanguage);

        return scores
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    #endregion

    #region ── Scoring Weights ───────────────────────────────────────────────

    private static class Weight
    {
        public const int Timezone = 3; // Most specific — often 1 country
        public const int SystemLanguage = 2; // OS region — very reliable
        public const int AppLanguage = 1; // May be overridden by user
    }

    private static void Score(Dictionary<string, int> scores, string country, int weight)
    {
        if (string.IsNullOrWhiteSpace(country)) return;
        country = country.Trim();
        scores[country] = scores.TryGetValue(country, out int existing)
            ? existing + weight
            : weight;
    }

    private static bool IsTzCandidate(string country, string? tzId)
    {
        if (string.IsNullOrWhiteSpace(tzId)) return false;
        return _timezoneMap.TryGetValue(tzId, out var candidates) &&
               candidates.Contains(country, StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region ── Timezone Resolution ───────────────────────────────────────────

    /// <summary>
    /// Resolves a timezone ID or display name to candidate country names.
    /// Tries: exact map → Windows TZ canonical ID → StandardName → keyword extraction.
    /// </summary>
    public static IReadOnlyList<string> ResolveTimezone(string? tzInput)
    {
        if (string.IsNullOrWhiteSpace(tzInput)) return [];

        // 1. Direct map lookup (canonical Windows TZ ID)
        if (_timezoneMap.TryGetValue(tzInput, out var direct))
            return direct;

        // 2. Try current system TZ canonical ID and StandardName
        try
        {
            var localTz = TimeZoneInfo.Local;

            if (_timezoneMap.TryGetValue(localTz.Id, out var byId))
                return byId;

            if (_timezoneMap.TryGetValue(localTz.StandardName, out var byStd))
                return byStd;
        }
        catch { /* ignore */ }

        // 3. Try all registered Windows timezones for a match
        try
        {
            foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            {
                if (string.Equals(tz.StandardName, tzInput,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tz.DisplayName, tzInput,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (_timezoneMap.TryGetValue(tz.Id, out var byMatch))
                        return byMatch;
                }
            }
        }
        catch { /* ignore */ }

        // 4. Partial / substring match in map keys
        foreach (var kvp in _timezoneMap)
        {
            if (tzInput.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.Contains(tzInput, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        // 5. Keyword extraction from localized display names
        return ExtractFromLocalizedName(tzInput);
    }

    /// <summary>
    /// Extracts country hint from localized timezone names like:
    /// "Hora estándar de la India" → "India"
    /// "Türkiye Standart Saati"   → "Turkey"
    /// "Heure standard du Maroc"  → "Morocco"
    /// </summary>
    private static IReadOnlyList<string> ExtractFromLocalizedName(string tzName)
    {
        // Prepositions used in timezone names across many languages
        string[] separators =
        [
            " de la ", " de l'", " de l'", " de ", " del ",
            " von ", " van ", " di ", " do ", " da ",
            " of ", " от ", " de l'", " du ", " d'",
            " standard time", " standart saati",
            " saati", " waktu ", " uur "
        ];

        foreach (var sep in separators)
        {
            int idx = tzName.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            string candidate = tzName[(idx + sep.Length)..].Trim();

            // Strip trailing parenthetical like "(zima)", "(běžný čas)"
            int paren = candidate.IndexOf('(');
            if (paren > 0) candidate = candidate[..paren].Trim();

            // Strip trailing comma or period
            candidate = candidate.TrimEnd(',', '.').Trim();

            if (candidate.Length >= 3)
            {
                // Try to match against known country names in our map values
                var matched = FindCountryByName(candidate);
                if (!string.IsNullOrWhiteSpace(matched))
                {
                    Logger.Log(
                        $"[Triangulator] Keyword '{candidate}' → '{matched}'");
                    return [matched];
                }

                // Return raw candidate — caller will use it as-is
                Logger.Log(
                    $"[Triangulator] Raw keyword candidate: '{candidate}'");
                return [candidate];
            }
        }

        // Last attempt: check if the whole string contains a known country name
        var direct = FindCountryByName(tzName);
        if (!string.IsNullOrWhiteSpace(direct))
            return [direct];

        return [];
    }

    /// <summary>
    /// Searches all map values for a country name that matches the input.
    /// </summary>
    private static string FindCountryByName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        foreach (var countries in _timezoneMap.Values)
        {
            foreach (var c in countries)
            {
                if (c.Equals(input, StringComparison.OrdinalIgnoreCase) ||
                    input.Contains(c, StringComparison.OrdinalIgnoreCase) ||
                    c.Contains(input, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
        }

        return string.Empty;
    }

    #endregion

    #region ── Language Tag Resolution ──────────────────────────────────────

    /// <summary>
    /// Resolves a BCP-47 language tag to candidate country names.
    /// "fr-CM" → ["Cameroon"]
    /// "en-PK" → ["Pakistan"]
    /// "ar"    → ["Saudi Arabia", "Egypt", ...]
    /// </summary>
    public static IReadOnlyList<string> ResolveLanguageTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return [];

        try
        {
            if (tag.Contains('-'))
            {
                var parts = tag.Split('-');

                // Try last segment first (handles "zh-Hans-CN" → "CN")
                for (int i = parts.Length - 1; i >= 1; i--)
                {
                    string seg = parts[i];
                    if (seg.Length == 2 && seg.All(char.IsLetter))
                    {
                        string name = IsoToCountryName(seg.ToUpperInvariant());
                        if (!string.IsNullOrWhiteSpace(name))
                            return [name];
                    }
                }

                // Try .NET RegionInfo directly
                try
                {
                    var region = new RegionInfo(tag);
                    if (!string.IsNullOrWhiteSpace(region.EnglishName))
                        return [region.EnglishName];
                }
                catch { /* neutral culture */ }
            }

            // Neutral language code → multiple candidates
            string neutral = tag.Split('-')[0].ToLowerInvariant();
            if (_neutralLangMap.TryGetValue(neutral, out var candidates))
                return candidates;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Triangulator] Language tag error '{tag}': {ex.Message}");
        }

        return [];
    }

    /// <summary>
    /// Converts ISO 3166-1 alpha-2 code to English country name via .NET RegionInfo.
    /// "PK" → "Pakistan", "DE" → "Germany", "CM" → "Cameroon"
    /// </summary>
    public static string IsoToCountryName(string isoCode)
    {
        if (string.IsNullOrWhiteSpace(isoCode) || isoCode.Length != 2)
            return string.Empty;
        try
        {
            return new RegionInfo(isoCode.ToUpperInvariant()).EnglishName;
        }
        catch
        {
            return string.Empty;
        }
    }

    #endregion

    #region ── Helpers ───────────────────────────────────────────────────────

    private static string NormalizeInput(string? input)
        => string.IsNullOrWhiteSpace(input) ? string.Empty : input.Trim();

    #endregion

    #region ── Timezone → Country Map ───────────────────────────────────────
    //
    // Keys   = Windows canonical TZ IDs (from tzutil /l) AND localized names
    //          seen in real-world telemetry (CSV data).
    // Values = Ordered list of countries (most likely first).
    //
    // Coverage: ~200 Windows timezone IDs + localized display names in
    //           English, Spanish, French, Portuguese, Turkish, Arabic,
    //           Russian, Hungarian, Czech, Italian, German, Dutch.
    //
    private static readonly Dictionary<string, string[]> _timezoneMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── UTC ────────────────────────────────────────────────────────────
            ["UTC"] = ["Unknown"],
            ["Coordinated Universal Time"] = ["Unknown"],

            // ── Africa / Atlantic ──────────────────────────────────────────────
            ["Azores Standard Time"] = ["Portugal"],
            ["Cape Verde Standard Time"] = ["Cape Verde"],
            ["Morocco Standard Time"] = ["Morocco"],
            ["GMT Standard Time"] = ["United Kingdom", "Ireland",
                                                        "Portugal"],
            ["Greenwich Standard Time"] = ["Iceland", "Ghana",
                                                        "Senegal", "Gambia",
                                                        "Guinea", "Guinea-Bissau",
                                                        "Sierra Leone", "Liberia"],
            ["W. Central Africa Standard Time"] = ["Nigeria", "Cameroon",
                                                        "Chad", "Niger", "Gabon",
                                                        "Congo",
                                                        "Central African Republic",
                                                        "Equatorial Guinea"],
            ["W. Africa Standard Time"] = ["Senegal", "Mali",
                                                        "Mauritania", "Guinea",
                                                        "Ivory Coast",
                                                        "Burkina Faso"],
            ["Sao Tome Standard Time"] = ["Sao Tome and Principe"],
            ["E. Africa Standard Time"] = ["Kenya", "Ethiopia",
                                                        "Tanzania", "Uganda",
                                                        "Somalia", "Djibouti",
                                                        "Eritrea"],
            ["South Africa Standard Time"] = ["South Africa", "Zimbabwe",
                                                        "Mozambique", "Zambia",
                                                        "Botswana", "Malawi",
                                                        "Lesotho", "Eswatini"],
            ["Egypt Standard Time"] = ["Egypt"],
            ["Libya Standard Time"] = ["Libya"],
            ["Sudan Standard Time"] = ["Sudan"],
            ["Namibia Standard Time"] = ["Namibia"],
            ["Mauritius Standard Time"] = ["Mauritius"],

            // ── Europe ────────────────────────────────────────────────────────
            ["Romance Standard Time"] = ["France", "Belgium",
                                                        "Spain", "Netherlands"],
            ["W. Europe Standard Time"] = ["Germany", "Austria",
                                                        "Switzerland",
                                                        "Netherlands", "Italy",
                                                        "Sweden", "Norway",
                                                        "Luxembourg"],
            ["Central Europe Standard Time"] = ["Hungary", "Czech Republic",
                                                        "Slovakia", "Slovenia",
                                                        "Croatia"],
            ["Central European Standard Time"] = ["Poland", "Serbia",
                                                        "Bosnia and Herzegovina",
                                                        "North Macedonia",
                                                        "Albania", "Kosovo",
                                                        "Montenegro"],
            ["E. Europe Standard Time"] = ["Cyprus"],
            ["FLE Standard Time"] = ["Finland", "Latvia",
                                                        "Lithuania", "Estonia",
                                                        "Ukraine"],
            ["GTB Standard Time"] = ["Greece", "Romania",
                                                        "Bulgaria"],
            ["Turkey Standard Time"] = ["Turkey"],
            ["Russian Standard Time"] = ["Russia", "Belarus"],
            ["Kaliningrad Standard Time"] = ["Russia"],
            ["Belarus Standard Time"] = ["Belarus"],
            ["Ukraine Standard Time"] = ["Ukraine"],
            ["Caucasus Standard Time"] = ["Armenia"],
            ["Georgian Standard Time"] = ["Georgia"],
            ["Astrakhan Standard Time"] = ["Russia"],
            ["Saratov Standard Time"] = ["Russia"],
            ["Volgograd Standard Time"] = ["Russia"],
            ["Ulyanovsk Standard Time"] = ["Russia"],

            // ── Middle East ───────────────────────────────────────────────────
            ["Arab Standard Time"] = ["Saudi Arabia", "Kuwait",
                                                        "Qatar", "Bahrain",
                                                        "Yemen", "Iraq"],
            ["Arabian Standard Time"] = ["United Arab Emirates",
                                                        "Oman"],
            ["Arabic Standard Time"] = ["Iraq"],
            ["Israel Standard Time"] = ["Israel"],
            ["Jordan Standard Time"] = ["Jordan"],
            ["Syria Standard Time"] = ["Syria"],
            ["West Bank Standard Time"] = ["Palestinian Territories"],
            ["Iran Standard Time"] = ["Iran"],
            ["Azerbaijan Standard Time"] = ["Azerbaijan"],
            ["Azerbaijani Standard Time"] = ["Azerbaijan"],

            // ── South Asia ────────────────────────────────────────────────────
            ["Pakistan Standard Time"] = ["Pakistan"],
            ["India Standard Time"] = ["India"],
            ["Sri Lanka Standard Time"] = ["Sri Lanka"],
            ["Bangladesh Standard Time"] = ["Bangladesh"],
            ["Nepal Standard Time"] = ["Nepal"],
            ["Afghanistan Standard Time"] = ["Afghanistan"],

            // ── Central Asia ──────────────────────────────────────────────────
            ["Central Asia Standard Time"] = ["Kazakhstan",
                                                        "Kyrgyzstan"],
            ["Ekaterinburg Standard Time"] = ["Russia"],
            ["West Asia Standard Time"] = ["Uzbekistan",
                                                        "Tajikistan",
                                                        "Turkmenistan"],
            ["Qyzylorda Standard Time"] = ["Kazakhstan"],
            ["Omsk Standard Time"] = ["Russia"],
            ["N. Central Asia Standard Time"] = ["Russia"],

            // ── East / SE Asia ────────────────────────────────────────────────
            ["China Standard Time"] = ["China", "Taiwan"],
            ["Singapore Standard Time"] = ["Singapore", "Malaysia"],
            ["SE Asia Standard Time"] = ["Thailand", "Vietnam",
                                                        "Indonesia", "Cambodia",
                                                        "Laos"],
            ["North Asia Standard Time"] = ["Russia"],
            ["North Asia East Standard Time"] = ["Russia"],
            ["Ulaanbaatar Standard Time"] = ["Mongolia"],
            ["Korea Standard Time"] = ["South Korea"],
            ["Tokyo Standard Time"] = ["Japan"],
            ["Taipei Standard Time"] = ["Taiwan"],
            ["Hong Kong Standard Time"] = ["Hong Kong"],
            ["Myanmar Standard Time"] = ["Myanmar"],
            ["W. Mongolia Standard Time"] = ["Mongolia"],
            ["Yakutsk Standard Time"] = ["Russia"],
            ["Vladivostok Standard Time"] = ["Russia"],
            ["Russia Time Zone 9"] = ["Russia"],
            ["Russia Time Zone 10"] = ["Russia"],
            ["Russia Time Zone 11"] = ["Russia"],
            ["Magadan Standard Time"] = ["Russia"],
            ["Sakhalin Standard Time"] = ["Russia"],
            ["Kamchatka Standard Time"] = ["Russia"],

            // ── Oceania ───────────────────────────────────────────────────────
            ["AUS Central Standard Time"] = ["Australia"],
            ["AUS Eastern Standard Time"] = ["Australia"],
            ["Aus Central W. Standard Time"] = ["Australia"],
            ["E. Australia Standard Time"] = ["Australia"],
            ["W. Australia Standard Time"] = ["Australia"],
            ["Cen. Australia Standard Time"] = ["Australia"],
            ["Tasmania Standard Time"] = ["Australia"],
            ["Lord Howe Standard Time"] = ["Australia"],
            ["New Zealand Standard Time"] = ["New Zealand"],
            ["Chatham Islands Standard Time"] = ["New Zealand"],
            ["Fiji Standard Time"] = ["Fiji"],
            ["Tonga Standard Time"] = ["Tonga"],
            ["Samoa Standard Time"] = ["Samoa"],
            ["Norfolk Standard Time"] = ["Australia"],
            ["Bougainville Standard Time"] = ["Papua New Guinea"],
            ["Papua New Guinea Standard Time"] = ["Papua New Guinea"],
            ["West Pacific Standard Time"] = ["Papua New Guinea",
                                                        "Guam"],
            ["Central Pacific Standard Time"] = ["Solomon Islands",
                                                        "Vanuatu"],

            // ── Americas — North ───────────────────────────────────────────────
            ["Eastern Standard Time"] = ["United States", "Canada"],
            ["Central Standard Time"] = ["United States", "Canada"],
            ["Mountain Standard Time"] = ["United States", "Canada"],
            ["Pacific Standard Time"] = ["United States", "Canada"],
            ["US Eastern Standard Time"] = ["United States"],
            ["US Mountain Standard Time"] = ["United States"],
            ["Alaskan Standard Time"] = ["United States"],
            ["Hawaiian Standard Time"] = ["United States"],
            ["Canada Central Standard Time"] = ["Canada"],
            ["Atlantic Standard Time"] = ["Canada"],
            ["Newfoundland Standard Time"] = ["Canada"],
            ["Yukon Standard Time"] = ["Canada"],

            // ── Americas — Mexico / Central ────────────────────────────────────
            ["Mexico Standard Time"] = ["Mexico"],
            ["Mexico Standard Time 2"] = ["Mexico"],
            ["Central Standard Time (Mexico)"] = ["Mexico"],
            ["Mountain Standard Time (Mexico)"] = ["Mexico"],
            ["Pacific Standard Time (Mexico)"] = ["Mexico"],
            ["Central America Standard Time"] = ["Guatemala", "Honduras",
                                                        "El Salvador",
                                                        "Nicaragua",
                                                        "Costa Rica", "Belize"],
            ["Cuba Standard Time"] = ["Cuba"],
            ["Haiti Standard Time"] = ["Haiti"],
            ["Turks And Caicos Standard Time"] = ["Turks and Caicos Islands"],
            ["Eastern Standard Time (Mexico)"] = ["Mexico"],

            // ── Americas — Caribbean / South ───────────────────────────────────
            ["SA Eastern Standard Time"] = ["Brazil", "Argentina",
                                                        "Uruguay", "Paraguay",
                                                        "Suriname", "Guyana"],
            ["SA Western Standard Time"] = ["Bolivia", "Peru",
                                                        "Ecuador", "Colombia",
                                                        "Venezuela"],
            ["SA Pacific Standard Time"] = ["Colombia", "Peru",
                                                        "Ecuador"],
            ["E. South America Standard Time"] = ["Brazil"],
            ["Bahia Standard Time"] = ["Brazil"],
            ["Argentina Standard Time"] = ["Argentina"],
            ["Venezuela Standard Time"] = ["Venezuela"],
            ["Central Brazilian Standard Time"] = ["Brazil"],
            ["Montevideo Standard Time"] = ["Uruguay"],
            ["Paraguay Standard Time"] = ["Paraguay"],
            ["Chile Standard Time"] = ["Chile"],
            ["Magallanes Standard Time"] = ["Chile"],
            ["Tocantins Standard Time"] = ["Brazil"],
            ["Greenland Standard Time"] = ["Greenland"],
            ["Saint Pierre Standard Time"] = ["Saint Pierre and Miquelon"],

            // ══════════════════════════════════════════════════════════════════
            // LOCALIZED TIMEZONE DISPLAY NAMES
            // Sourced from real-world telemetry (CSV) and Windows locale data.
            // These are the StandardName values Windows returns in non-English
            // UI languages when TimeZoneInfo.Local.StandardName is called.
            // ══════════════════════════════════════════════════════════════════

            // ── Spanish (es-*) ─────────────────────────────────────────────────
            ["Hora estándar de la India"] = ["India"],
            ["Hora estándar de Pakistán"] = ["Pakistan"],
            ["Hora estándar de Afganistán"] = ["Afghanistan"],
            ["Hora estándar de Bangladesh"] = ["Bangladesh"],
            ["Hora estándar de Nepal"] = ["Nepal"],
            ["Hora estándar de Sri Lanka"] = ["Sri Lanka"],
            ["Hora estándar de Myanmar"] = ["Myanmar"],
            ["Hora estándar de Tailandia"] = ["Thailand"],
            ["Hora estándar de Vietnam"] = ["Vietnam"],
            ["Hora estándar de Indonesia"] = ["Indonesia"],
            ["Hora estándar de Filipinas"] = ["Philippines"],
            ["Hora estándar de Malasia"] = ["Malaysia"],
            ["Hora estándar de Singapur"] = ["Singapore"],
            ["Hora estándar de China"] = ["China"],
            ["Hora estándar de Taipéi"] = ["Taiwan"],
            ["Hora estándar de Corea"] = ["South Korea"],
            ["Hora estándar de Tokio"] = ["Japan"],
            ["Hora estándar de Mongolia"] = ["Mongolia"],
            ["Hora estándar de Arabia"] = ["Saudi Arabia", "Yemen"],
            ["Hora estándar arábiga"] = ["Iraq"],
            ["Hora estándar de Irán"] = ["Iran"],
            ["Hora estándar de Azerbaiyán"] = ["Azerbaijan"],
            ["Hora estándar de Georgia"] = ["Georgia"],
            ["Hora estándar de Turquía"] = ["Turkey"],
            ["Hora estándar de Israel"] = ["Israel"],
            ["Hora estándar de Jordania"] = ["Jordan"],
            ["Hora estándar de Siria"] = ["Syria"],
            ["Hora estándar de Egipto"] = ["Egypt"],
            ["Hora estándar de Libia"] = ["Libya"],
            ["Hora estándar de Sudán"] = ["Sudan"],
            ["Hora estándar de Marruecos"] = ["Morocco"],
            ["Hora estándar de Namibia"] = ["Namibia"],
            ["Hora estándar de Sudáfrica"] = ["South Africa"],
            ["Hora estándar de Zimbabue"] = ["Zimbabwe"],
            ["Hora estándar de Kenia"] = ["Kenya"],
            ["Hora estándar de Etiopía"] = ["Ethiopia"],
            ["Hora estándar de Rusia"] = ["Russia"],
            ["Hora estándar de Bielorrusia"] = ["Belarus"],
            ["Hora estándar de Ucrania"] = ["Ukraine"],
            ["Hora estándar de Kazajistán"] = ["Kazakhstan"],
            ["Hora estándar de Uzbekistán"] = ["Uzbekistan"],
            ["Hora estándar de Australia Oriental"] = ["Australia"],
            ["Hora estándar de Australia Central"] = ["Australia"],
            ["Hora estándar de Australia Occidental"] = ["Australia"],
            ["Hora estándar de Nueva Zelanda"] = ["New Zealand"],
            ["Hora estándar de Fiyi"] = ["Fiji"],
            ["Hora estándar de Hawái"] = ["United States"],
            ["Hora estándar de Alaska"] = ["United States"],
            ["Hora estándar del Pacífico"] = ["United States", "Canada"],
            ["Hora estándar de la montaña"] = ["United States", "Canada"],
            ["Hora estándar central"] = ["United States", "Canada"],
            ["Hora estándar del Este"] = ["United States", "Canada"],
            ["Hora estándar del Atlántico"] = ["Canada"],
            ["Hora estándar de Terranova"] = ["Canada"],
            ["Hora estándar de Groenlandia"] = ["Greenland"],
            ["Hora estándar de México"] = ["Mexico"],
            ["Hora estándar central (México)"] = ["Mexico"],
            ["Hora estándar de la montaña (México)"] = ["Mexico"],
            ["Hora estándar del Pacífico (México)"] = ["Mexico"],
            ["Hora estándar de América Central"] = ["Guatemala", "Honduras",
                                                        "El Salvador",
                                                        "Nicaragua",
                                                        "Costa Rica"],
            ["Hora estándar de Cuba"] = ["Cuba"],
            ["Hora estándar de Haití"] = ["Haiti"],
            ["Horario estándar de Haití"] = ["Haiti"],
            ["Hora estándar de Colombia"] = ["Colombia"],
            ["Hora estándar de Perú"] = ["Peru"],
            ["Hora estándar de Ecuador"] = ["Ecuador"],
            ["Hora estándar de Bolivia"] = ["Bolivia"],
            ["Hora estándar de Venezuela"] = ["Venezuela"],
            ["Hora estándar de Chile"] = ["Chile"],
            ["Hora estándar de Paraguay"] = ["Paraguay"],
            ["Hora estándar de Uruguay"] = ["Uruguay"],
            ["Hora estándar de Argentina"] = ["Argentina"],
            ["Hora estándar de Brasil Oriental"] = ["Brazil"],
            ["Hora estándar de Brasil Central"] = ["Brazil"],
            ["Hora estándar de Brasília"] = ["Brazil"],
            ["Hora estándar de Bahía"] = ["Brazil"],
            ["Hora estándar de São Tomé"] = ["Sao Tome and Principe"],
            ["Hora estándar de Guatemala"] = ["Guatemala"],
            ["Hora estándar de Honduras"] = ["Honduras"],
            ["Hora estándar de El Salvador"] = ["El Salvador"],
            ["Hora estándar de Nicaragua"] = ["Nicaragua"],
            ["Hora estándar de Costa Rica"] = ["Costa Rica"],
            ["Hora estándar de Panamá"] = ["Panama"],
            ["Hora estándar de la República Dominicana"] = ["Dominican Republic"],
            ["Hora estándar de Jamaica"] = ["Jamaica"],
            ["Hora estándar de Puerto Rico"] = ["Puerto Rico"],
            ["Hora estándar de Trinidad y Tobago"] = ["Trinidad and Tobago"],
            ["Hora estándar de Guyana"] = ["Guyana"],
            ["Hora estándar de Surinam"] = ["Suriname"],
            ["Hora estándar de Magallanes"] = ["Chile"],
            ["Hora estándar de Tocantins"] = ["Brazil"],
            ["Hora estándar del Pacífico, Sudamérica"] = ["Colombia", "Peru",
                                                        "Ecuador"],
            ["Hora est. Pacífico, Sudamérica"] = ["Colombia", "Peru",
                                                        "Ecuador"],

            // ── French (fr-*) ──────────────────────────────────────────────────
            ["Heure standard de l'Inde"] = ["India"],
            ["Heure standard du Pakistan"] = ["Pakistan"],
            ["Heure standard de l'Afghanistan"] = ["Afghanistan"],
            ["Heure standard du Bangladesh"] = ["Bangladesh"],
            ["Heure standard du Népal"] = ["Nepal"],
            ["Heure standard du Sri Lanka"] = ["Sri Lanka"],
            ["Heure standard de la Birmanie"] = ["Myanmar"],
            ["Heure standard de la Thaïlande"] = ["Thailand"],
            ["Heure standard du Vietnam"] = ["Vietnam"],
            ["Heure standard de l'Indonésie"] = ["Indonesia"],
            ["Heure standard des Philippines"] = ["Philippines"],
            ["Heure standard de la Malaisie"] = ["Malaysia"],
            ["Heure standard de Singapour"] = ["Singapore"],
            ["Heure standard de Chine"] = ["China"],
            ["Heure standard de Corée"] = ["South Korea"],
            ["Heure standard du Japon"] = ["Japan"],
            ["Heure standard de Mongolie"] = ["Mongolia"],
            ["Heure standard de l'Arabie"] = ["Saudi Arabia", "Yemen"],
            ["Heure standard arabique"] = ["Iraq"],
            ["Heure standard de l'Iran"] = ["Iran"],
            ["Heure standard de l'Azerbaïdjan"] = ["Azerbaijan"],
            ["Heure standard de la Géorgie"] = ["Georgia"],
            ["Heure standard de la Turquie"] = ["Turkey"],
            ["Heure standard d'Israël"] = ["Israel"],
            ["Heure standard de la Jordanie"] = ["Jordan"],
            ["Heure standard de la Syrie"] = ["Syria"],
            ["Heure standard de l'Égypte"] = ["Egypt"],
            ["Heure standard de la Libye"] = ["Libya"],
            ["Heure standard du Soudan"] = ["Sudan"],
            ["Heure standard du Maroc"] = ["Morocco"],
            ["Heure standard de la Namibie"] = ["Namibia"],
            ["Heure standard de l'Afrique du Sud"] = ["South Africa"],
            ["Heure standard du Zimbabwe"] = ["Zimbabwe"],
            ["Heure standard du Kenya"] = ["Kenya"],
            ["Heure standard de l'Éthiopie"] = ["Ethiopia"],
            ["Heure standard de l'Afrique de l'Est"] = ["Kenya", "Ethiopia",
                                                        "Tanzania"],
            ["Heure standard de l'Afrique centrale de l'Ouest"]
                                                    = ["Nigeria", "Cameroon",
                                                        "Gabon"],
            ["Heure standard de l'Afrique de l'Ouest"] = ["Senegal", "Mali",
                                                        "Ivory Coast"],
            ["Heure standard de Greenwich"] = ["Ghana", "Ivory Coast",
                                                        "Senegal", "Gambia"],
            ["Heure standard de Haïti"] = ["Haiti"],
            ["Heure standard du Maroc"] = ["Morocco"],
            ["Heure standard de Nouvelle-Zélande"] = ["New Zealand"],
            ["Heure standard d'Australie orientale"] = ["Australia"],
            ["Heure standard d'Australie centrale"] = ["Australia"],
            ["Heure standard d'Australie occidentale"] = ["Australia"],
            ["Heure standard de Fidji"] = ["Fiji"],
            ["Heure standard de São Tomé"] = ["Sao Tome and Principe"],
            ["Heure standard de Russie"] = ["Russia"],
            ["Heure standard de Biélorussie"] = ["Belarus"],
            ["Heure standard d'Ukraine"] = ["Ukraine"],
            ["Heure standard du Kazakhstan"] = ["Kazakhstan"],
            ["Heure standard d'Ouzbékistan"] = ["Uzbekistan"],
            ["Heure standard de Romance"] = ["France", "Belgium",
                                                        "Spain"],
            ["Heure standard de l'Europe centrale"] = ["Germany", "Austria",
                                                        "Switzerland"],
            ["Heure standard de l'Europe de l'Est"] = ["Greece", "Romania",
                                                        "Bulgaria"],
            ["Heure standard du Pacifique"] = ["United States", "Canada"],
            ["Heure standard de la montagne"] = ["United States", "Canada"],
            ["Heure standard du Centre"] = ["United States", "Canada"],
            ["Heure standard de l'Est"] = ["United States", "Canada"],
            ["Heure standard de l'Atlantique"] = ["Canada"],
            ["Heure standard de Terre-Neuve"] = ["Canada"],
            ["Heure standard du Groenland"] = ["Greenland"],
            ["Heure standard du Mexique"] = ["Mexico"],
            ["Heure standard de l'Amérique centrale"] = ["Guatemala", "Honduras",
                                                        "El Salvador",
                                                        "Nicaragua",
                                                        "Costa Rica"],
            ["Heure standard de Cuba"] = ["Cuba"],
            ["Heure standard de Colombie"] = ["Colombia"],
            ["Heure standard du Pérou"] = ["Peru"],
            ["Heure standard de l'Équateur"] = ["Ecuador"],
            ["Heure standard de Bolivie"] = ["Bolivia"],
            ["Heure standard du Venezuela"] = ["Venezuela"],
            ["Heure standard du Chili"] = ["Chile"],
            ["Heure standard du Paraguay"] = ["Paraguay"],
            ["Heure standard de l'Uruguay"] = ["Uruguay"],
            ["Heure standard de l'Argentine"] = ["Argentina"],
            ["Heure standard de Brasilia"] = ["Brazil"],
            ["Heure standard du Brésil oriental"] = ["Brazil"],
            ["Heure standard du Brésil central"] = ["Brazil"],
            ["Temps universel coordonné"] = ["Unknown"],
            ["Temps universel coordinné"] = ["Mali", "Senegal",
                                                        "Burkina Faso",
                                                        "Guinea", "Togo"],
            ["GTB"] = ["Greece", "Romania"],

            // ── Portuguese (pt-*) ──────────────────────────────────────────────
            ["Hora oficial do Brasil"] = ["Brazil"],
            ["Hora Padrão de Brasília"] = ["Brazil"],
            ["Hora Padrão de Brasília Central"] = ["Brazil"],
            ["Hora de Verão de Brasília"] = ["Brazil"],
            ["Hora oficial do Brasil Central"] = ["Brazil"],
            ["Hora Padrão da Índia"] = ["India"],
            ["Hora Padrão do Paquistão"] = ["Pakistan"],
            ["Hora Padrão de Lisboa"] = ["Portugal"],
            ["Hora Padrão de Cabo Verde"] = ["Cape Verde"],
            ["Hora Padrão dos Açores"] = ["Portugal"],

            // ── Turkish (tr-*) ─────────────────────────────────────────────────
            ["Türkiye Standart Saati"] = ["Turkey"],
            ["Türkiye Yaz Saati"] = ["Turkey"],
            ["Azerbaycan Standart Saati"] = ["Azerbaijan"],
            ["Azerbaycan Yaz Saati"] = ["Azerbaijan"],
            ["Gürcistan Standart Saati"] = ["Georgia"],
            ["Arabistan Standart Saati"] = ["Saudi Arabia", "Yemen"],
            ["Arap Standart Saati"] = ["Iraq"],
            ["İran Standart Saati"] = ["Iran"],
            ["Pakistan Standart Saati"] = ["Pakistan"],
            ["Hindistan Standart Saati"] = ["India"],
            ["Bangladeş Standart Saati"] = ["Bangladesh"],
            ["Nepal Standart Saati"] = ["Nepal"],
            ["Afganistan Standart Saati"] = ["Afghanistan"],
            ["Azerbaijani Standart Saati"] = ["Azerbaijan"],
            ["Orta Avrupa Standart Saati"] = ["Germany", "Austria",
                                                        "Hungary",
                                                        "Czech Republic"],
            ["Doğu Avrupa Standart Saati"] = ["Greece", "Romania",
                                                        "Bulgaria"],
            ["Batı Avrupa Standart Saati"] = ["Germany", "France",
                                                        "Netherlands"],
            ["Mısır Standart Saati"] = ["Egypt"],
            ["Fas Standart Saati"] = ["Morocco"],
            ["Güney Afrika Standart Saati"] = ["South Africa"],
            ["Doğu Afrika Standart Saati"] = ["Kenya", "Ethiopia"],
            ["Çin Standart Saati"] = ["China"],
            ["Japonya Standart Saati"] = ["Japan"],
            ["Kore Standart Saati"] = ["South Korea"],
            ["Singapur Standart Saati"] = ["Singapore", "Malaysia"],
            ["Avustralya Doğu Standart Saati"] = ["Australia"],
            ["Yeni Zelanda Standart Saati"] = ["New Zealand"],
            ["ABD Doğu Standart Saati"] = ["United States"],
            ["ABD Merkezi Standart Saati"] = ["United States"],
            ["ABD Dağ Standart Saati"] = ["United States"],
            ["ABD Pasifik Standart Saati"] = ["United States"],
            ["Meksika Standart Saati"] = ["Mexico"],
            ["Brezilya Doğu Standart Saati"] = ["Brazil"],
            ["Arjantin Standart Saati"] = ["Argentina"],

            // ── German (de-*) ──────────────────────────────────────────────────
            ["Mitteleuropäische Zeit"] = ["Germany", "Austria",
                                                        "Switzerland",
                                                        "Netherlands",
                                                        "Belgium"],
            ["Mitteleuropäische Sommerzeit"] = ["Germany", "Austria",
                                                        "Switzerland"],
            ["Westeuropäische Zeit"] = ["United Kingdom",
                                                        "Ireland", "Portugal"],
            ["Osteuropäische Zeit"] = ["Greece", "Romania",
                                                        "Bulgaria", "Finland"],
            ["Türkische Normalzeit"] = ["Turkey"],
            ["Russische Normalzeit"] = ["Russia"],
            ["Indische Normalzeit"] = ["India"],
            ["Pakistanische Normalzeit"] = ["Pakistan"],
            ["Arabische Normalzeit"] = ["Saudi Arabia", "Yemen"],
            ["Ägyptische Normalzeit"] = ["Egypt"],
            ["Südafrikanische Normalzeit"] = ["South Africa"],
            ["Ostafrika-Normalzeit"] = ["Kenya", "Ethiopia"],
            ["Westafrikanische Normalzeit"] = ["Nigeria", "Cameroon"],
            ["Zentralafrikanische Normalzeit"] = ["Nigeria", "Cameroon"],
            ["Chinesische Normalzeit"] = ["China"],
            ["Japanische Normalzeit"] = ["Japan"],
            ["Koreanische Normalzeit"] = ["South Korea"],
            ["Australische Ostnormalzeit"] = ["Australia"],
            ["Neuseeländische Normalzeit"] = ["New Zealand"],
            ["Ostamerikanische Normalzeit"] = ["United States", "Canada"],
            ["Zentralamerikanische Normalzeit"] = ["United States", "Canada"],
            ["Brasilianische Normalzeit"] = ["Brazil"],
            ["Argentinische Normalzeit"] = ["Argentina"],

            // ── Arabic (ar-*) ──────────────────────────────────────────────────
            ["غرب أفريقيا الوسطى - توقيت رسمي"] = ["Nigeria", "Cameroon",
                                                        "Algeria", "Niger",
                                                        "Chad"],
            ["مصر - التوقيت الرسمي"] = ["Egypt"],
            ["السعودية - التوقيت الرسمي"] = ["Saudi Arabia"],
            ["الجزائر - التوقيت الرسمي"] = ["Algeria"],
            ["المغرب - التوقيت الرسمي"] = ["Morocco"],
            ["العراق - التوقيت الرسمي"] = ["Iraq"],
            ["الأردن - التوقيت الرسمي"] = ["Jordan"],
            ["سوريا - التوقيت الرسمي"] = ["Syria"],
            ["إسرائيل - التوقيت الرسمي"] = ["Israel"],
            ["الإمارات - التوقيت الرسمي"] = ["United Arab Emirates"],
            ["الكويت - التوقيت الرسمي"] = ["Kuwait"],
            ["قطر - التوقيت الرسمي"] = ["Qatar"],
            ["البحرين - التوقيت الرسمي"] = ["Bahrain"],
            ["اليمن - التوقيت الرسمي"] = ["Yemen"],
            ["السودان - التوقيت الرسمي"] = ["Sudan"],
            ["ليبيا - التوقيت الرسمي"] = ["Libya"],
            ["إيران - التوقيت الرسمي"] = ["Iran"],
            ["أفغانستان - التوقيت الرسمي"] = ["Afghanistan"],
            ["باكستان - التوقيت الرسمي"] = ["Pakistan"],

            // ── Russian (ru-*) ─────────────────────────────────────────────────
            ["Западная Азия (зима)"] = ["Uzbekistan",
                                                        "Kazakhstan",
                                                        "Tajikistan"],
            ["Западная Европа (зима)"] = ["United Kingdom",
                                                        "Portugal", "Ireland"],
            ["Москва, стандартное время"] = ["Russia"],
            ["Екатеринбург, стандартное время"] = ["Russia"],
            ["Омск, стандартное время"] = ["Russia"],
            ["Красноярск, стандартное время"] = ["Russia"],
            ["Новосибирск, стандартное время"] = ["Russia"],
            ["Якутск, стандартное время"] = ["Russia"],
            ["Владивосток, стандартное время"] = ["Russia"],
            ["Магадан, стандартное время"] = ["Russia"],
            ["Камчатка, стандартное время"] = ["Russia"],
            ["Калининград, стандартное время"] = ["Russia"],
            ["Беларусь, стандартное время"] = ["Belarus"],
            ["Украина, стандартное время"] = ["Ukraine"],
            ["Грузия, стандартное время"] = ["Georgia"],
            ["Азербайджан, стандартное время"] = ["Azerbaijan"],
            ["Армения, стандартное время"] = ["Armenia"],
            ["Казахстан, стандартное время"] = ["Kazakhstan"],
            ["Узбекистан, стандартное время"] = ["Uzbekistan"],
            ["Туркменистан, стандартное время"] = ["Turkmenistan"],
            ["Таджикистан, стандартное время"] = ["Tajikistan"],
            ["Монголия, стандартное время"] = ["Mongolia"],
            ["Китай, стандартное время"] = ["China"],
            ["Корея, стандартное время"] = ["South Korea"],
            ["Япония, стандартное время"] = ["Japan"],
            ["Индия, стандартное время"] = ["India"],
            ["Пакистан, стандартное время"] = ["Pakistan"],
            ["Афганистан, стандартное время"] = ["Afghanistan"],
            ["Бангладеш, стандартное время"] = ["Bangladesh"],
            ["Непал, стандартное время"] = ["Nepal"],
            ["Шри-Ланка, стандартное время"] = ["Sri Lanka"],
            ["Иран, стандартное время"] = ["Iran"],
            ["Турция, стандартное время"] = ["Turkey"],
            ["Египет, стандартное время"] = ["Egypt"],
            ["Израиль, стандартное время"] = ["Israel"],
            ["Саудовская Аравия, стандартное время"] = ["Saudi Arabia"],
            ["Южная Африка, стандартное время"] = ["South Africa"],
            ["Восточная Африка (зима)"] = ["Kenya", "Ethiopia",
                                                        "Tanzania"],
            ["Западная Азия (зима)"] = ["Uzbekistan",
                                                        "Kazakhstan"],

            // ── Hungarian (hu-*) ───────────────────────────────────────────────
            ["Közép-európai téli idő"] = ["Hungary",
                                                        "Czech Republic",
                                                        "Slovakia", "Poland"],
            ["Kelet-európai téli idő"] = ["Romania", "Bulgaria",
                                                        "Greece"],
            ["Közepp-európai téli idő"] = ["Hungary"],
            ["Nyugat-európai téli idő"] = ["United Kingdom",
                                                        "Portugal", "Ireland"],
            ["Török téli idő"] = ["Turkey"],
            ["Orosz téli idő"] = ["Russia"],
            ["Indiai téli idő"] = ["India"],
            ["Pakisztáni téli idő"] = ["Pakistan"],

            // ── Czech (cs-*) ───────────────────────────────────────────────────
            ["Střední Evropa (běžný čas)"] = ["Czech Republic",
                                                        "Slovakia", "Poland",
                                                        "Hungary"],
            ["Střední Evropa (letní čas)"] = ["Czech Republic",
                                                        "Slovakia"],
            ["Východní Evropa (běžný čas)"] = ["Romania", "Bulgaria",
                                                        "Greece"],
            ["Turecko (běžný čas)"] = ["Turkey"],
            ["Rusko (běžný čas)"] = ["Russia"],

            // ── Italian (it-*) ─────────────────────────────────────────────────
            ["ora solare Europa occidentale"] = ["Italy", "France",
                                                        "Spain", "Germany"],
            ["ora solare Europa centrale"] = ["Italy", "Germany",
                                                        "Austria",
                                                        "Switzerland"],
            ["ora solare Europa orientale"] = ["Greece", "Romania",
                                                        "Bulgaria"],
            ["ora solare Turchia"] = ["Turkey"],
            ["ora solare Russia"] = ["Russia"],
            ["ora solare India"] = ["India"],
            ["ora solare Pakistan"] = ["Pakistan"],
            ["ora solare Africa orientale"] = ["Kenya", "Ethiopia"],
            ["ora solare Africa del Sud"] = ["South Africa"],
            ["ora solare Africa occidentale"] = ["Nigeria", "Senegal"],

            // ── Dutch (nl-*) ───────────────────────────────────────────────────
            ["Midden-Europese standaardtijd"] = ["Netherlands", "Belgium",
                                                        "Germany", "Austria"],
            ["West-Europese standaardtijd"] = ["United Kingdom",
                                                        "Portugal", "Ireland"],
            ["Oost-Europese standaardtijd"] = ["Greece", "Romania",
                                                        "Bulgaria"],
            ["Turkse standaardtijd"] = ["Turkey"],
            ["Russische standaardtijd"] = ["Russia"],
            ["Indiase standaardtijd"] = ["India"],
            ["Pakistaanse standaardtijd"] = ["Pakistan"],
            ["Zuid-Afrikaanse standaardtijd"] = ["South Africa"],
            ["Oost-Afrikaanse standaardtijd"] = ["Kenya", "Ethiopia"],

            // ── Uzbek (uz-*) ───────────────────────────────────────────────────
            ["Западная Азия (зима)"] = ["Uzbekistan",
                                                        "Kazakhstan"],

            // ── Azerbaijani (az-*) ─────────────────────────────────────────────
            ["Azərbaycan Standart Vaxtı"] = ["Azerbaijan"],
            ["Azərbaycan Yay Vaxtı"] = ["Azerbaijan"],
            ["Gürcüstan Standart Vaxtı"] = ["Georgia"],
            ["Türkiyə Standart Vaxtı"] = ["Turkey"],
            ["İran Standart Vaxtı"] = ["Iran"],
            ["Ərəbistan Standart Vaxtı"] = ["Saudi Arabia", "Yemen"],
            ["Hindistan Standart Vaxtı"] = ["India"],
            ["Pakistanın Standart Vaxtı"] = ["Pakistan"],

            // ── Farsi / Persian (fa-*) ─────────────────────────────────────────
            ["وقت استاندارد ایران"] = ["Iran"],
            ["وقت استاندارد افغانستان"] = ["Afghanistan"],
            ["وقت استاندارد پاکستان"] = ["Pakistan"],
            ["وقت استاندارد هند"] = ["India"],
            ["وقت استاندارد عربستان"] = ["Saudi Arabia"],
            ["وقت استاندارد ترکیه"] = ["Turkey"],

            // ── Urdu (ur-*) ────────────────────────────────────────────────────
            ["پاکستان معیاری وقت"] = ["Pakistan"],
            ["ہندوستان معیاری وقت"] = ["India"],
            ["افغانستان معیاری وقت"] = ["Afghanistan"],

            // ── Bengali (bn-*) ─────────────────────────────────────────────────
            ["বাংলাদেশ প্রমাণ সময়"] = ["Bangladesh"],
            ["ভারত প্রমাণ সময়"] = ["India"],

            // ── Hindi (hi-*) ───────────────────────────────────────────────────
            ["भारत मानक समय"] = ["India"],
            ["पाकिस्तान मानक समय"] = ["Pakistan"],
            ["बांग्लादेश मानक समय"] = ["Bangladesh"],

            // ── Nepali (ne-*) ──────────────────────────────────────────────────
            ["नेपाल मानक समय"] = ["Nepal"],
            ["भारत मानक समय"] = ["India"],

            // ── Sinhala (si-*) ─────────────────────────────────────────────────
            ["ශ්‍රී ලංකා ප්‍රමාණ වේලාව"] = ["Sri Lanka"],

            // ── Chinese (zh-*) ─────────────────────────────────────────────────
            ["中国标准时间"] = ["China"],
            ["台北标准时间"] = ["Taiwan"],
            ["香港标准时间"] = ["Hong Kong"],
            ["新加坡标准时间"] = ["Singapore"],
            ["东京 (标准时间)"] = ["Japan"],
            ["韩国标准时间"] = ["South Korea"],

            // ── Japanese (ja-*) ────────────────────────────────────────────────
            ["東京 (標準時)"] = ["Japan"],
            ["大韓民国標準時"] = ["South Korea"],
            ["中国標準時"] = ["China"],

            // ── Korean (ko-*) ──────────────────────────────────────────────────
            ["대한민국 표준시"] = ["South Korea"],
            ["일본 표준시"] = ["Japan"],
            ["중국 표준시"] = ["China"],

            // ── Indonesian / Malay (id/ms-*) ───────────────────────────────────
            ["Waktu Standar Asia Tenggara"] = ["Indonesia", "Thailand",
                                                        "Vietnam"],
            ["Waktu Standar Singapura"] = ["Singapore", "Malaysia"],
            ["Waktu Standar Indonesia Barat"] = ["Indonesia"],
            ["Waktu Standar Indonesia Tengah"] = ["Indonesia"],
            ["Waktu Standar Indonesia Timur"] = ["Indonesia"],
            ["Waktu Standar China"] = ["China"],
            ["Waktu Standar Jepang"] = ["Japan"],
            ["Waktu Standar Korea"] = ["South Korea"],
            ["Waktu Standar India"] = ["India"],
            ["Waktu Standar Pakistan"] = ["Pakistan"],
            ["Waktu Standar Australia Timur"] = ["Australia"],
            ["Waktu Standar Semenanjung Melayu"] = ["Malaysia"],

            // ── Swahili (sw-*) ─────────────────────────────────────────────────
            ["Saa za Afrika Mashariki"] = ["Kenya", "Tanzania",
                                                        "Uganda"],
            ["Saa za Afrika Kusini"] = ["South Africa",
                                                        "Zimbabwe"],
            ["Saa za Afrika Magharibi ya Kati"] = ["Nigeria", "Cameroon"],

            // ── Amharic (am-*) ─────────────────────────────────────────────────
            ["የምስራቅ አፍሪካ ሰዓት"] = ["Ethiopia", "Kenya",
                                                        "Tanzania"],

            // ── Vietnamese (vi-*) ──────────────────────────────────────────────
            ["Giờ chuẩn Đông Nam Á"] = ["Vietnam", "Thailand",
                                                        "Indonesia"],
            ["Giờ chuẩn Trung Quốc"] = ["China"],
            ["Giờ chuẩn Nhật Bản"] = ["Japan"],
            ["Giờ chuẩn Ấn Độ"] = ["India"],

            // ── Thai (th-*) ────────────────────────────────────────────────────
            ["เวลามาตรฐานเอเชียตะวันออกเฉียงใต้"] = ["Thailand", "Vietnam",
                                                        "Indonesia"],
            ["เวลามาตรฐานอินเดีย"] = ["India"],
            ["เวลามาตรฐานจีน"] = ["China"],
            ["เวลามาตรฐานญี่ปุ่น"] = ["Japan"],
        };

    #endregion

    #region ── Neutral Language → Country Map ────────────────────────────────
    //
    // Maps ISO 639-1 neutral language codes to their most common countries.
    // Used when the language tag has no region subtag (e.g. "fr" not "fr-CM").
    //
    private static readonly Dictionary<string, string[]> _neutralLangMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Major world languages
            ["ar"] = ["Saudi Arabia", "Egypt", "Algeria", "Morocco",
                   "Iraq", "Sudan", "Yemen", "Syria", "Tunisia", "Libya", "Jordan", "Kuwait", "UAE", "Qatar",
                   "Bahrain", "Oman", "Lebanon", "Palestine"],
            ["fr"] = ["France", "Belgium", "Switzerland", "Canada",
                   "Senegal", "Ivory Coast", "Cameroon", "Mali",
                   "Burkina Faso", "Niger", "Guinea", "Togo",
                   "Benin", "Congo", "Gabon", "Madagascar",
                   "Rwanda", "Burundi", "Haiti", "Morocco",
                   "Algeria", "Tunisia"],
            ["es"] = ["Spain", "Mexico", "Colombia", "Argentina",
                   "Peru", "Venezuela", "Chile", "Ecuador",
                   "Guatemala", "Cuba", "Bolivia", "Dominican Republic",
                   "Honduras", "Paraguay", "El Salvador", "Nicaragua",
                   "Costa Rica", "Panama", "Uruguay"],
            ["pt"] = ["Brazil", "Portugal", "Angola", "Mozambique",
                   "Cape Verde", "Guinea-Bissau", "Sao Tome and Principe",
                   "Timor-Leste"],
            ["de"] = ["Germany", "Austria", "Switzerland",
                   "Luxembourg", "Liechtenstein"],
            ["zh"] = ["China", "Taiwan", "Singapore", "Hong Kong"],
            ["ru"] = ["Russia", "Belarus", "Kazakhstan",
                   "Kyrgyzstan", "Tajikistan"],
            ["tr"] = ["Turkey", "Azerbaijan", "Cyprus"],
            ["fa"] = ["Iran", "Afghanistan", "Tajikistan"],
            ["ur"] = ["Pakistan", "India"],
            ["hi"] = ["India"],
            ["bn"] = ["Bangladesh", "India"],
            ["id"] = ["Indonesia"],
            ["ms"] = ["Malaysia", "Brunei", "Singapore"],
            ["sw"] = ["Kenya", "Tanzania", "Uganda",
                   "Rwanda", "Burundi", "Congo"],
            ["ha"] = ["Nigeria", "Niger", "Chad",
                   "Ghana", "Cameroon"],
            ["yo"] = ["Nigeria", "Benin"],
            ["ig"] = ["Nigeria"],
            ["am"] = ["Ethiopia"],
            ["so"] = ["Somalia", "Ethiopia", "Kenya", "Djibouti"],
            ["rw"] = ["Rwanda"],
            ["mg"] = ["Madagascar"],
            ["ny"] = ["Malawi", "Zambia", "Zimbabwe"],
            ["sn"] = ["Zimbabwe", "Zambia"],
            ["zu"] = ["South Africa"],
            ["xh"] = ["South Africa"],
            ["af"] = ["South Africa", "Namibia"],
            ["st"] = ["South Africa", "Lesotho"],
            ["tn"] = ["South Africa", "Botswana"],
            ["ts"] = ["South Africa", "Mozambique"],
            ["ss"] = ["South Africa", "Eswatini"],
            ["ve"] = ["South Africa"],
            ["nr"] = ["South Africa"],
            ["nso"] = ["South Africa"],

            // Europe
            ["hu"] = ["Hungary"],
            ["cs"] = ["Czech Republic"],
            ["sk"] = ["Slovakia"],
            ["pl"] = ["Poland"],
            ["uk"] = ["Ukraine"],
            ["ro"] = ["Romania", "Moldova"],
            ["bg"] = ["Bulgaria"],
            ["sr"] = ["Serbia", "Bosnia and Herzegovina",
                   "Montenegro", "Croatia"],
            ["hr"] = ["Croatia", "Bosnia and Herzegovina"],
            ["bs"] = ["Bosnia and Herzegovina"],
            ["sq"] = ["Albania", "Kosovo", "North Macedonia"],
            ["mk"] = ["North Macedonia"],
            ["sl"] = ["Slovenia"],
            ["lt"] = ["Lithuania"],
            ["lv"] = ["Latvia"],
            ["et"] = ["Estonia"],
            ["fi"] = ["Finland"],
            ["sv"] = ["Sweden", "Finland"],
            ["no"] = ["Norway"],
            ["nb"] = ["Norway"],
            ["nn"] = ["Norway"],
            ["da"] = ["Denmark"],
            ["nl"] = ["Netherlands", "Belgium"],
            ["el"] = ["Greece", "Cyprus"],
            ["he"] = ["Israel"],
            ["mt"] = ["Malta"],
            ["ga"] = ["Ireland"],
            ["cy"] = ["United Kingdom"],
            ["gd"] = ["United Kingdom"],
            ["eu"] = ["Spain"],
            ["ca"] = ["Spain", "Andorra"],
            ["gl"] = ["Spain"],
            ["is"] = ["Iceland"],
            ["lb"] = ["Luxembourg"],
            ["rm"] = ["Switzerland"],
            ["fo"] = ["Faroe Islands"],

            // Caucasus / Central Asia
            ["ka"] = ["Georgia"],
            ["hy"] = ["Armenia"],
            ["az"] = ["Azerbaijan"],
            ["uz"] = ["Uzbekistan"],
            ["kk"] = ["Kazakhstan"],
            ["ky"] = ["Kyrgyzstan"],
            ["tg"] = ["Tajikistan"],
            ["tk"] = ["Turkmenistan"],
            ["mn"] = ["Mongolia"],

            // South / Southeast Asia
            ["my"] = ["Myanmar"],
            ["km"] = ["Cambodia"],
            ["lo"] = ["Laos"],
            ["th"] = ["Thailand"],
            ["vi"] = ["Vietnam"],
            ["si"] = ["Sri Lanka"],
            ["ne"] = ["Nepal", "India"],
            ["ps"] = ["Afghanistan", "Pakistan"],
            ["tl"] = ["Philippines"],
            ["fil"] = ["Philippines"],
            ["ceb"] = ["Philippines"],
            ["jv"] = ["Indonesia"],
            ["su"] = ["Indonesia"],
            ["mg"] = ["Madagascar"],
            ["dz"] = ["Bhutan"],
            ["bo"] = ["China"],       // Tibetan
            ["ug"] = ["China"],       // Uyghur
            ["ii"] = ["China"],       // Yi
            ["za"] = ["China"],       // Zhuang

            // East Asia
            ["ko"] = ["South Korea", "North Korea"],
            ["ja"] = ["Japan"],
            ["zh"] = ["China", "Taiwan", "Singapore", "Hong Kong"],

            // Middle East
            ["ku"] = ["Iraq", "Turkey", "Iran", "Syria"],
            ["ckb"] = ["Iraq", "Iran"],   // Central Kurdish
            ["syr"] = ["Syria", "Iraq"],  // Syriac
            ["arc"] = ["Iraq", "Syria"],  // Aramaic
            ["he"] = ["Israel"],

            // Pacific / Oceania
            ["mi"] = ["New Zealand"],
            ["sm"] = ["Samoa"],
            ["to"] = ["Tonga"],
            ["fj"] = ["Fiji"],
            ["haw"] = ["United States"],  // Hawaiian

            // Americas
            ["qu"] = ["Peru", "Bolivia", "Ecuador"],
            ["ay"] = ["Bolivia", "Peru"],
            ["gn"] = ["Paraguay"],
            ["ht"] = ["Haiti"],          // Haitian Creole

            // Other
            ["it"] = ["Italy", "Switzerland", "San Marino"],
            ["nl"] = ["Netherlands", "Belgium", "Suriname"],
            ["en"] = ["United States", "United Kingdom", "Canada",
                   "Australia", "New Zealand", "Ireland",
                   "South Africa", "India", "Nigeria",
                   "Philippines", "Pakistan", "Ghana",
                   "Kenya", "Tanzania", "Uganda", "Zimbabwe",
                   "Zambia", "Botswana", "Namibia", "Rwanda",
                   "Ethiopia", "Cameroon", "Sierra Leone",
                   "Liberia", "Gambia", "Malawi"],
        };

    #endregion
}

