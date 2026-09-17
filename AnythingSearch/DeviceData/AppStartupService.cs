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
            while (retryCount < maxRetries)
            {
                var _IsInternetAvailable = StartupHelper.CheckNet();
                if (_IsInternetAvailable)
                {
                    bool _IsNewInstallation = SettingsService.Current.AppVersion == 0 ? true : false;
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
                        Logger.Log($"An error occurred: {ex.Message}");
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
