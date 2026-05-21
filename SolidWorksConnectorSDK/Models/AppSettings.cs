namespace SolidWorksConnectorSDK.Models
{
    public class AppSettings
    {
        public SolidWorksSettings SolidWorks { get; set; } = new SolidWorksSettings();
    }

    public class SolidWorksSettings
    {
        public string InputDirectory { get; set; }
        public string OutputDirectory { get; set; }
        public int ServerPort { get; set; } = 5050;
    }
}
