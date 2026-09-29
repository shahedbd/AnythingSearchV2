using AnythingSearch.Helper;
using AnythingSearch.Services.Store;

namespace AnythingSearch.UserControls.About;

/// <summary>One card on the About screen, as described by the tier plan.</summary>
public sealed class AboutCardSpec
{
    /// <summary>Header glyph; its color is the card's accent.</summary>
    public ColorIcon Icon { get; init; }
    public string Title { get; init; } = string.Empty;
    public (ColorIcon Icon, string Text)[] Rows { get; init; } = [];
    public string? Note { get; init; }

    /// <summary>Null for an informational card; set to show the Go Pro call to action.</summary>
    public string? CtaText { get; init; }
}

/// <summary>
/// What the About screen shows for the user's edition - edition badge, cards and whether an
/// upgrade is on offer at all. Ported from NetSpeedMeterPlus's AboutTierPlan, reduced to the
/// two editions this app has:
///
/// Free → the free features, then Go Pro (with its benefits and the CTA).
/// Pro  → what the purchase covers, then everything else included; no upsell.
/// </summary>
public sealed class AboutTierPlan
{
    public string EditionLabel { get; private init; } = string.Empty;
    public Color EditionColor { get; private init; }
    public AboutCardSpec[] Cards { get; private init; } = [];

    /// <summary>Builds the plan for the edition the license currently resolves to.</summary>
    public static AboutTierPlan ForCurrentTier() =>
        ProLicenseManager.Instance.IsPro ? Pro() : Free();

    private static AboutTierPlan Free() => new()
    {
        EditionLabel = "FREE · Go Pro",
        EditionColor = ColorIcons.CheckMark.Color,
        Cards =
        [
            new AboutCardSpec
            {
                Icon = ColorIcons.CheckMark,
                Title = "Included in this free edition",
                Rows = AboutContent.FreeFeatures,
                Note = AboutContent.FreeUpgradeNote
            },
            new AboutCardSpec
            {
                Icon = ColorIcons.ProStar,
                Title = "Go Pro - one payment, yours for life",
                Rows = AboutContent.ProBenefits(isPro: false),
                Note = AboutContent.PurchaseNote,
                CtaText = "Go Pro"
            }
        ]
    };

    private static AboutTierPlan Pro() => new()
    {
        EditionLabel = "★ PRO",
        EditionColor = ColorIcons.ProStar.Color,
        Cards =
        [
            new AboutCardSpec
            {
                Icon = ColorIcons.ProStar,
                Title = "Pro is active - thank you!",
                Rows = AboutContent.ProBenefits(isPro: true),
                Note = AboutContent.ProOwnerNote
            },
            new AboutCardSpec
            {
                Icon = ColorIcons.CheckMark,
                Title = "Also included",
                Rows = AboutContent.FreeFeatures
            }
        ]
    };
}
