using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AnythingSearch.Helper;

namespace DeviceDataModule
{
    public class DeviceInfoCollector
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// Collects device information with parallel execution for better performance
        /// </summary>
        public async Task<DeviceInstallationInfoVm> CollectDeviceInfoAsync(bool _IsNewInstallation)
        {
            var deviceInfo = new DeviceInstallationInfoVm();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Fast synchronous operations (no need for async)
                deviceInfo.UserName = WindowsIdentity.GetCurrent().Name;
                deviceInfo.MachineName = Environment.MachineName;
                deviceInfo.OSVersion = Environment.OSVersion.ToString();
                deviceInfo.OSName = GetOSName();
                deviceInfo.DotNetVersion = Environment.Version.ToString();
                deviceInfo.ScreenResolution = $"{Screen.PrimaryScreen.Bounds.Width}x{Screen.PrimaryScreen.Bounds.Height}";
                deviceInfo.TimeZone = TimeZoneInfo.Local.StandardName;
                deviceInfo.AppName = AppConfig.AppName;
                deviceInfo.AppVersion = AppConfig.AppReleaseVersion.ToString();
                deviceInfo.IsNewInstallation = _IsNewInstallation;
                //deviceInfo.IsNewInstallation = SettingsService.Current.IsNewInstallation;

                deviceInfo.UserConsentGiven = true;
                deviceInfo.CreatedDate = DateTime.Now;

                // ✅ NEW FIELDS - Fast synchronous operations
                deviceInfo.AppLanguage = CultureInfo.CurrentUICulture.Name; // e.g., "en-US"
                deviceInfo.SystemLanguage = CultureInfo.InstalledUICulture.Name; // OS language
                deviceInfo.IsAdministrator = IsRunningAsAdministrator();
                deviceInfo.AppArchitecture = GetAppArchitecture();
                deviceInfo.AvailableDiskSpace = GetAvailableDiskSpace();
                deviceInfo.InstallationSource = GetInstallationSource();

                // Session tracking
                deviceInfo.SessionId = Guid.NewGuid().ToString();
                deviceInfo.LaunchCount = GetAndIncrementLaunchCount();
                deviceInfo.LastUpdateDate = GetLastUpdateDate();

                // Hardware UUID (fast and reliable)
                deviceInfo.DeviceId = HardwareIdentifierUUID.GetHardwareUUID();

                // Parallel execution of independent operations
                var processorTask = Task.Run(() => GetProcessorName());
                var memoryTask = Task.Run(() => GetTotalPhysicalMemory());
                var ipTask = Task.Run(() => GetIPAddress());
                var macTask = Task.Run(() => GetMacAddress());

                // Country lookup in background (don't wait for it)
                //var countryTask = GetCountryNameFromIPAsync();
                // Pass the already-collected fields — zero extra cost
                deviceInfo.CountryName = await CountryDetectionService.GetCountryNameAsync(
                    timeZoneId: TimeZoneInfo.Local.Id,
                    appLanguage: deviceInfo.AppLanguage,   // already set above
                    sysLanguage: deviceInfo.SystemLanguage // already set above
                );


                // Wait for hardware info (fast operations)
                await Task.WhenAll(processorTask, memoryTask, ipTask, macTask);

                deviceInfo.ProcessorName = processorTask.Result;
                deviceInfo.TotalMemory = memoryTask.Result;
                deviceInfo.IPAddress = ipTask.Result;
                deviceInfo.MacAddress = macTask.Result;

                // Try to get country, but don't block if it takes too long
                /*if (await Task.WhenAny(countryTask, Task.Delay(2000)) == countryTask)
                {
                    deviceInfo.CountryName = await countryTask;
                }
                else
                {
                    deviceInfo.CountryName = "Unknown (Timeout)";
                    Logger.Log("Country lookup timed out");
                }*/

                stopwatch.Stop();
                Logger.Log($"Device info collected in {stopwatch.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error collecting device info: {ex.Message}");
            }

            return deviceInfo;
        }

        /// <summary>
        /// ✅ NEW: Checks if app is running with administrator privileges
        /// </summary>
        private bool IsRunningAsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error checking administrator status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ✅ NEW: Gets the application architecture (x86, x64, ARM64)
        /// </summary>
        private string GetAppArchitecture()
        {
            try
            {
                // Get process architecture
                var architecture = RuntimeInformation.ProcessArchitecture;

                return architecture switch
                {
                    Architecture.X86 => "x86",
                    Architecture.X64 => "x64",
                    Architecture.Arm => "ARM",
                    Architecture.Arm64 => "ARM64",
                    _ => architecture.ToString()
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting app architecture: {ex.Message}");
                return "Unknown";
            }
        }

        /// <summary>
        /// ✅ NEW: Gets available disk space on system drive in bytes
        /// </summary>
        private long GetAvailableDiskSpace()
        {
            try
            {
                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
                DriveInfo driveInfo = new DriveInfo(systemDrive);

                if (driveInfo.IsReady)
                {
                    // Return in bytes (you can convert to GB in your model if needed)
                    return driveInfo.AvailableFreeSpace;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting available disk space: {ex.Message}");
            }

            return 0;
        }

        /// <summary>
        /// ✅ NEW: Determines if app was installed from Microsoft Store or sideloaded
        /// </summary>
        private string GetInstallationSource()
        {
            try
            {
                // Method 1: Check if running as packaged app (MSIX/AppX)
                if (IsPackagedApp())
                {
                    return "MicrosoftStore";
                }

                // Method 2: Check installation path
                string assemblyLocation = Assembly.GetExecutingAssembly().Location;

                if (assemblyLocation.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    return "MicrosoftStore";
                }
                else if (assemblyLocation.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
                {
                    return "Installer";
                }
                else
                {
                    return "Sideload";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error determining installation source: {ex.Message}");
                return "Unknown";
            }
        }

        /// <summary>
        /// Helper method to check if app is packaged (UWP/MSIX)
        /// </summary>
        private bool IsPackagedApp()
        {
            try
            {
                // For .NET 6+ and Windows App SDK
                if (AppDomain.CurrentDomain.GetData("APP_CONTEXT_BASE_DIRECTORY") is string baseDir)
                {
                    return baseDir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
                }

                // Alternative: Check for package identity
                // This requires Windows.ApplicationModel reference
                // return Windows.ApplicationModel.Package.Current != null;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ✅ NEW: Gets and increments the launch count from settings
        /// </summary>
        private int GetAndIncrementLaunchCount()
        {
            try
            {
                // Read current launch count from settings
                int currentCount = SettingsService.Current.LaunchCount;

                // Increment and save
                currentCount++;
                SettingsService.Current.LaunchCount = currentCount;
                SettingsService.Save();

                return currentCount;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting/incrementing launch count: {ex.Message}");
                return 1; // Default to 1 if error
            }
        }

        /// <summary>
        /// ✅ NEW: Gets the last update date from settings
        /// </summary>
        private DateTime? GetLastUpdateDate()
        {
            try
            {
                // Get last recorded app version
                string lastVersion = SettingsService.Current.LastAppVersion;
                string currentVersion = AppConfig.AppReleaseVersion.ToString();

                // If versions differ, this is an update
                if (!string.IsNullOrEmpty(lastVersion) && lastVersion != currentVersion)
                {
                    // Get the date when version was last changed
                    DateTime? lastUpdateDate = SettingsService.Current.LastUpdateDate;

                    // Update to current version
                    SettingsService.Current.LastAppVersion = currentVersion;
                    SettingsService.Current.LastUpdateDate = DateTime.Now;
                    SettingsService.Save();

                    return lastUpdateDate ?? DateTime.Now;
                }
                else if (string.IsNullOrEmpty(lastVersion))
                {
                    // First installation
                    SettingsService.Current.LastAppVersion = currentVersion;
                    SettingsService.Current.LastUpdateDate = DateTime.Now;
                    SettingsService.Save();

                    return null; // No previous update
                }

                // Same version, return existing update date
                return SettingsService.Current.LastUpdateDate;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting last update date: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets country name from IP with timeout protection
        /// </summary>
        public async Task<string> GetCountryNameFromIPAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(AppConfig.CountryNameAPIServiceURL);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return string.IsNullOrWhiteSpace(content) ? "Unknown" : content.Trim();
                }
                else
                {
                    Logger.Log($"API error: {response.StatusCode}");
                    return $"Unknown (API error: {response.StatusCode})";
                }
            }
            catch (TaskCanceledException)
            {
                Logger.Log("Country API request timed out");
                return "Unknown (Timeout)";
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception in GetCountryNameFromIPAsync: {ex.Message}");
                return "Unknown";
            }
        }

        /// <summary>
        /// Synchronous WMI call - no need for async wrapper
        /// </summary>
        private string GetProcessorName()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["Name"].ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting processor name: {ex.Message}");
            }

            return "Unknown";
        }

        /// <summary>
        /// Synchronous WMI call
        /// </summary>
        private string GetTotalPhysicalMemory()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        long memoryBytes = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                        double memoryGB = Math.Round(memoryBytes / (1024.0 * 1024.0 * 1024.0), 2);
                        return $"{memoryGB} GB";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting total memory: {ex.Message}");
            }

            return "Unknown";
        }

        /// <summary>
        /// Synchronous network interface query
        /// </summary>
        private string GetIPAddress()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up)
                    {
                        foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                return ip.Address.ToString();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting IP address: {ex.Message}");
            }

            return "Unknown";
        }

        /// <summary>
        /// Synchronous network interface query
        /// </summary>
        private string GetMacAddress()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up)
                    {
                        var mac = BitConverter.ToString(ni.GetPhysicalAddress().GetAddressBytes());
                        if (!string.IsNullOrEmpty(mac) && mac != "00-00-00-00-00-00")
                        {
                            return mac;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting MAC address: {ex.Message}");
            }

            return "Unknown";
        }

        /// <summary>
        /// Gets OS name from version
        /// </summary>
        public string GetOSName()
        {
            try
            {
                // ✅ Enhanced: Use Registry for accurate detection
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        var productName = key.GetValue("ProductName")?.ToString();
                        var displayVersion = key.GetValue("DisplayVersion")?.ToString();
                        var buildNumber = key.GetValue("CurrentBuild")?.ToString();

                        if (!string.IsNullOrEmpty(productName))
                        {
                            var osName = productName;
                            if (!string.IsNullOrEmpty(displayVersion))
                                osName += $" {displayVersion}";
                            if (!string.IsNullOrEmpty(buildNumber))
                                osName += $" (Build {buildNumber})";

                            return osName;
                        }
                    }
                }

                // Fallback to version detection
                var osVersion = Environment.OSVersion;
                var version = osVersion.Version;

                if (osVersion.Platform == PlatformID.Win32NT)
                {
                    if (version.Major == 10 && version.Minor == 0)
                    {
                        if (version.Build >= 22000)
                            return "Windows 11";
                        else
                            return "Windows 10";
                    }
                    else if (version.Major == 6 && version.Minor == 3)
                        return "Windows 8.1";
                    else if (version.Major == 6 && version.Minor == 2)
                        return "Windows 8";
                    else if (version.Major == 6 && version.Minor == 1)
                        return "Windows 7";
                    else if (version.Major == 5 && version.Minor == 1)
                        return "Windows XP";
                }

                return "Unknown OS";
            }
            catch (Exception ex)
            {
                Logger.Log($"Error determining OS name: {ex.Message}");
                return "Unknown OS";
            }
        }

        /// <summary>
        /// Inserts device info via API
        /// </summary>
        public async Task<bool> InsertUserDeviceInfoUsingAPIAsync(DeviceInstallationInfoVm model)
        {
            try
            {
                string jsonData = JsonSerializer.Serialize(model);

                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("secretKey", AppConfig.apiSecretKey);
                    var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await client.PostAsync(AppConfig.apiUrlProd, content);

                    if (response.IsSuccessStatusCode)
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        return true;
                    }
                    else
                    {
                        Logger.Log("API Call Failed:\n" + response.ReasonPhrase);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"An error occurred:\n: {ex.Message}");
                return false;
            }
        }
    }
}
