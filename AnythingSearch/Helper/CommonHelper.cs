using System.Diagnostics;

namespace AnythingSearch.Helper
{
    public static class CommonHelper
    {
        public static async Task<bool> StartProcessAsync(string _FileName, int delayMs = 0)
        {
            // Validation
            if (string.IsNullOrWhiteSpace(_FileName))
            {
                Logger.Log("StartProcess: Empty filename provided");
                return false;
            }

            try
            {
                // Optional delay to prevent multiple tabs opening simultaneously
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }

                var _ProcessStartInfo = new ProcessStartInfo
                {
                    FileName = _FileName,
                    UseShellExecute = true,
                    ErrorDialog = false // Don't show error dialogs to user
                };

                Logger.Log($"Starting process: {_FileName}");

                // Process.Start is already non-blocking for external processes
                var process = Process.Start(_ProcessStartInfo);

                if (process == null)
                {
                    Logger.Log($"Failed to start process: {_FileName}");
                    return false;
                }

                Logger.Log($"Successfully started process: {_FileName}");
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // Common errors: No default browser, file not found, access denied
                Logger.Log($"Win32Exception starting process '{_FileName}': {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                // Catch any other exceptions
                Logger.Log($"Exception starting process '{_FileName}': {ex.Message}");
                return false;
            }
        }
        [System.Runtime.InteropServices.DllImport("wininet.dll")]
        private extern static bool InternetGetConnectedState(out int Description, int ReservedValue);
        public static bool CheckNet()
        {
            int desc;
            return InternetGetConnectedState(out desc, 0);
        }
    }
}
