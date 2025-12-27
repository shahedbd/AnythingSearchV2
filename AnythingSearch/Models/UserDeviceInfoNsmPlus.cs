namespace AnythingSearch.Models

{
    public class UserDeviceInfoNsmPlus
    {
        public Int64 Id { get; set; }
        public string UserName { get; set; }
        public string MachineName { get; set; }
        public string OSVersion { get; set; }
        public string OSName { get; set; }
        public string ProcessorName { get; set; }
        public string TotalMemory { get; set; }
        public string IPAddress { get; set; }
        public string MacAddress { get; set; }
        public string ScreenResolution { get; set; }
        public string DotNetVersion { get; set; }
        public string DeviceUniqueKey { get; set; }
        public string TimeZone { get; set; }
        public string CountryName { get; set; }
        public string AppName { get; set; }
        public string AppVersion { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
