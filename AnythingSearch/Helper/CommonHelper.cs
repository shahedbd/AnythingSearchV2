using System.Diagnostics;

namespace AnythingSearch.Helper
{
    public static class CommonHelper
    {
        public static Icon LoadApplicationIcon()
        {
            try
            {
                // Method 1: Try to load from file system
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "favicon.ico");
                if (File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }

                // Method 2: Try to load from embedded resources
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = "AnythingSearch.Resources.favicon.ico";
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    return new Icon(stream);
                }

                // Method 3: Try alternative resource names
                var resourceNames = assembly.GetManifestResourceNames();
                var iconResource = resourceNames.FirstOrDefault(r => r.EndsWith("favicon.ico"));
                if (iconResource != null)
                {
                    using var altStream = assembly.GetManifestResourceStream(iconResource);
                    if (altStream != null)
                    {
                        return new Icon(altStream);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load icon: {ex.Message}");
            }

            // Fallback to system icon
            return SystemIcons.Application;
        }
        [System.Runtime.InteropServices.DllImport("wininet.dll")]
        private extern static bool InternetGetConnectedState(out int Description, int ReservedValue);
        public static bool CheckNet()
        {
            int desc;
            return InternetGetConnectedState(out desc, 0);
        }
        public static async Task StartProcessAsync(string _FileName)
        {
            var _ProcessStartInfo = new ProcessStartInfo
            {
                FileName = _FileName,
                UseShellExecute = true
            };

            await Task.Run(() => Process.Start(_ProcessStartInfo));
        }
    }
}
