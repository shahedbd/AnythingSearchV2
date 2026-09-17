namespace DeviceDataModule;

using System.Management;
using System.Security.Cryptography;
using System.Text;

public class HardwareIdentifierUUID
{
    /// <summary>
    /// Gets hardware UUID (Universally Unique Identifier)
    /// This is the MOST reliable method - built into motherboard firmware
    /// </summary>
    public static string GetHardwareUUID()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    var uuid = obj["UUID"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(uuid))
                    {
                        return uuid.ToLower();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to get hardware UUID: {ex.Message}", ex);
        }

        throw new Exception("Hardware UUID not found");
    }

    /// <summary>
    /// Gets hashed UUID for privacy
    /// </summary>
    public static string GetHashedUUID()
    {
        var uuid = GetHardwareUUID();
        using (var sha256 = SHA256.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(uuid);
            var hashBytes = sha256.ComputeHash(bytes);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }
    }
}
