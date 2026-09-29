using AnythingSearch.Helper;
using Windows.Services.Store;

namespace AnythingSearch.Services.Store;

/// <summary>
/// App-wide singleton owning the Anything Search Pro licence - a one-time, durable add-on.
/// A simplified port of NetSpeedMeterPlus's EntitlementManager: no tiers, no trial and no
/// grace window, because a durable licence never expires.
///
/// The answer is cached in AppSettings.IsProCached and only an explicit Store reply changes
/// it, so a paying user who starts the app offline stays Pro. See
/// Doc/Requirements/05_Go_Pro_Design.md.
/// </summary>
public sealed class ProLicenseManager
{
    public static ProLicenseManager Instance { get; } = new();

    private readonly object _storeLock = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private MicrosoftStoreService? _storeService;
    private bool _storeUnavailable;

    /// <summary>Raised (on any thread) when IsPro flips.</summary>
    public event EventHandler? ProStatusChanged;

    private ProLicenseManager() { }

    /// <summary>True when this user owns Pro, as last confirmed by the Store.</summary>
    public bool IsPro => AppConfig.ProTest switch
    {
        ProTestMode.ForcePro => true,
        ProTestMode.ForceFree => false,
        _ => SettingsService.Current.IsProCached
    };

    /// <summary>
    /// Store service, created on first use. Null on unpackaged runs or when creation failed -
    /// StoreContext throws without package identity, so it is never attempted there.
    /// </summary>
    private MicrosoftStoreService? Store
    {
        get
        {
            lock (_storeLock)
            {
                if (_storeService != null || _storeUnavailable)
                    return _storeService;

                if (!MicrosoftStoreService.IsPackaged())
                {
                    Logger.Log("No package identity - Pro licence checks skipped; cached state governs.");
                    _storeUnavailable = true;
                    return null;
                }

                try
                {
                    _storeService = new MicrosoftStoreService();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Store service init failed: {ex.Message}");
                    _storeUnavailable = true;
                }

                return _storeService;
            }
        }
    }

    /// <summary>
    /// Attaches the main window so the Store purchase dialog can be shown. Call once a real
    /// handle exists (MainForm.Load). Safe to call repeatedly.
    /// </summary>
    public void Initialize(IntPtr windowHandle)
    {
        try
        {
            Store?.AttachWindow(windowHandle);
        }
        catch (Exception ex)
        {
            Logger.Log($"Store window attach failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-checks the Store licence and caches the answer. Returns false when the Store could
    /// not be asked (unpackaged, offline, error) - the cached value is then left untouched.
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        var store = Store;
        if (store == null)
            return false;

        await _refreshLock.WaitAsync();
        try
        {
            SetCachedPro(await store.HasProAsync());
            return true;
        }
        catch (Exception ex)
        {
            // Fail soft - keep whatever cache we already have.
            Logger.Log($"Pro licence check failed, keeping cached value: {ex.Message}");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Runs the Store purchase flow. Returns null when the Store is not available (unpackaged
    /// run, failed init, or no window attached yet). Must be called on the UI thread.
    /// </summary>
    public async Task<StorePurchaseStatus?> PurchaseAsync()
    {
        var store = Store;
        if (store == null || !store.IsWindowAttached)
            return null;

        var status = await store.PurchaseProAsync();

        // Trust the Store's own purchase result rather than re-reading the licence straight
        // away: the licence can take a moment to show the new add-on, and a stale read would
        // leave a buyer looking unpaid. The next startup refresh confirms it.
        if (status is StorePurchaseStatus.Succeeded or StorePurchaseStatus.AlreadyPurchased)
            SetCachedPro(true);

        return status;
    }

    /// <summary>Store-formatted add-on price, falling back to AppConfig.</summary>
    public async Task<string> GetFormattedPriceAsync()
    {
        var store = Store;
        if (store == null)
            return AppConfig.LifetimeSubscriptionStorePrice;

        try
        {
            return await store.GetFormattedPriceAsync();
        }
        catch
        {
            return AppConfig.LifetimeSubscriptionStorePrice;
        }
    }

    private void SetCachedPro(bool isPro)
    {
        var settings = SettingsService.Current;
        bool changed = settings.IsProCached != isPro;

        settings.IsProCached = isPro;
        settings.LastLicenseCheckUtc = DateTime.UtcNow;
        SettingsService.Save();

        if (changed)
            ProStatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
