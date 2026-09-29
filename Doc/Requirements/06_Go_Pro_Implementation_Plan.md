# Go Pro — Implementation Plan

**Status:** READY — design decisions approved (see `05_Go_Pro_Design.md` §11).
**Scope:** smallest change that delivers Pro licence check + purchase + "no ads".
Follows existing patterns: singleton services (`ThemeManager.Instance`), `MainForm`
partial files (`MainForm.ThemeToggle.cs`), `SettingsService.Current` + `Save()`, `Logger`.

---

## Files

| Action | File | Purpose | ~Lines |
|---|---|---|---|
| NEW | `Services/Store/MicrosoftStoreService.cs` | Thin Store wrapper (ported from NetSpeedMeterPlus) | 130 |
| NEW | `Services/Store/ProLicenseManager.cs` | Singleton: `IsPro`, cache, refresh, purchase, price | 150 |
| NEW | `Forms/GoProForm.cs` | Go Pro / You're Pro dialog (§5, §6 of design) | 280 |
| NEW | `Forms/MainForm.GoPro.cs` | Header button, menu + tray items, state refresh | 120 |
| EDIT | `Helper/AppConfig.cs` | Test override only (Store ID, price, email already added) | +5 |
| EDIT | `Models/AppSettings.cs` | Cached licence fields | +8 |
| EDIT | `Helper/ColorIcons.cs` | `ProStar` icon | +1 |
| EDIT | `DeviceData/AppStartupService.cs` | Skip promo launches for Pro | +10 |
| EDIT | `Forms/MainForm.Layout.cs` | Insert header button; shift Rebuild/Settings X | +6 |
| EDIT | `Forms/MainForm.SystemTray.cs` | Add tray item | +3 |
| EDIT | `Forms/AboutForm.cs` | Edition line + Go Pro button | +25 |

No new NuGet packages: the project already targets `net10.0-windows10.0.19041.0`, which
exposes `Windows.Services.Store` and `WinRT.Interop.InitializeWithWindow`.

---

## Step 1 — Config (`Helper/AppConfig.cs`)

```csharp
// ALREADY PRESENT (added by owner) — reuse, do not rename:
public const string LifetimeSubscriptionStoreId = "9NDK4ZTL7ML9";   // Anything Search Pro | Durable
public const string LifetimeSubscriptionStorePrice = "$9.99/year";  // fallback only; Store price wins
public static string SupportEmail = "shahedbddev@gmail.com";        // Pro support, subject "[PRO] ..."

// NEW — test-only. MUST be Off in every shipped build.
public static ProTestMode ProTest = ProTestMode.Off;
```
```csharp
public enum ProTestMode { Off, ForcePro, ForceFree }
```

## Step 2 — Settings (`Models/AppSettings.cs`)

```csharp
// ── Pro licence cache ───────────────────────────────────────────
/// <summary>Last Store answer for the Pro add-on. Only an explicit Store reply changes it.</summary>
public bool IsProCached { get; set; } = false;
public DateTime? LastLicenseCheckUtc { get; set; } = null;
```

## Step 3 — Store wrapper (`Services/Store/MicrosoftStoreService.cs`)

Port from NetSpeedMeterPlus with these changes only:
- namespace `AnythingSearch.Services.Store`; `Debug.WriteLine` -> `Logger.Log`.
- `GetEntitlementAsync` -> `Task<bool> HasProAsync()`: loop `appLicense.AddOnLicenses`,
  match `license.SkuStoreId.StartsWith(AppConfig.LifetimeSubscriptionStoreId)`, return `license.IsActive`.
  **Throws** on Store failure (caller keeps cache).
- `GetAnnualSubscriptionAsync` -> `GetProProductAsync()` — same
  `GetAssociatedStoreProductsAsync(new[] { "Durable" })` query, match `LifetimeSubscriptionStoreId`.
- `PurchaseAnnualSubscriptionAsync` -> `PurchaseProAsync()`.
- `GetFormattedPriceAsync()` falls back to `AppConfig.LifetimeSubscriptionStorePrice`;
  `GoProForm` strips any `/...` suffix before display (one-time, not yearly).
- Keep `IsPackaged()` exactly as is (unpackaged F5 runs must never touch `StoreContext`).
- Constructor takes no HWND; add `AttachWindow(IntPtr hwnd)` that calls
  `InitializeWithWindow.Initialize(_storeContext, hwnd)` once. `PurchaseProAsync` returns
  `null`-mapped failure if no window was attached.

## Step 4 — Licence manager (`Services/Store/ProLicenseManager.cs`)

Simplified `EntitlementManager`: no tiers, no trial, no grace window (durable never expires).

```csharp
public sealed class ProLicenseManager
{
    public static ProLicenseManager Instance { get; } = new();
    public event EventHandler? ProStatusChanged;

    /// <summary>True once the Store has confirmed the add-on this run or in a past run.</summary>
    public bool IsPro => AppConfig.ProTest switch
    {
        ProTestMode.ForcePro  => true,
        ProTestMode.ForceFree => false,
        _                     => SettingsService.Current.IsProCached
    };

    /// <summary>True when the Store answered at least once this run (used by the ad gate).</summary>
    public bool IsVerifiedThisRun { get; private set; }

    public void Initialize(IntPtr hwnd);                 // create store service if packaged
    public Task<bool> RefreshAsync();                    // returns false on Store failure
    public Task<StorePurchaseStatus?> PurchaseAsync();   // Succeeded/AlreadyPurchased -> RefreshAsync
    public Task<string> GetFormattedPriceAsync();
}
```

`RefreshAsync` rules:
- Store OK -> `IsProCached = result`, `LastLicenseCheckUtc = now`, `IsVerifiedThisRun = true`,
  `Save()`, raise `ProStatusChanged` **only if the value changed**.
- Store throws -> log, leave cache untouched, return false.
- Serialise concurrent calls with a `SemaphoreSlim(1,1)` (startup + Restore can overlap).

## Step 5 — No ads (`DeviceData/AppStartupService.cs`)

Inside the internet-available branch, before either promo block:

```csharp
// Pro owners never see promo pages. If the Store cannot answer yet, skip promos this
// run rather than risk showing one to a paying user.
bool licenceKnown = await ProLicenseManager.Instance.RefreshAsync()
                    || !MicrosoftStoreService.IsPackaged();
bool showPromos = licenceKnown && !ProLicenseManager.Instance.IsPro;
```

- Block A (install/update): still saves `AppVersion`/`PromotionCount` and sends device info;
  wrap only the `StartProcessAsync` pair in `if (showPromos)`.
- Block B (15-day): `if (showPromos && ShouldShowPromotion())`.

Ordering note: `ExecuteStartupTaskAsync` is started from `Program.cs:50` via `Task.Run`,
before MainForm has a handle. Licence **reads** (`GetAppLicenseAsync`) don't need a window,
so `ProLicenseManager` creates the `StoreContext` lazily on first use and only calls
`InitializeWithWindow` when `Initialize(hwnd)` runs (required for the purchase UI).
The startup task therefore needs no reordering.

## Step 6 — Icon (`Helper/ColorIcons.cs`)

```csharp
public static readonly ColorIcon ProStar = new("", Amber);
```

## Step 7 — Header, menu, tray (`Forms/MainForm.GoPro.cs` + small layout edits)

Partial class, same shape as `MainForm.ThemeToggle.cs`:

```csharp
private Button btnGoPro = null!;
private ToolStripMenuItem _goProMenuItem = null!, _restoreMenuItem = null!, _goProTrayItem = null!;

private void InitializeGoPro()          // called from ctor after layout
{
    ProLicenseManager.Instance.ProStatusChanged += OnProStatusChanged;
    FormClosed += (_, _) => ProLicenseManager.Instance.ProStatusChanged -= OnProStatusChanged;
    Load += async (_, _) =>
    {
        ProLicenseManager.Instance.Initialize(Handle);
        UpdateGoProUi();
        await ProLicenseManager.Instance.RefreshAsync();
    };
}

private void ShowGoPro() { using var f = new GoProForm(); f.ShowDialog(this); }
private void OnProStatusChanged(object? s, EventArgs e) => SafeInvoke(UpdateGoProUi);
private void UpdateGoProUi();            // text/tooltip/visibility per design §4, §6
```

Layout edit in `MainForm.Layout.cs` (~line 280): add `goProBtnWidth` and compute
`rebuildBtnX` one slot further left; add `btnGoPro` to `pnlHeader.Controls.AddRange`;
include it in the `ApplyTheme()` restyle loop. Help menu: insert `Go Pro...` and
`Restore Purchase` before the separator. Tray: insert `Go Pro...` after `Open`.

## Step 8 — Dialog (`Forms/GoProForm.cs`)

- Built in code (no Designer), DPI-scaled via existing `Scale()` style helper.
- One `TableLayoutPanel` column, `Anchor = None` rows between two 50% springs
  (the centring trick from `SubscriptionPromptControl.BuildLayout`).
- `BuildFreeLayout()` / `BuildProLayout()`; `SwapLayout()` clears and rebuilds on success.
- Price: `_ = LoadPriceAsync()` — guard `IsDisposed` before touching the label.
- Upgrade click: disable button, text `Opening Store...`, `await PurchaseAsync()`,
  map status per design §7.3 to an inline error label; restore button in `finally`.
- Store UI must run on the UI thread — call from the click handler, never `Task.Run`.
- Support button: `Process.Start` on
  `mailto:{AppConfig.SupportEmail}?subject=[PRO] Anything Search {AppConfig.AppVersion}`
  (via existing `StartupHelper.StartProcessAsync`).
- Theme: colours from `AppColors`; subscribe to `ThemeManager.ThemeChanged`, unsubscribe on close.

## Step 9 — About dialog (`Forms/AboutForm.cs`)

Add `Edition: FREE | PRO` label under `lblVersion`; in FREE add `[* Go Pro]` next to
"Check for Updates" that closes About and calls `MainForm.ShowGoPro()` (or opens
`GoProForm` directly with About as owner).

---

## Step 10 — Partner Center checklist (manual)

- [ ] Add-on `9NDK4ZTL7ML9` product type = **Durable**, lifetime = **Forever**.
- [ ] Price tier set; add-on **published** (purchase fails with `NotPurchased`/`ServerError` otherwise).
- [ ] Add-on listing text matches the four benefits (no feature-gating claims).
- [ ] Package associated with the Store app (`Package.StoreAssociation.xml` already present).

---

## Test flow

| # | Setup | Expect |
|---|---|---|
| 1 | Unpackaged F5, `ProTest = Off` | FREE UI; Upgrade shows "Store isn't available"; promos follow old rules |
| 2 | `ProTest = ForcePro` | `[* PRO]` badge, "You're Pro" dialog, **no** promo launches |
| 3 | `ProTest = ForceFree` | FREE UI everywhere |
| 4 | Packaged, Store sandbox / real account, no purchase | Price from Store, Upgrade opens Store window |
| 5 | Close Store window | Silent, button restored |
| 6 | Complete purchase | Dialog swaps to Pro; header badge updates without restart |
| 7 | Restart offline after purchase | Still Pro (cache); no promo |
| 8 | Fresh install on 2nd PC, same MS account | Pro after first refresh, or via Restore Purchase |
| 9 | Fresh install, Store unreachable, internet up | No promo this run (licence unknown) |
| 10 | Dark / light toggle with dialog open | Dialog repaints |
| 11 | Before shipping | `ProTest == Off` (grep) |

---

## Risks

- **Store latency at startup** — promo decision waits on `RefreshAsync`; acceptable, the
  startup task is already async and retry-based.
- **HWND timing** — Store purchase UI needs `InitializeWithWindow`; attach in `MainForm.Load`,
  not the ctor. Licence reads work before that (see Step 5).
- **Memory** — WinRT Store projection adds a few MB once loaded; loaded lazily only on
  packaged runs (relevant to the recent memory-optimisation work).
