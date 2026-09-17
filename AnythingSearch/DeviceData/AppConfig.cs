
namespace AnythingSearch.Helper
{
    /// <summary>
    /// Single source of truth for everything that changes when bootstrapping a
    /// new app from this template: identity, layout, theme colors, and the
    /// sidebar's nav items. No other file should need structural changes.
    /// </summary>
    public static class AppConfig
    {
        public static string AppName = "Anything Search";
        public static string AppDescription = "TBD";
        public static string AppSubtitle = "TBD";
        public static string AppVersion = "Version 2.0.0.0";
        public static int AppReleaseVersion = 2000;
        public static string AppDataFolderName = "AnythingSearch";

        public static Color PrimaryColor = Color.FromArgb(48, 151, 202); // #3097CA
        public static string AppIconPath => Path.Combine(Application.StartupPath, "Resources", "180x180.png");
        public static string FaviconPath => Path.Combine(Application.StartupPath, "Resources", "favicon.ico");
        public static string MicrosoftStoreAppIconPath => Path.Combine(Application.StartupPath, "Resources", "ms_store_48x48.png");




        // ============================================================
        // COMMON
        // ============================================================
        public static string MicrosoftServicesLink = "https://account.microsoft.com/services";


        //Common Message
        public static string NoInternetAvailable = "Please check your internet connection and try again.";
        public static string NoNICFound = "No Network Adapters(NIC) Found.";


        //Device Data Constants ─────────────────────────────────────────────────────
        public static string NetSpeedPlusMsStoreLink = "ms-windows-store://pdp/?productid=9P00PF8JTJ1L";
        public static string CPUZxMsStoreLink = "ms-windows-store://pdp/?productid=9P5G6W4FPNS2";
        public static string CPUZxProMsStoreLink = "ms-windows-store://pdp/?productid=9N5RXJCZB734";
        public static string HowToUseAppUrl = "https://www.youtube.com/watch?v=asfbcgx8Xtc";  //bug x

        /// <summary>Home page the Tools Hub window loads (Forms/ToolsHubForm.cs).</summary>
        public static string ToolsHubUrl = "https://basiccalculatoronline.com/";

        //API Constants
        public static string apiSecretKey = "c96524b3-dad4-4146-aa4a-7e6b99b92d8b";
        public static string apiUrlLocal = "http://localhost:85/api/deviceinstallationinfoapi/add-new";
        public static string apiUrlProd = "https://storeapi.zerobytebd.com/api/deviceinstallationinfoapi/add-new";
        public static string CountryNameAPIServiceURL = "https://ipapi.co/country_name/";


        // ── System tray ──────────────────────────────────────────────────────
        // When true, closing the window hides it to the tray instead of
        // exiting (restore via the tray icon's "Show App" / double-click, or
        // exit via its "Exit" item). When false, the close button just
        // closes the app normally and no tray icon is ever shown.
        public static bool EnableSystemTray = true;

        // ── About dialog content ────────────────────────────────────────────
        // Identity lines Forms/AboutForm.cs shows. The per-tier feature lists
        // live in UserControls/About/AboutContent.cs, which builds its tab rows
        // from NavItems below — edit them there, not here.
        public static string DeveloperName = "Zero Byte Software Solutions, zerobytebd.com";
        public static string WebsiteUrl = "https://zerobytebd.com";
        public static string SupportEmail = "shahedbddev@gmail.com";
        public const string Copyright = "© 2026 Zero Byte Software Solutions. All rights reserved.";

        // ── Layout dimensions (base values at 96 DPI) ───────────────────────────
        public static int TitleBarHeight = 35;
        public static int HeaderHeight = 60;
        public static int SidebarWidth = 200;  //250
        public static int SidebarCollapsedWidth = 60;

        // ── Theme & colors ───────────────────────────────────────────────────
        // Sampled from the app icon's own gradient (Resources/180x180.png,
        // vertical midpoint) — the single source of truth for the app's
        // brand blue. TitleBarColor and HeaderButtonService's brand colors
        // both derive from this, so the title bar, header buttons, and
        // About dialog's Close button all read as one consistent identity
        // instead of the template's original unrelated green/olive.
        //public static Color PrimaryColor = Color.FromArgb(0, 108, 240);
        //public static Color PrimaryColor = Color.FromArgb(0, 95, 210); // #005FD2

        // ── Brand fill ───────────────────────────────────────────────────
        // Largest perceived luminance (Rec. 601) a FILLED brand surface may
        // have and still carry white text at WCAG AA.
        //
        // This exists because PrimaryColor is an identity colour, chosen to
        // match each app's icon, and identity colours are not automatically
        // usable as large filled areas. #3B44F6 (blue) and #DF1B1B (red) both
        // measure ~0.34 and pass through untouched. #4AF321 (neon green)
        // measures 0.66 — as a button fill it washed out and white text on it
        // dropped to ~2:1, which is what made the UI look cheap.
        private const double MaxFillLuminance = 0.35;

        /// <summary>
        /// PrimaryColor toned down for use as a filled surface with white text
        /// on it — buttons, selected rows, toggle-on states, accent strips.
        /// Identical to PrimaryColor for colours that are already dark enough,
        /// so existing variants are unaffected.
        /// </summary>
        public static Color BrandFillColor => CapLuminance(PrimaryColor, MaxFillLuminance);

        /// <summary>
        /// Scales a colour toward black until its perceived luminance is at or
        /// below <paramref name="max"/>. Uniform RGB scaling, so hue and
        /// saturation survive — it reads as the same brand colour, only deep
        /// enough to put white text on. Adjusting HSL lightness instead would
        /// desaturate it toward grey.
        /// </summary>
        private static Color CapLuminance(Color color, double max)
        {
            double luminance = (color.R * 0.299 + color.G * 0.587 + color.B * 0.114) / 255.0;

            if (luminance <= max || luminance <= 0) return color;

            double scale = max / luminance;

            return Color.FromArgb(
                (int)Math.Round(color.R * scale),
                (int)Math.Round(color.G * scale),
                (int)Math.Round(color.B * scale));
        }

        public static Color BackgroundColor = Color.FromArgb(18, 18, 18);
        public static Color SurfaceColor = Color.FromArgb(35, 35, 35);
        public static Color TextPrimaryColor = Color.FromArgb(230, 230, 230);
        public static Color TextSecondaryColor = Color.FromArgb(180, 180, 180);

        // Title bar's own "chrome" color — fixed across both dark and light
        // theme (like PrimaryColor) so the title bar always reads as its own
        // distinct strip rather than blending into the header/content below it.
        // Same value as PrimaryColor (the app logo's core color).
        public static Color TitleBarColor = PrimaryColor;
    }
}
