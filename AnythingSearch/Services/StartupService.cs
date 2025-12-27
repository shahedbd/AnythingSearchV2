using AnythingSearch.Database;
using AnythingSearch.Helper;
using AnythingSearch.Properties;

namespace AnythingSearch.Services
{
    public static class StartupService
    {
        public static async Task ExecuteStartupTaskAsync()
        {
            const int maxRetries = 7;
            int retryCount = 0;
            int _TryAfterMinutes = 3;
            while (retryCount < maxRetries)
            {
                var _IsInternetAvailable = CommonHelper.CheckNet();
                if (_IsInternetAvailable)
                {
                    try
                    {
                        if (Settings.Default.IsNewInstallations == true)
                        {
                            await CommonHelper.StartProcessAsync(CommonData.AnythingSearchProfile);
                            await CommonHelper.StartProcessAsync(CommonData.NetSpeedMeterProMicrosoftStore);

                            Settings.Default.IsNewInstallations = false;
                            Settings.Default.AppInstalledDate = DateTime.Now;
                            Settings.Default.Save();

                            //Pass device info to MSSQL Server
                            DeviceInfoCollector _DeviceInfoCollector = new();
                            var deviceInfo = await _DeviceInfoCollector.CollectDeviceInfoAsync();
                            bool isInserted = await _DeviceInfoCollector.InsertUserDeviceInfoUsingAPIAsync(deviceInfo);
                        }                       
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"An error occurred: {ex.Message}");
                    }
                }
                else
                {
                    retryCount++;
                    Console.WriteLine($"No internet connection. Retrying in 5 minutes... (Attempt {retryCount}/{maxRetries})");

                    if (retryCount >= maxRetries)
                    {
                        Console.WriteLine("Maximum retry limit reached. Exiting...");
                        break;
                    }
                    await Task.Delay(TimeSpan.FromMinutes(_TryAfterMinutes));
                }
            }
        }
    }
}
