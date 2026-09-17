namespace DeviceDataModule
{
    public class DeviceInstallationInfoVm
    {
        public Int64 Id { get; set; }
        public string DeviceId { get; set; }
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
        public string TimeZone { get; set; }
        public string CountryName { get; set; }
        public string AppName { get; set; }
        public string AppVersion { get; set; }
        public bool IsNewInstallation { get; set; }
        public bool UserConsentGiven { get; set; }

        public string InstallationSource { get; set; }  
        public string AppLanguage { get; set; }        
        public string SystemLanguage { get; set; } 
        public bool IsAdministrator { get; set; }    
        public string AppArchitecture { get; set; }     
        public long AvailableDiskSpace { get; set; }   
        public string SessionId { get; set; }       
        public int LaunchCount { get; set; }          
        public DateTime? LastUpdateDate { get; set; } 
        public DateTime CreatedDate { get; set; }
    }
}
