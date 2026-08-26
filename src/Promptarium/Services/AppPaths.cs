using System.IO;

namespace Promptarium.Services;

public static class AppPaths
{
    public static string AppDataDirectory
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Promptarium");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string DatabasePath => Path.Combine(AppDataDirectory, "promptarium.db");
    public static string BackupDirectory => Path.Combine(AppDataDirectory, "backups");
    public static string DiagnosticsDirectory => Path.Combine(AppDataDirectory, "logs");
    public static string DiagnosticsPath => Path.Combine(DiagnosticsDirectory, "promptarium.log");
}
