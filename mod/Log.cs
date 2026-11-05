using System;
using System.IO;
using GTA.UI;

namespace QDay;

public static class Log
{
    private static string path;
    private static bool warned;

    // dir is Script.BaseDirectory: the folder SHVDN loaded QDay.dll from (scripts\).
    // Not Assembly.Location, which points into SHVDN's shadow-copy cache.
    public static void Init(string dir)
    {
        path = Path.Combine(dir, "QDay.log");
        Write($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
    }

    public static void Write(string s)
    {
        try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); }
        catch (Exception e)
        {
            // Say so once in game rather than swallowing it: a missing log costs far more time.
            if (warned) return;
            warned = true;
            Notification.Show($"~r~Q-DAY:~s~ cannot write log to {path} -- {e.Message}");
        }
    }
}
