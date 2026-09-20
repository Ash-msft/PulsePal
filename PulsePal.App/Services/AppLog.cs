using System.Diagnostics;

namespace PulsePal.App.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulsePal");
    public static void Write(string operation, Exception exception)
    {
        Trace.TraceError("{0}: {1}", operation, exception);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {operation}: {exception}{Environment.NewLine}");
            }
        }
        catch (Exception logError)
        {
            Trace.TraceError("Unable to write PulsePal diagnostic log: {0}", logError);
        }
    }
}
