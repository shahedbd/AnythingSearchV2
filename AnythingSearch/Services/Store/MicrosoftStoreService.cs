using AnythingSearch.Helper;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Services.Store;
using WinRT.Interop;

namespace AnythingSearch.Services.Store;

/// <summary>
/// Thin wrapper over the Microsoft Store APIs for the Anything Search Pro durable add-on
/// (AppConfig.LifetimeSubscriptionStoreId). Ported from NetSpeedMeterPlus's
/// Services/Store/MicrosoftStoreService.cs.
///
/// All licence interpretation and caching lives in ProLicenseManager - this class only talks
/// to the Store. Licence reads need no window, so the service can be created by the startup
/// task before MainForm exists; the purchase UI needs one, attached later by AttachWindow.
/// </summary>
public sealed class MicrosoftStoreService
{
    private readonly StoreContext _storeContext;
    private StoreProduct? _proProduct;

    public MicrosoftStoreService()
    {
        _storeContext = StoreContext.GetDefault();
    }

    /// <summary>True once a window handle has been attached for the Store's own UI.</summary>
    public bool IsWindowAttached { get; private set; }

    /// <summary>
    /// Required for desktop apps before any Store API shows UI (the purchase dialog).
    /// Only the first valid handle is used.
    /// </summary>
    public void AttachWindow(IntPtr windowHandle)
    {
        if (IsWindowAttached || windowHandle == IntPtr.Zero)
            return;

        InitializeWithWindow.Initialize(_storeContext, windowHandle);
        IsWindowAttached = true;
    }

    // ============================================================
    // PACKAGE IDENTITY
    // ============================================================

    /// <summary>
    /// True when running with MSIX package identity. StoreContext.GetDefault() throws without
    /// it, so an unpackaged run (plain F5, no packaging project) must never construct this
    /// service - ProLicenseManager treats unpackaged as "Store unavailable".
    /// </summary>
    public static bool IsPackaged()
    {
        try
        {
            int length = 0;
            // ERROR_INSUFFICIENT_BUFFER (122) => a package name exists.
            // APPMODEL_ERROR_NO_PACKAGE (15700) => unpackaged.
            int result = GetCurrentPackageFullName(ref length, null);
            return result != APPMODEL_ERROR_NO_PACKAGE;
        }
        catch (Exception ex)
        {
            Logger.Log($"Package identity check failed: {ex.Message}");
            return false;
        }
    }

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    // ============================================================
    // PUBLIC API
    // ============================================================

    /// <summary>
    /// True when the current Microsoft account owns an active Pro licence. Throws on any Store
    /// failure - the caller decides whether to fall back to its cached answer.
    /// </summary>
    public async Task<bool> HasProAsync()
    {
        StoreAppLicense appLicense = await _storeContext.GetAppLicenseAsync()
            ?? throw new InvalidOperationException("The Store returned no app licence.");

        foreach (var item in appLicense.AddOnLicenses)
        {
            StoreLicense license = item.Value;

            if (license.SkuStoreId.StartsWith(
                    AppConfig.LifetimeSubscriptionStoreId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return license.IsActive;
            }
        }

        return false;
    }

    /// <summary>Loads the Pro add-on product from the Store, or null when it can't be found.</summary>
    public async Task<StoreProduct?> GetProProductAsync()
    {
        try
        {
            var result = await _storeContext.GetAssociatedStoreProductsAsync(new[] { "Durable" });

            if (result.ExtendedError != null)
            {
                Logger.Log($"Store product error: {result.ExtendedError.Message}");
                return null;
            }

            foreach (var item in result.Products)
            {
                StoreProduct product = item.Value;

                if (product.StoreId.Equals(
                        AppConfig.LifetimeSubscriptionStoreId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _proProduct = product;
                    return product;
                }
            }

            Logger.Log("Pro add-on product was not found.");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"Get Pro product error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Purchases the Pro add-on. Displays Microsoft's own Store UI, so this must be called on
    /// the UI thread after <see cref="AttachWindow"/>.
    /// </summary>
    public async Task<StorePurchaseStatus> PurchaseProAsync()
    {
        try
        {
            var product = _proProduct ?? await GetProProductAsync();

            if (product == null)
                return StorePurchaseStatus.NotPurchased;

            StorePurchaseResult result = await product.RequestPurchaseAsync();

            if (result.ExtendedError != null)
                Logger.Log($"Purchase error: {result.ExtendedError.Message}");

            return result.Status;
        }
        catch (Exception ex)
        {
            Logger.Log($"Pro purchase error: {ex.Message}");
            return StorePurchaseStatus.ServerError;
        }
    }

    /// <summary>
    /// Price of the add-on as the Store reports it (already localised and currency-correct),
    /// falling back to AppConfig.LifetimeSubscriptionStorePrice when the Store can't be reached.
    /// </summary>
    public async Task<string> GetFormattedPriceAsync()
    {
        var product = _proProduct ?? await GetProProductAsync();
        return string.IsNullOrWhiteSpace(product?.Price?.FormattedPrice)
            ? AppConfig.LifetimeSubscriptionStorePrice
            : product.Price.FormattedPrice;
    }
}
