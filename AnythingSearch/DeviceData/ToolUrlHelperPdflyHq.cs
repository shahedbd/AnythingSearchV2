namespace DeviceDataModule;

public static class ToolUrlHelperPdflyHq
{
    private static readonly List<string> ToolUrls = new()
    {
        // Homepage
        "/",

        // PDF Tools
        "/compress-pdf",
        "/merge-pdf",
        "/pdf-to-word",
        "/jpg-to-pdf",
        "/word-to-pdf",
        "/split-pdf",
        "/pdf-to-jpg",
        "/excel-to-pdf",
        "/powerpoint-to-pdf",
        "/ocr-pdf",
        "/sign-pdf",
        "/pdf-to-excel",
        "/protect-pdf",
        "/unlock-pdf",
        "/organize-pdf",
        "/add-watermark",
        "/rotate-pdf",
        "/edit-pdf",
        "/html-to-pdf",
        "/pdf-to-powerpoint",
        "/pdf-forms",
        "/redact-pdf",
        "/flatten-pdf",
        "/scan-to-pdf",
        "/add-page-numbers",
        "/repair-pdf",
        "/pdf-to-pdfa",
        "/compare-pdf",
        "/extract-pages",
        "/remove-pages",
        "/crop-pdf",
        "/pdf-header-footer",
        "/pdf-to-text",
        "/pdf-metadata-editor",
        "/pdf-to-csv",
        "/pdf-bookmarks-editor",
        "/pdf-accessibility-checker",
        "/pdf-to-html",
        "/pdf-to-epub",
        "/pdf-to-markdown",
        "/pdf-page-size-converter",
        "/pdf-grayscale-converter",
        "/pdf-to-json",
        "/pdf-to-tiff",
        "/pdf-signature-verifier",
        "/pdf-to-png",
        "/pdf-linearize",
        "/pdf-to-xml",
        "/pdf-background-remover",
        "/pdf-layers-manager"
    };

    private static readonly Random _random = new();
    private const string BaseUrl = "https://pdflyhq.com";

    public static string GetRandomToolUrl()
    {
        int index = _random.Next(ToolUrls.Count);
        return $"{BaseUrl}{ToolUrls[index]}";
    }
}
