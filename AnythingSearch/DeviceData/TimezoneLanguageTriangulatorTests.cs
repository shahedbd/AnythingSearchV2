#if DEBUG
using System.Diagnostics;
using AnythingSearch.Helper;

public static class TimezoneLanguageTriangulatorTests
{
    /// <summary>
    /// Run all tests — call from app startup in DEBUG mode.
    /// </summary>
    public static void RunAll()
    {
        int passed = 0, failed = 0;

        void Test(string label,
                  string tzId, string app, string sys,
                  string expected)
        {
            string result = TimezoneLanguageTriangulator
                .GetCountry(tzId, app, sys);

            bool ok = result.Equals(expected,
                          StringComparison.OrdinalIgnoreCase);

            string icon = ok ? "✅" : "❌";
            Logger.Log(
                $"{icon} [{label}] " +
                $"TZ='{tzId}' App='{app}' Sys='{sys}' " +
                $"→ '{result}' (expected '{expected}')");

            if (ok) passed++; else failed++;
        }

        Logger.Log("═══ TimezoneLanguageTriangulator Tests ═══");

        // ── Single-country timezones (Layer: TZ alone) ────────────────────
        Test("PK exact TZ",
             "Pakistan Standard Time",       "en-US", "en-US",
             "Pakistan");

        Test("NP exact TZ",
             "Nepal Standard Time",          "en-US", "en-US",
             "Nepal");

        Test("AF exact TZ",
             "Afghanistan Standard Time",    "en-US", "en-US",
             "Afghanistan");

        Test("JP exact TZ",
             "Tokyo Standard Time",          "en-US", "en-US",
             "Japan");

        Test("TR exact TZ",
             "Turkey Standard Time",         "en-US", "en-US",
             "Turkey");

        Test("EG exact TZ",
             "Egypt Standard Time",          "en-US", "en-US",
             "Egypt");

        Test("MA exact TZ",
             "Morocco Standard Time",        "en-US", "en-US",
             "Morocco");

        Test("IR exact TZ",
             "Iran Standard Time",           "en-US", "en-US",
             "Iran");

        Test("BD exact TZ",
             "Bangladesh Standard Time",     "en-US", "en-US",
             "Bangladesh");

        Test("IN exact TZ",
             "India Standard Time",          "en-US", "en-US",
             "India");

        // ── TZ + System language disambiguates ────────────────────────────
        Test("CM: W.Africa TZ + fr-CM sys",
             "W. Central Africa Standard Time", "fr-FR", "fr-CM",
             "Cameroon");

        Test("NG: W.Africa TZ + en-NG sys",
             "W. Central Africa Standard Time", "en-US", "en-NG",
             "Nigeria");

        Test("DZ: W.Africa TZ + ar-DZ sys",
             "W. Central Africa Standard Time", "fr-FR", "ar-DZ",
             "Algeria");

        Test("KE: E.Africa TZ + sw-KE sys",
             "E. Africa Standard Time",      "en-US", "sw-KE",
             "Kenya");

        Test("ET: E.Africa TZ + am-ET sys",
             "E. Africa Standard Time",      "en-US", "am-ET",
             "Ethiopia");

        Test("ZW: S.Africa TZ + sn-ZW sys",
             "South Africa Standard Time",   "en-US", "sn-ZW",
             "Zimbabwe");

        Test("ZA: S.Africa TZ + zu-ZA sys",
             "South Africa Standard Time",   "en-US", "zu-ZA",
             "South Africa");

        Test("FR: Romance TZ + fr-FR sys",
             "Romance Standard Time",        "fr-FR", "fr-FR",
             "France");

        Test("BE: Romance TZ + fr-BE sys",
             "Romance Standard Time",        "fr-FR", "fr-BE",
             "Belgium");

        Test("US: Eastern TZ + en-US sys",
             "Eastern Standard Time",        "en-US", "en-US",
             "United States");

        Test("CA: Eastern TZ + en-CA sys",
             "Eastern Standard Time",        "en-US", "en-CA",
             "Canada");

        Test("MX: Central TZ (Mexico) + es-MX sys",
             "Central Standard Time (Mexico)", "es-MX", "es-MX",
             "Mexico");

        Test("CO: SA Pacific TZ + es-CO sys",
             "SA Pacific Standard Time",     "es-MX", "es-CO",
             "Colombia");

        Test("PE: SA Pacific TZ + es-PE sys",
             "SA Pacific Standard Time",     "es-MX", "es-PE",
             "Peru");

        Test("EC: SA Pacific TZ + es-EC sys",
             "SA Pacific Standard Time",     "es-MX", "es-EC",
             "Ecuador");

        Test("BR: E.SouthAmerica TZ + pt-BR sys",
             "E. South America Standard Time", "pt-BR", "pt-BR",
             "Brazil");

        Test("AR: Argentina TZ + es-AR sys",
             "Argentina Standard Time",      "es-AR", "es-AR",
             "Argentina");

        Test("HT: Haiti TZ + fr-HT sys",
             "Haiti Standard Time",          "es-MX", "fr-HT",
             "Haiti");

        Test("GT: CentralAmerica TZ + es-GT sys",
             "Central America Standard Time", "es-MX", "es-GT",
             "Guatemala");

        Test("HN: CentralAmerica TZ + es-HN sys",
             "Central America Standard Time", "es-MX", "es-HN",
             "Honduras");

        Test("SV: CentralAmerica TZ + es-SV sys",
             "Central America Standard Time", "es-MX", "es-SV",
             "El Salvador");

        Test("DO: SA Eastern TZ + es-DO sys",
             "SA Eastern Standard Time",     "es-MX", "es-DO",
             "Dominican Republic");

        // ── Localized timezone display names (from CSV) ───────────────────
        Test("TR localized Turkish TZ",
             "Türkiye Standart Saati",        "tr-TR", "tr-TR",
             "Turkey");

        Test("AZ localized Azerbaijani TZ",
             "Azerbaijani Standart Saati",    "az-AZ", "az-AZ",
             "Azerbaijan");

        Test("HT localized Spanish TZ",
             "Horario estándar de Haití",     "es-MX", "es-HN",
             "Haiti");

        Test("MA localized French TZ",
             "Heure standard du Maroc",       "fr-FR", "fr-MA",
             "Morocco");

        Test("EG Arabic TZ",
             "مصر - التوقيت الرسمي",          "ar-SA", "ar-EG",
             "Egypt");

        Test("DZ Arabic TZ",
             "غرب أفريقيا الوسطى - توقيت رسمي", "fr-FR", "ar-DZ",
             "Algeria");

        Test("SA Arabic TZ",
             "السعودية - التوقيت الرسمي",     "ar-SA", "ar-SA",
             "Saudi Arabia");

        Test("HU Hungarian TZ",
             "Közép-európai téli idő",        "hu-HU", "hu-HU",
             "Hungary");

        Test("CZ Czech TZ",
             "Střední Evropa (běžný čas)",    "cs-CZ", "cs-CZ",
             "Czech Republic");

        Test("IT Italian TZ",
             "ora solare Europa occidentale", "it-IT", "it-IT",
             "Italy");

        Test("BR Portuguese TZ",
             "Hora oficial do Brasil",        "pt-BR", "pt-BR",
             "Brazil");

        Test("ML French UTC TZ + fr-ML sys",
             "Temps universel coordinné",     "fr-FR", "fr-ML",
             "Mali");

        Test("SN French UTC TZ + fr-SN sys",
             "Temps universel coordinné",     "fr-FR", "fr-SN",
             "Senegal");

        Test("RU Russian TZ",
             "Западная Азия (зима)",          "ru-RU", "uz-UZ",
             "Uzbekistan");

        Test("UZ Russian TZ + uz-UZ sys",
             "Западная Азия (зима)",          "en-US", "uz-UZ",
             "Uzbekistan");

        Test("KZ Russian TZ + kk-KZ sys",
             "Западная Азия (зима)",          "en-US", "kk-KZ",
             "Kazakhstan");

        Test("DE German TZ",
             "Mitteleuropäische Zeit",        "de-DE", "de-DE",
             "Germany");

        Test("AT German TZ + de-AT sys",
             "Mitteleuropäische Zeit",        "de-DE", "de-AT",
             "Austria");

        Test("CH German TZ + de-CH sys",
             "Mitteleuropäische Zeit",        "de-DE", "de-CH",
             "Switzerland");

        Test("PK Urdu TZ name",
             "پاکستان معیاری وقت",            "ur-PK", "ur-PK",
             "Pakistan");

        Test("IN Hindi TZ name",
             "भारत मानक समय",                 "hi-IN", "hi-IN",
             "India");

        Test("BD Bengali TZ name",
             "বাংলাদেশ প্রমাণ সময়",           "bn-BD", "bn-BD",
             "Bangladesh");

        Test("CN Chinese TZ name",
             "中国标准时间",                    "zh-CN", "zh-CN",
             "China");

        Test("JP Japanese TZ name",
             "東京 (標準時)",                   "ja-JP", "ja-JP",
             "Japan");

        Test("KR Korean TZ name",
             "대한민국 표준시",                  "ko-KR", "ko-KR",
             "South Korea");

        Test("ID Indonesian TZ name",
             "Waktu Standar Asia Tenggara",   "id-ID", "id-ID",
             "Indonesia");

        Test("MY Malay TZ name",
             "Waktu Standar Semenanjung Melayu", "ms-MY", "ms-MY",
             "Malaysia");

        // ── Language tag only (no TZ match) ──────────────────────────────
        Test("PH: no TZ, fil-PH lang",
             "SE Asia Standard Time",        "en-US", "fil-PH",
             "Philippines");

        Test("BO: SA Western TZ + es-BO sys",
             "SA Western Standard Time",     "es-MX", "es-BO",
             "Bolivia");

        Test("VE: SA Western TZ + es-VE sys",
             "SA Western Standard Time",     "es-MX", "es-VE",
             "Venezuela");

        Test("PY: SA Eastern TZ + es-PY sys",
             "SA Eastern Standard Time",     "es-MX", "es-PY",
             "Paraguay");

        Test("UY: Montevideo TZ + es-UY sys",
             "Montevideo Standard Time",     "es-MX", "es-UY",
             "Uruguay");

        Test("CL: Chile TZ + es-CL sys",
             "Chile Standard Time",          "es-MX", "es-CL",
             "Chile");

        Test("CR: CentralAmerica TZ + es-CR sys",
             "Central America Standard Time", "es-MX", "es-CR",
             "Costa Rica");

        Test("NI: CentralAmerica TZ + es-NI sys",
             "Central America Standard Time", "es-MX", "es-NI",
             "Nicaragua");

        Test("PA: SA Pacific TZ + es-PA sys",
             "SA Pacific Standard Time",     "es-MX", "es-PA",
             "Panama");

        Test("TG: W.Africa TZ + fr-TG sys",
             "W. Africa Standard Time",      "fr-FR", "fr-TG",
             "Togo");

        Test("BF: W.Africa TZ + fr-BF sys",
             "W. Africa Standard Time",      "fr-FR", "fr-BF",
             "Burkina Faso");

        Test("GN: W.Africa TZ + fr-GN sys",
             "W. Africa Standard Time",      "fr-FR", "fr-GN",
             "Guinea");

        Test("CI: W.Africa TZ + fr-CI sys",
             "W. Africa Standard Time",      "fr-FR", "fr-CI",
             "Ivory Coast");

        Test("ML: W.Africa TZ + fr-ML sys",
             "W. Africa Standard Time",      "fr-FR", "fr-ML",
             "Mali");

        Test("SN: Greenwich TZ + fr-SN sys",
             "Greenwich Standard Time",      "fr-FR", "fr-SN",
             "Senegal");

        Test("GH: Greenwich TZ + en-GH sys",
             "Greenwich Standard Time",      "en-US", "en-GH",
             "Ghana");

        Test("NG: W.CentralAfrica TZ + ha-NG sys",
             "W. Central Africa Standard Time", "en-US", "ha-NG",
             "Nigeria");

        Test("KE: E.Africa TZ + en-KE sys",
             "E. Africa Standard Time",      "en-US", "en-KE",
             "Kenya");

        Test("TZ: E.Africa TZ + sw-TZ sys",
             "E. Africa Standard Time",      "en-US", "sw-TZ",
             "Tanzania");

        Test("UG: E.Africa TZ + sw-UG sys",
             "E. Africa Standard Time",      "en-US", "sw-UG",
             "Uganda");

        Test("ZM: S.Africa TZ + en-ZM sys",
             "South Africa Standard Time",   "en-US", "en-ZM",
             "Zambia");

        Test("MW: S.Africa TZ + ny-MW sys",
             "South Africa Standard Time",   "en-US", "ny-MW",
             "Malawi");

        Test("MZ: S.Africa TZ + pt-MZ sys",
             "South Africa Standard Time",   "pt-BR", "pt-MZ",
             "Mozambique");

        Test("NA: Namibia TZ + af-NA sys",
             "Namibia Standard Time",        "en-US", "af-NA",
             "Namibia");

        Test("GE: Georgian TZ + ka-GE sys",
             "Georgian Standard Time",       "en-US", "ka-GE",
             "Georgia");

        Test("AM: Caucasus TZ + hy-AM sys",
             "Caucasus Standard Time",       "en-US", "hy-AM",
             "Armenia");

        Test("AZ: Azerbaijan TZ + az-AZ sys",
             "Azerbaijan Standard Time",     "en-US", "az-AZ",
             "Azerbaijan");

        Test("KZ: Central Asia TZ + kk-KZ sys",
             "Central Asia Standard Time",   "en-US", "kk-KZ",
             "Kazakhstan");

        Test("KG: Central Asia TZ + ky-KG sys",
             "Central Asia Standard Time",   "en-US", "ky-KG",
             "Kyrgyzstan");

        Test("TJ: West Asia TZ + tg-TJ sys",
             "West Asia Standard Time",      "en-US", "tg-TJ",
             "Tajikistan");

        Test("TM: West Asia TZ + tk-TM sys",
             "West Asia Standard Time",      "en-US", "tk-TM",
             "Turkmenistan");

        Test("MN: Ulaanbaatar TZ + mn-MN sys",
             "Ulaanbaatar Standard Time",    "en-US", "mn-MN",
             "Mongolia");

        Test("MM: Myanmar TZ + my-MM sys",
             "Myanmar Standard Time",        "en-US", "my-MM",
             "Myanmar");

        Test("KH: SE Asia TZ + km-KH sys",
             "SE Asia Standard Time",        "en-US", "km-KH",
             "Cambodia");

        Test("LA: SE Asia TZ + lo-LA sys",
             "SE Asia Standard Time",        "en-US", "lo-LA",
             "Laos");

        Test("VN: SE Asia TZ + vi-VN sys",
             "SE Asia Standard Time",        "en-US", "vi-VN",
             "Vietnam");

        Test("TH: SE Asia TZ + th-TH sys",
             "SE Asia Standard Time",        "en-US", "th-TH",
             "Thailand");

        Test("SG: Singapore TZ + ms-SG sys",
             "Singapore Standard Time",      "en-US", "ms-SG",
             "Singapore");

        Test("MY: Singapore TZ + ms-MY sys",
             "Singapore Standard Time",      "en-US", "ms-MY",
             "Malaysia");

        Test("NZ: New Zealand TZ + en-NZ sys",
             "New Zealand Standard Time",    "en-US", "en-NZ",
             "New Zealand");

        Test("AU: AUS Eastern TZ + en-AU sys",
             "AUS Eastern Standard Time",    "en-US", "en-AU",
             "Australia");

        Test("FJ: Fiji TZ + en-FJ sys",
             "Fiji Standard Time",           "en-US", "en-FJ",
             "Fiji");

        Test("IS: GMT TZ + is-IS sys",
             "GMT Standard Time",            "en-US", "is-IS",
             "Iceland");

        Test("IE: GMT TZ + ga-IE sys",
             "GMT Standard Time",            "en-US", "ga-IE",
             "Ireland");

        Test("GB: GMT TZ + en-GB sys",
             "GMT Standard Time",            "en-US", "en-GB",
             "United Kingdom");

        Test("PT: GMT TZ + pt-PT sys",
             "GMT Standard Time",            "en-US", "pt-PT",
             "Portugal");

        Test("PL: CentralEuropean TZ + pl-PL sys",
             "Central European Standard Time", "en-US", "pl-PL",
             "Poland");

        Test("RS: CentralEuropean TZ + sr-RS sys",
             "Central European Standard Time", "en-US", "sr-RS",
             "Serbia");

        Test("BA: CentralEuropean TZ + bs-BA sys",
             "Central European Standard Time", "en-US", "bs-BA",
             "Bosnia and Herzegovina");

        Test("AL: CentralEuropean TZ + sq-AL sys",
             "Central European Standard Time", "en-US", "sq-AL",
             "Albania");

        Test("MK: CentralEuropean TZ + mk-MK sys",
             "Central European Standard Time", "en-US", "mk-MK",
             "North Macedonia");

        Test("SI: CentralEurope TZ + sl-SI sys",
             "Central Europe Standard Time",  "en-US", "sl-SI",
             "Slovenia");

        Test("HR: CentralEurope TZ + hr-HR sys",
             "Central Europe Standard Time",  "en-US", "hr-HR",
             "Croatia");

        Test("SK: CentralEurope TZ + sk-SK sys",
             "Central Europe Standard Time",  "en-US", "sk-SK",
             "Slovakia");

        Test("RO: GTB TZ + ro-RO sys",
             "GTB Standard Time",            "en-US", "ro-RO",
             "Romania");

        Test("GR: GTB TZ + el-GR sys",
             "GTB Standard Time",            "en-US", "el-GR",
             "Greece");

        Test("BG: GTB TZ + bg-BG sys",
             "GTB Standard Time",            "en-US", "bg-BG",
             "Bulgaria");

        Test("FI: FLE TZ + fi-FI sys",
             "FLE Standard Time",            "en-US", "fi-FI",
             "Finland");

        Test("EE: FLE TZ + et-EE sys",
             "FLE Standard Time",            "en-US", "et-EE",
             "Estonia");

        Test("LV: FLE TZ + lv-LV sys",
             "FLE Standard Time",            "en-US", "lv-LV",
             "Latvia");

        Test("LT: FLE TZ + lt-LT sys",
             "FLE Standard Time",            "en-US", "lt-LT",
             "Lithuania");

        Test("UA: FLE TZ + uk-UA sys",
             "FLE Standard Time",            "en-US", "uk-UA",
             "Ukraine");

        Test("BY: Russian TZ + be-BY sys",
             "Russian Standard Time",        "en-US", "be-BY",
             "Belarus");

        Test("IL: Israel TZ + he-IL sys",
             "Israel Standard Time",         "en-US", "he-IL",
             "Israel");

        Test("JO: Jordan TZ + ar-JO sys",
             "Jordan Standard Time",         "en-US", "ar-JO",
             "Jordan");

        Test("SY: Syria TZ + ar-SY sys",
             "Syria Standard Time",          "en-US", "ar-SY",
             "Syria");

        Test("IQ: Arabic TZ + ar-IQ sys",
             "Arabic Standard Time",         "en-US", "ar-IQ",
             "Iraq");

        Test("SA: Arab TZ + ar-SA sys",
             "Arab Standard Time",           "en-US", "ar-SA",
             "Saudi Arabia");

        Test("AE: Arabian TZ + ar-AE sys",
             "Arabian Standard Time",        "en-US", "ar-AE",
             "United Arab Emirates");

        Test("OM: Arabian TZ + ar-OM sys",
             "Arabian Standard Time",        "en-US", "ar-OM",
             "Oman");

        Test("KW: Arab TZ + ar-KW sys",
             "Arab Standard Time",           "en-US", "ar-KW",
             "Kuwait");

        Test("QA: Arab TZ + ar-QA sys",
             "Arab Standard Time",           "en-US", "ar-QA",
             "Qatar");

        Test("BH: Arab TZ + ar-BH sys",
             "Arab Standard Time",           "en-US", "ar-BH",
             "Bahrain");

        Test("YE: Arab TZ + ar-YE sys",
             "Arab Standard Time",           "en-US", "ar-YE",
             "Yemen");

        // ── Live system test ──────────────────────────────────────────────
        Logger.Log("─── Live System Detection ───");
        var ranked = TimezoneLanguageTriangulator.GetRankedCandidates();
        Logger.Log("Ranked candidates for THIS machine:");
        foreach (var (country, score) in ranked)
            Logger.Log($"  {score,2}pt  {country}");

        string live = TimezoneLanguageTriangulator.GetCountry();
        Logger.Log($"Live result: '{live}'");

        // ── Summary ───────────────────────────────────────────────────────
        Logger.Log($"═══ Results: {passed} passed, {failed} failed " +
                        $"({passed + failed} total) ═══");
    }
}
#endif
