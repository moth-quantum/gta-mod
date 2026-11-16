using System;
using System.IO;
using System.Threading;
using GTA.UI;

namespace QDay;

public static class Log
{
    static string path;
    static bool warned;
    static int scriptThread;
    static readonly object gate = new();   // Atlas worker threads log too

    // dir is Script.BaseDirectory: the folder SHVDN loaded QDay.dll from (scripts\).
    // Not Assembly.Location, which points into SHVDN's shadow-copy cache.
    public static void Init(string dir)
    {
        path = Path.Combine(dir, "QDay.log");
        scriptThread = Thread.CurrentThread.ManagedThreadId;
        Write($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
    }

    public static void Write(string s)
    {
        try { lock (gate) File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); }
        catch (Exception e)
        {
            // Say so once in game rather than swallowing it: a missing log costs far more time.
            // Only from the script thread: natives must not be called from a worker.
            if (warned || Thread.CurrentThread.ManagedThreadId != scriptThread) return;
            warned = true;
            Notification.Show($"~r~Q-DAY:~s~ cannot write log to {path} -- {e.Message}");
        }
    }
}
