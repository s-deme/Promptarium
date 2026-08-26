using System.Text;
using System.IO;

namespace Promptarium.Services;

public static class AppLogger
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string context, Exception exception) => Write("ERROR", context, exception);

    public static string ReadRecent(int maxCharacters = 16000)
    {
        try
        {
            var path = AppPaths.DiagnosticsPath;
            if (!File.Exists(path)) return "診断ログはまだありません。";
            var content = File.ReadAllText(path, Encoding.UTF8);
            return content.Length <= maxCharacters ? content : content[^maxCharacters..];
        }
        catch (Exception exception)
        {
            return $"診断ログを読み取れません: {exception.Message}";
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
            var entry = new StringBuilder()
                .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
                .Append(level).Append(' ').Append(message).AppendLine();
            if (exception is not null) entry.AppendLine(exception.ToString());
            lock (Gate)
            {
                File.AppendAllText(AppPaths.DiagnosticsPath, entry.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never create a second failure path.
        }
    }
}
