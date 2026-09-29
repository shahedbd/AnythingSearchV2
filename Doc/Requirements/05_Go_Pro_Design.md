# Go Pro — Design (UI + Behaviour)

**Status:** REVIEWED — decisions recorded in §11. No code written yet.
**Product:** Anything Search Pro — Microsoft Store **Durable add-on**, Store ID `9NDK4ZTL7ML9`
**Reference:** NetSpeedMeterPlus `Managers/EntitlementManager.cs`,
`Services/Store/MicrosoftStoreService.cs`, `UserControls/Pages/SubscriptionPromptControl.cs`
**Implementation steps:** see `06_Go_Pro_Implementation_Plan.md`

---

## 1. What Pro is (and is not)

| Pro benefit | What it means in code |
|---|---|
| **One-time payment** | Durable add-on, bought once via Microsoft Store. No subscription, no expiry, no renewal. |
| **No ads** | `AppStartupService` never launches promo links (CPU-Zx Store pages, PdflyHq tool URLs). |
| **Lifetime updates** | Marketing promise only — Store updates are already free. Nothing to gate. |
| **Lifetime support** | "Pro Support" email link with `[PRO]` subject tag so we can prioritise it. |

**Search features are NOT gated.** Free users keep 100% of search, indexing, auto-watch,
theme, settings. Pro removes interruptions; it does not take anything away from Free.
This keeps the change small, avoids padlocks everywhere, and avoids 1-star
"they locked my search" reviews.

> Difference from NetSpeedMeterPlus: that app sells an **annual subscription** that
> **unlocks tabs** (trial, grace window, `UserTier` ladder). None of that is needed here —
> we copy only the Store plumbing and the prompt layout, not the tier/trial model.

---

## 2. What counts as an "ad" today

`DeviceData/AppStartupService.cs`:

| # | Trigger | Opens | Pro behaviour |
|---|---|---|---|
| A | Fresh install / new version (once) | `CPUZxMsStoreLink` + random PdflyHq tool URL | **Skip** |
| B | Every 15 days (`ShouldShowPromotion`) | `CPUZxProMsStoreLink` + random PdflyHq tool URL | **Skip** |
| C | Fresh install / new version | Device-info API call (telemetry, not an ad) | Unchanged |

Rule: *if the licence is Pro, or cannot be determined this run, do not open anything.*
Failing toward "no ad" protects paying users who start the app offline or before the
Store has answered.

---

## 3. Entry points (where the user meets "Go Pro")

```
 (1) Header button   (2) Help menu     (3) Tray menu        (4) About dialog
 [* Go Pro]          Help              Open Anything Search  Edition: FREE  [Go Pro]
                      About...          ---------------      Edition: PRO   (thanks)
                      Check Update      * Go Pro...
                      * Go Pro...       Status: ...
                      Restore Purchase  Exit
                      Website
```

All four open the same **Go Pro dialog** (§5). After purchase, every entry point
switches to its "Pro" state (§6). No popups, no timers, no auto-open.

---

## 4. Header button — placement

Current header (right side): `Search | Rebuild | Settings | [x] Auto-Watch | Theme`.
Go Pro is inserted left of Rebuild so the theme button stays top-right.

**FREE**
```
+--------------------------------------------------------------------------------------+
| File  Help                                                                           |
+--------------------------------------------------------------------------------------+
|  [ (o) Search files and folders...          x ]  [* Go Pro] [@] [#] [x] Auto-Watch [C] |
+--------------------------------------------------------------------------------------+
     * = amber star glyph, amber text      @ = Rebuild   # = Settings   C = Theme
```

**PRO**
```
+--------------------------------------------------------------------------------------+
|  [ (o) Search files and folders...          x ]  [* PRO]    [@] [#] [x] Auto-Watch [C] |
+--------------------------------------------------------------------------------------+
     [* PRO] = small amber badge, flat, no border; click opens "You're Pro" (§6)
     tooltip: "Anything Search Pro — thank you!"
```

Tooltip (FREE): `One-time payment - No ads - Lifetime updates & support`.

---

## 5. Go Pro dialog — FREE user

Modal `GoProForm`, ~460 x 560 @96 DPI, centred on MainForm, themed (light/dark).

```
+------------------------------------------------------+
|  Go Pro                                          [X] |
+------------------------------------------------------+
|                                                      |
|                     .-------.                        |
|                    (    *    )      <- amber circle  |
|                     '-------'                        |
|                                                      |
|              Anything Search  PRO                    |
|           Pay once. Own it for life.                 |
|                                                      |
|   +----------------------------------------------+   |
|   |  [v]  One-time payment                       |   |
|   |       No subscription. No renewals.          |   |
|   |                                              |   |
|   |  [v]  No ads                                 |   |
|   |       No promo pages or browser tabs, ever.  |   |
|   |                                              |   |
|   |  [v]  Lifetime updates                       |   |
|   |       Every future version included.         |   |
|   |                                              |   |
|   |  [v]  Lifetime support                       |   |
|   |       Priority email support from the dev.   |   |
|   +----------------------------------------------+   |
|                                                      |
|                      $9.99                           |
|                  one-time payment                    |
|                                                      |
|          +--------------------------------+          |
|          |      *  Upgrade to Pro         |          |  <- amber fill, black text
|          +--------------------------------+          |
|                                                      |
|              Restore purchase  |  Not now            |  <- link labels
|                                                      |
|   [MS] Secure payment by Microsoft Store.            |
|        Your search features stay free, always.       |
+------------------------------------------------------+
```

Notes
- Price label starts with `AppConfig.LifetimeSubscriptionStorePrice` fallback (any `/year` suffix stripped), then replaced by the Store's
  localised `FormattedPrice` (same pattern as `SubscriptionPromptControl.LoadPriceAsync`).
- `[MS]` = `Resources/ms_store_48x48.png` (already referenced by `AppConfig`).
- `Not now` = close. `Restore purchase` = re-query Store licence (§7.4).
- `Esc` closes; `Enter` = Upgrade.

---

## 6. "You're Pro" dialog — PRO user

Same form, Pro layout. Opened from the `[* PRO]` badge, Help menu, tray, or About.

```
+------------------------------------------------------+
|  Anything Search Pro                             [X] |
+------------------------------------------------------+
|                                                      |
|                     .-------.                        |
|                    (    V    )     <- green circle   |
|                     '-------'                        |
|                                                      |
|                  You're Pro!                         |
|             Thank you for your support.              |
|                                                      |
|   +----------------------------------------------+   |
|   |  [v]  Paid once - yours for life             |   |
|   |  [v]  No ads                                 |   |
|   |  [v]  Lifetime updates                       |   |
|   |  [v]  Lifetime support                       |   |
|   +----------------------------------------------+   |
|                                                      |
|          +--------------------------------+          |
|          |   @  Contact Pro Support       |          |  <- mailto:, subject "[PRO] ..."
|          +--------------------------------+          |
|                                                      |
|                        Close                         |
+------------------------------------------------------+
```

Menu/tray labels in Pro state: `Go Pro...` becomes `Anything Search Pro` (same dialog).
`Restore Purchase` is hidden once Pro.

---

## 7. Step-by-step flows

### 7.1 App start
```
MainForm.Load
  -> ProLicenseManager.Initialize(Handle)       (needs a real HWND for Store UI)
  -> header shows cached state immediately      (SettingsService.Current.IsProCached)
  -> ProLicenseManager.RefreshAsync()  [background]
        Store OK   -> IsPro = add-on active; cache it; raise ProStatusChanged
        Store FAIL -> keep cached value (fail-soft)
  -> ProStatusChanged -> header / menus / tray re-render
```

### 7.2 Upgrade (happy path)
```
[* Go Pro] click
  -> GoProForm (FREE layout), price loads async
  -> [Upgrade to Pro] click
       button: "Opening Store..." (disabled)
  -> Microsoft Store purchase window (Microsoft's UI, card never touches the app)
  -> Succeeded / AlreadyPurchased
       -> RefreshAsync() -> IsPro = true
       -> dialog swaps to "You're Pro" layout (§6)
       -> header [* Go Pro] -> [* PRO]
```

### 7.3 Upgrade — other results
| `StorePurchaseStatus` | UI |
|---|---|
| `NotPurchased` (user closed Store window) | Silent. Button restored. |
| `NetworkError` | Inline red line: "No internet connection. Please try again." |
| `ServerError` / other | Inline red line: "Purchase couldn't be completed. Please try again." |
| `null` (unpackaged / Store init failed) | Inline: "Microsoft Store isn't available right now." |

Inline message sits under the button — no extra MessageBox on top of a modal.

```
|          +--------------------------------+          |
|          |      *  Upgrade to Pro         |          |
|          +--------------------------------+          |
|      ! Purchase couldn't be completed. Try again.    |   <- AppColors.Error
```

### 7.4 Restore purchase (new PC / reinstall)
```
[Restore purchase] click
  -> link text: "Checking..."
  -> RefreshAsync()
       Pro found   -> swap to "You're Pro" layout
       not found   -> inline: "No Pro purchase found for this Microsoft account."
       Store error -> inline: "Couldn't reach Microsoft Store. Try again later."
```
Durable licences follow the Microsoft account, so Restore is just a re-query — no keys.

### 7.5 Refund / licence revoked
Next successful Store check returns no active add-on -> `IsPro = false`, cache cleared,
UI returns to FREE. Only an explicit Store answer downgrades; a failed check never does.

---

## 8. About dialog addition

One line under the version label; one button in the existing button row.

```
|              Anything Search                         |
|              Version 2.0.1.0                         |
|              Edition: FREE          [* Go Pro]       |    <- FREE
|              Edition: PRO  *  Thank you!             |    <- PRO (amber)
```

---

## 9. Theming & icons

| Element | Light | Dark |
|---|---|---|
| Star glyph / badge | Amber (`ColorIcons` Amber) | Amber |
| Upgrade button | Amber fill, black text | Amber fill, black text |
| Pro tick circle | `AppColors.Success` tint | same |
| Card / text | `AppColors.Surface` / `TextPrimary` | same tokens |

New `ColorIcons` entry: `ProStar = new("", Amber)` (Segoe MDL2 FavoriteStarFill).
Dialog subscribes to `ThemeManager.ThemeChanged` like `MainForm.Theme.cs`.

---

## 10. Out of scope (proposed)

- No feature gating / padlocks / trial.
- No nag popup or timer-based upsell. The existing 15-day promo is the only periodic
  touch, and it already points elsewhere; revisit only if conversion is too low.
- No separate "Pro" build/Store listing — one app, one add-on.

---

## 11. Review decisions

| # | Question | Decision |
|---|---|---|
| 1 | Price / Store ID constants | Use existing `AppConfig.LifetimeSubscriptionStoreId = "9NDK4ZTL7ML9"` and `AppConfig.LifetimeSubscriptionStorePrice` (fallback only; Store price wins at runtime). |
| 2 | Header button left of Rebuild | **Approved.** |
| 3 | Support address | Existing `AppConfig.SupportEmail`, subject tagged `[PRO]`. |
| 4 | Point 15-day promo at Pro dialog | **Skipped** — promo unchanged for Free users. |
| 5 | Grandfathering old paid users | **Skipped** — none. |

**Price suffix:** `LifetimeSubscriptionStorePrice` is currently `"$9.99/year"`. The
product is a one-time Durable, so the dialog strips any `/...` suffix (same
`ExtractAmount` approach as NetSpeedMeterPlus) and shows `$9.99` + "one-time payment".
Recommended: change the constant to `"$9.99"` so no other caller shows "/year".
