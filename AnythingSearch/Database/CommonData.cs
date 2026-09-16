using System.Reflection;

namespace AnythingSearch.Database;

public static class CommonData
{
    public static string ApplicationName = "Anything Search";

    /// <summary>
    /// Read from the built assembly (AnythingSearch.csproj &lt;Version&gt;) so there is a single
    /// source of truth instead of a separately hardcoded string.
    /// </summary>
    public static string ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";

    public static string EmailAddress = "shahedbddev@gmail.com";

    public static string AnythingSearchProfile = "https://zerobytebd.com/anything-search";
    public static string strNoInternet = "Please check your internet connection and try again.";

    public static string PleaseDonate = "https://www.paypal.com/donate/?hosted_button_id=63ZZ9GYNRX7HW";

    public static string apiSecretKey = "1dd92348-859b-40ba-aa60-ae8fbc156946";
    public static string apiUrlLocal = "https://localhost:5003/api/UserDeviceInfoAPI/AddNsmPlus";
    public static string apiUrlProd = "http://208.87.132.188:81/api/UserDeviceInfoAPI/AddNsmPlus";

    public static string CountryNameAPIServiceURL = "https://ipapi.co/country_name/";
}
