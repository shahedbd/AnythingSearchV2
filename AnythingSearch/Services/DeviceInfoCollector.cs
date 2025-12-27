using AnythingSearch.Database;
using AnythingSearch.Models;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;


namespace AnythingSearch.Services
{
    public class DeviceInfoCollector
    {
        public async Task<UserDeviceInfoNsmPlus> CollectDeviceInfoAsync()
        {
            var deviceInfo = new UserDeviceInfoNsmPlus();

            try
            {
                deviceInfo.UserName = WindowsIdentity.GetCurrent().Name;
                deviceInfo.MachineName = Environment.MachineName;
                deviceInfo.OSVersion = Environment.OSVersion.ToString();
                deviceInfo.OSName = GetOSName();
                deviceInfo.DotNetVersion = Environment.Version.ToString();

                deviceInfo.ProcessorName = await GetProcessorNameAsync();
                deviceInfo.TotalMemory = await GetTotalPhysicalMemoryAsync();
                deviceInfo.IPAddress = await GetIPAddressAsync();
                deviceInfo.MacAddress = await GetMacAddressAsync();

                deviceInfo.ScreenResolution = $"{Screen.PrimaryScreen.Bounds.Width}x{Screen.PrimaryScreen.Bounds.Height}";
                deviceInfo.DeviceUniqueKey = GenerateDeviceUniqueKey(deviceInfo);

                deviceInfo.CreatedDate = DateTime.Now;
                deviceInfo.TimeZone = TimeZoneInfo.Local.StandardName;
                deviceInfo.CountryName = await GetCountryNameFromIPAsync();
                deviceInfo.AppName = CommonData.ApplicationName;
                deviceInfo.AppVersion = CommonData.ApplicationVersion;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error collecting device info: {ex.Message}");
            }

            return deviceInfo;
        }
        public async Task<string> GetCountryNameFromIPAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetAsync(CommonData.CountryNameAPIServiceURL);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        return string.IsNullOrWhiteSpace(content) ? "Unknown" : content.Trim();
                    }
                    else
                    {
                        Debug.WriteLine($"API error: {response.StatusCode}");
                        return "Unknown, " + $"API error: {response.StatusCode}";
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in GetCountryNameFromIPAsync: {ex.Message}");
                return "Unknown";
            }
        }


        private async Task<string> GetProcessorNameAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("select Name from Win32_Processor"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            return obj["Name"].ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error getting processor name: {ex.Message}");
                }

                return "Unknown";
            });
        }

        private async Task<string> GetTotalPhysicalMemoryAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("select TotalPhysicalMemory from Win32_ComputerSystem"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            long memoryBytes = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                            return $"{memoryBytes / (1024 * 1024 * 1024)} GB";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error getting total memory: {ex.Message}");
                }

                return "Unknown";
            });
        }

        private async Task<string> GetIPAddressAsync()
        {
            return await Task.Run(() =>
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
                    Debug.WriteLine($"Error getting IP address: {ex.Message}");
                }

                return "Unknown";
            });
        }

        private async Task<string> GetMacAddressAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus == OperationalStatus.Up)
                        {
                            return BitConverter.ToString(ni.GetPhysicalAddress().GetAddressBytes());
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error getting MAC address: {ex.Message}");
                }

                return "Unknown";
            });
        }

        private string GenerateDeviceUniqueKey(UserDeviceInfoNsmPlus deviceInfo)
        {
            try
            {
                // Combine key components
                var keyComponents = $"{deviceInfo.MachineName}-{deviceInfo.ProcessorName}-{deviceInfo.MacAddress}";

                // Hash the combined string for uniqueness
                using (var sha256 = SHA256.Create())
                {
                    var bytes = Encoding.UTF8.GetBytes(keyComponents);
                    var hashBytes = sha256.ComputeHash(bytes);

                    // Convert hash to a readable string
                    var sb = new StringBuilder();
                    foreach (var b in hashBytes)
                    {
                        sb.Append(b.ToString("x2"));
                    }

                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error generating device unique key: {ex.Message}");
            }

            return "Unknown";
        }
        private string GetOSName()
        {
            try
            {
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
                Debug.WriteLine($"Error determining OS name: {ex.Message}");
                return "Unknown OS";
            }
        }

        public async Task<bool> InsertUserDeviceInfoUsingAPIAsync(UserDeviceInfoNsmPlus model)
        {
            try
            {
                string jsonData = JsonSerializer.Serialize(model);
                using (HttpClient client = new())
                {
                    client.DefaultRequestHeaders.Add("secretKey", CommonData.apiSecretKey);
                    var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await client.PostAsync(CommonData.apiUrlProd, content);

                    if (response.IsSuccessStatusCode)
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        return true;
                    }
                    else
                    {
                        Console.WriteLine("API Call Failed:\n" + response.ReasonPhrase);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred:\n: {ex.Message}");
                return false;
            }
        }
    }
}
