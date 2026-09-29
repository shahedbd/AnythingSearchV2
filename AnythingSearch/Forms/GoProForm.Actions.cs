using AnythingSearch.Helper;
using AnythingSearch.Services.Store;
using System.Diagnostics;
using Windows.Services.Store;

namespace AnythingSearch.Forms;

/// <summary>
/// GoProForm behaviour: price loading, the Store purchase, restore, and Pro support.
/// Outcomes follow Doc/Requirements/05_Go_Pro_Design.md §7 - errors show inline under the
/// button rather than as a MessageBox on top of a modal.
/// </summary>
public sealed partial class GoProForm
{
    /// <summary>Display price; starts at the AppConfig fallback, replaced by the Store's.</summary>
    private string _price = ExtractAmount(AppConfig.LifetimeSubscriptionStorePrice);

    /// <summary>
    /// Strips any period suffix ("$9.99/year" -> "$9.99"). Pro is a one-time purchase, so a
    /// "/year" must never reach the dialog, whichever source the price came from.
    /// </summary>
    private static string ExtractAmount(string price)
    {
        if (string.IsNullOrWhiteSpace(price)) return string.Empty;

        int slash = price.IndexOf('/');
        return (slash >= 0 ? price[..slash] : price).Trim();
    }

    private async Task LoadPriceAsync()
    {
        try
        {
            string price = ExtractAmount(await ProLicenseManager.Instance.GetFormattedPriceAsync());
            if (string.IsNullOrWhiteSpace(price)) return;

            _price = price;
            if (!IsDisposed && _lblPrice is { IsDisposed: false })
                _lblPrice.Text = price;
        }
        catch
        {
            // Leave the AppConfig fallback in place.
        }
    }

    private void ShowError(string? message)
    {
        if (_lblError == null || _lblError.IsDisposed) return;

        _lblError.Text = message ?? string.Empty;
        _lblError.Visible = !string.IsNullOrEmpty(message);
    }

    // ══════════════════════════════════════════════════════════════════
    //  PURCHASE
    // ══════════════════════════════════════════════════════════════════

    private async void BtnUpgrade_Click(object? sender, EventArgs e)
    {
        if (_btnUpgrade == null) return;

        var button = _btnUpgrade;
        string original = button.Text;
        button.Enabled = false;
        button.Text = "Opening Store...";
        ShowError(null);

        try
        {
            // Store UI must run on the UI thread - never wrap this in Task.Run.
            var status = await ProLicenseManager.Instance.PurchaseAsync();

            switch (status)
            {
                case StorePurchaseStatus.Succeeded:
                case StorePurchaseStatus.AlreadyPurchased:
                    if (!IsDisposed) BuildLayout();   // swap to "You're Pro"
                    return;

                case StorePurchaseStatus.NotPurchased:
                    // User closed the Store window - not an error.
                    break;

                case StorePurchaseStatus.NetworkError:
                    ShowError("No internet connection. Please try again.");
                    break;

                case null:
                    ShowError("Microsoft Store isn't available right now.");
                    break;

                default:
                    ShowError("Purchase couldn't be completed. Please try again.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex);
            ShowError("Purchase couldn't be completed. Please try again.");
        }
        finally
        {
            if (!button.IsDisposed)
            {
                button.Text = original;
                button.Enabled = true;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  RESTORE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Durable licences follow the Microsoft account, so restoring is just a fresh licence
    /// read - no keys, no codes.
    /// </summary>
    private async void LnkRestore_Click(object? sender, EventArgs e)
    {
        if (_lnkRestore == null) return;

        var link = _lnkRestore;
        link.Enabled = false;
        link.Text = "Checking...";
        ShowError(null);

        bool reached = await ProLicenseManager.Instance.RefreshAsync();
        if (IsDisposed) return;

        if (ProLicenseManager.Instance.IsPro)
        {
            BuildLayout();
            return;
        }

        link.Text = "Restore purchase";
        link.Enabled = true;
        ShowError(reached
            ? "No Pro purchase found for this Microsoft account."
            : "Couldn't reach Microsoft Store. Try again later.");
    }

    // ══════════════════════════════════════════════════════════════════
    //  SUPPORT
    // ══════════════════════════════════════════════════════════════════

    private void BtnSupport_Click(object? sender, EventArgs e)
    {
        string subject = Uri.EscapeDataString($"[PRO] {AppConfig.AppName} {AppConfig.AppVersion}");

        try
        {
            Process.Start(new ProcessStartInfo($"mailto:{AppConfig.SupportEmail}?subject={subject}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"Could not open mail client: {ex.Message}");
        }
    }
}
