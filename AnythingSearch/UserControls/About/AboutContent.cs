using AnythingSearch.Forms;
using AnythingSearch.Helper;

namespace AnythingSearch.UserControls.About;

/// <summary>
/// The text the About screen shows, kept in one place. Ported from NetSpeedMeterPlus's
/// AboutContent; icons are ColorIcons glyphs instead of FontAwesome, so each row carries its
/// own color. The Pro rows come from GoProForm.Benefits, so About and Go Pro can never
/// describe the add-on differently.
/// </summary>
public static class AboutContent
{
    /// <summary>Everything the free edition does - available in every edition.</summary>
    public static (ColorIcon Icon, string Text)[] FreeFeatures =>
    [
        (ColorIcons.Search, "Instant search - millions of files, results as you type"),
        (ColorIcons.Rebuild, "Smart indexing - phased, resumable, runs in the background"),
        (ColorIcons.AutoWatch, "Auto-Watch - keeps the index in sync with your drives"),
        (ColorIcons.History, "Recent searches - one click to run them again"),
        (ColorIcons.OpenLocation, "Right-click actions - open, locate, copy path or name"),
        (ColorIcons.MinimizeToTray, "System tray - always one double-click away"),
        (ColorIcons.Moon, "Dark & Light themes"),
        (ColorIcons.Lightning, "Lightweight - compact local index, low memory use"),
    ];

    /// <summary>What the Pro add-on covers, worded for a buyer or an owner.</summary>
    public static (ColorIcon Icon, string Text)[] ProBenefits(bool isPro) =>
        GoProForm.Benefits
            .Select(b => (ColorIcons.ProStar, $"{(isPro ? b.ProTitle : b.Title)} - {b.Detail}"))
            .ToArray();

    // ── Copy ─────────────────────────────────────────────────────────────

    public const string FreeUpgradeNote = "Upgrade to Pro once and keep every benefit for life.";
    public const string ProOwnerNote = "Your one-time purchase covers every future version.";
    public const string PurchaseNote = "One-time purchase through the Microsoft Store - no subscription.";

    public static string Tagline => AppConfig.AppDescription;
    public const string SubTagline = "Built with C#, WinForms and .NET 10.";

    public static string WebsiteUrl => AppConfig.ProductPageUrl;
    public static string WebsiteLabel => WebsiteUrl.Replace("https://", "").Replace("http://", "").TrimEnd('/');
    public static string Developer => AppConfig.CompanyName;
    public static string SupportEmail => AppConfig.SupportEmail;
}
