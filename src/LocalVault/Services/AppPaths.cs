namespace LocalVault.Services;

public static class AppPaths
{
    /// <summary>
    /// %LocalAppData%\LocalVault by default. The LOCALVAULT_HOME environment variable overrides it
    /// (useful for a portable install or for testing against a throwaway vault).
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            string? overridePath = Environment.GetEnvironmentVariable("LOCALVAULT_HOME");
            return !string.IsNullOrWhiteSpace(overridePath)
                ? Path.GetFullPath(overridePath)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalVault");
        }
    }
}
