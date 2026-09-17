namespace DeviceDataModule;

public static class ToolUrlHelperBasicCalc
{
    private static readonly List<string> ToolUrls = new()
        {
            // Homepage
            "/",
            "/all-tools",

            // Basic Calculators
            "/scientific-calculator",
            "/percentage-calculator",
            "/fraction-calculator",
            "/discount-calculator",
            "/time-calculator",
            "/gpa-calculator",
            "/grade-calculator",
            "/date-calculator",

            // Financial Calculators
            "/loan-emi-mortgage-calculator",
            "/mortgage-calculator",
            "/compound-interest-calculator",
            "/investment-calculator",
            "/retirement-calculator",

            // Health & Fitness
            "/bmi-calculator",
            "/age-calculator",
            "/calorie-calculator",
            "/tdee-calculator",
            "/ovulation-calculator",
            "/pregnancy-calculator",
            "/macro-calculator",
            "/ideal-weight-calculator",
            "/body-fat-calculator",

            // Converters
            "/unit-converter",
            "/currency",
            "/case-converter",
            "/number-to-word",
            "/color-converter",
            "/number-base-converter",
            "/temperature-converter",

            // Image & PDF Tools
            "/image-resizer",
            "/image-type",
            "/image-compressor",
            "/image-to-text",
            "/image-to-pdf",
            "/merge-pdf",
            "/image-cropper",
            "/background-remover",

            // Developer Tools
            "/json-formatter",
            "/qr-code-generator",
            "/password-generator",
            "/uuid-generator",
            "/base64-encoder",
            "/hash-generator",

            // Resource Tools
            "/invoice",
            "/pdf-to-word",
            "/word-counter",
            "/lorem-ipsum-generator",

            // Utility Tools
            "/white-screen",
            "/fake-update"
        };

    private static readonly Random _random = new();
    private const string BaseUrl = "https://basiccalculatoronline.com";

    public static string GetRandomToolUrl()
    {
        int index = _random.Next(ToolUrls.Count);
        return $"{BaseUrl}{ToolUrls[index]}";
    }
}
