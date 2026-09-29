using AnythingSearch.Services.Store;

namespace AnythingSearch.Forms;

/// <summary>
/// About dialog's hand-off to Go Pro: both the header's edition badge ("FREE · Go Pro" /
/// "★ PRO") and the Go Pro card's call to action open GoProForm - the Go Pro dialog for Free
/// users, the "You're Pro" dialog for owners. See 05_Go_Pro_Design.md §8.
/// </summary>
public partial class AboutForm
{
    private void OpenGoPro()
    {
        bool wasPro = ProLicenseManager.Instance.IsPro;

        using (var dialog = new GoProForm())
            dialog.ShowDialog(this);

        // A purchase just now changes the badge and the cards - rebuild for the new edition.
        if (ProLicenseManager.Instance.IsPro != wasPro)
        {
            BuildLayout();
            ScrollToTop();
        }
    }
}
