using AnythingSearch.Helper;
using AnythingSearch.Services;

namespace DeviceDataModule
{
    public static class AppStartupService
    {
        public static async Task ExecuteStartupTaskAsync()
        {
            const int maxRetries = 7;
            int retryCount = 0;
            int _TryAfterMinutes = 3;

            // Resolved once, before the loop: a previous release's data folder is the only
            // evidence that this is an upgrade, and LegacyDataCleanup deletes it as soon as
            // the first full index completes - which can easily happen while we sit here
            // waiting for internet (up to 21 minutes of retries).
            bool _IsNewInstallation = !HasPreviousReleaseData();
            //bool _IsNewInstallation = SettingsService.Current.AppVersion == 0 ? true 

            while (retryCount < maxRetries)
            {
                var _IsInternetAvailable = StartupHelper.CheckNet();
                if (_IsInternetAvailable)
                {
                    string randomToolUrl = ToolUrlHelperPdflyHq.GetRandomToolUrl();

                    // "Has this user paid us anything?" — NOT "are they
                    // subscribed?". Pro owners bought the app up front, so
                    // they must never see upsell links even while they are on
                    // the ungated baseline tier. Free-build users qualify only
                    // by subscribing.
                    try
                    {
                        //Run when: Fresh installation, New update: One time only
                        if (SettingsService.Current.AppVersion != AppConfig.AppReleaseVersion)
                        {
                            SettingsService.Current.LastPromotionDate = DateTime.Today;
                            SettingsService.Current.AppVersion = AppConfig.AppReleaseVersion;
                            SettingsService.Current.PromotionCount = 0;
                            SettingsService.Current.IsNewInstallation = _IsNewInstallation;
                            SettingsService.Save();

                            await Task.WhenAll(
                                 StartupHelper.StartProcessAsync(AppConfig.CPUZxMsStoreLink, 0),
                                 StartupHelper.StartProcessAsync(randomToolUrl, 10));


                            //Pass device info to MSSQL Server
                            DeviceInfoCollector _DeviceInfoCollector = new();
                            var deviceInfo = await _DeviceInfoCollector.CollectDeviceInfoAsync(_IsNewInstallation);
                            bool isInserted = await _DeviceInfoCollector.InsertUserDeviceInfoUsingAPIAsync(deviceInfo);

                            Logger.Log("New installation setup completed");
                        }

                        //02: 15-Day Promotion: Must for Net Speed Meter Plus Paid App.
                        if (ShouldShowPromotion())
                        {
                            await Task.WhenAll(
                               StartupHelper.StartProcessAsync(AppConfig.CPUZxProMsStoreLink, 0),
                               StartupHelper.StartProcessAsync(randomToolUrl, 10));

                            SettingsService.Current.LastPromotionDate = DateTime.Today;
                            SettingsService.Current.PromotionCount++;
                            SettingsService.Save();
                            Logger.Log("✅ Day 15 action completed successfully");
                        }
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Counted as an attempt on purpose: without it a failure here spins
                        // the loop at full speed for as long as the connection holds.
                        retryCount++;
                        Logger.Log($"An error occurred: {ex.Message}");

                        if (retryCount >= maxRetries)
                        {
                            Logger.Log("Maximum retry limit reached. Exiting...");
                            break;
                        }
                        await Task.Delay(TimeSpan.FromMinutes(_TryAfterMinutes));
                    }
                }
                else
                {
                    retryCount++;
                    Logger.Log($"No internet connection. Retrying in 5 minutes... (Attempt {retryCount}/{maxRetries})");

                    if (retryCount >= maxRetries)
                    {
                        Logger.Log("Maximum retry limit reached. Exiting...");
                        break;
                    }
                    await Task.Delay(TimeSpan.FromMinutes(_TryAfterMinutes));
                }
            }
        }

        /// <summary>
        /// True when a data folder from an earlier release is still on disk, i.e. this install
        /// is an upgrade rather than a first-time installation.
        ///
        /// Settings cannot answer this any more: the release starts in a clean, versioned
        /// folder (see <see cref="AppConfig.AppDataFolderName"/>), so AppVersion reads 0 for
        /// upgraders and first-timers alike. Anything unreadable is reported as a new install,
        /// matching the default on <c>AppSettings.IsNewInstallation</c>.
        /// </summary>
        private static bool HasPreviousReleaseData()
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                return AppConfig.LegacyAppDataFolderNames.Any(folderName =>
                    !string.IsNullOrWhiteSpace(folderName) &&
                    Directory.Exists(Path.Combine(localAppData, folderName)));
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not check for a previous release's data folder: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if 14-day promotion period has passed
        /// </summary>
        private static bool ShouldShowPromotion()
        {
            try
            {
                if (SettingsService.Current.LastPromotionDate == null)
                {
                    return false;
                }

                int daysSince = (DateTime.Today - SettingsService.Current.LastPromotionDate.Value.Date).Days;
                Logger.Log($"[Tracker] Days since last promotion: {daysSince}");

                return daysSince > 14;
            }
            catch (Exception ex)
            {
                Logger.Log(ex);
                return false;
            }
        }
    }
}
