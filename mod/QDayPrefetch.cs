// The Q-Day job, started early so the player never waits on IBM hardware.
//
// BeginPitch sends comet-qrng-v1 under a name unique to this run ("qday-<n>"). It runs
// on an Atlas background thread through the heists and deliveries. Whenever it lands,
// in whatever phase, OnTick hands it to CatchQDayResult, which keeps it in liveQDay.
// Threshold then lasts a fixed ThresholdSeconds and starts Q-Day on liveQDay if it is
// there, or on a random recorded run from QDayLibrary if not. BeginIdle clears liveQDay
// and bumps the run number, so a late result from an aborted run is ignored.

namespace QDay;

public partial class QDayScript
{
    const double ThresholdSeconds = 10;

    int qdayRun;                       // bumped in BeginIdle; names this run's job
    AtlasResult liveQDay;              // this run's Q-Day job result, once it has arrived

    string QDayJobName => $"qday-{qdayRun}";

    /// <summary>Called from BeginPitch.</summary>
    void SendQDayJob()
    {
        liveQDay = null;
        atlas.QDay(QDayJobName);
        Log.Write($"qday: job {QDayJobName} sent at the pitch ({(atlas.Offline ? "offline" : "live")})");
    }

    /// <summary>Called from OnTick with whatever Atlas result arrived this tick.</summary>
    void CatchQDayResult(AtlasResult r)
    {
        if (r is not { Kind: "qday" }) return;
        if (r.Name != QDayJobName) { Log.Write($"qday: ignoring {r.Name}, this run is {QDayJobName}"); return; }
        if (r.Status == "done")
        {
            liveQDay = r;
            Log.Write($"qday: {r.Name} arrived during {phase}, job={r.JobId} backend={r.Backend} certified={r.Certified}");
        }
        else
        {
            liveQDay = null;
            Log.Write($"qday: {r.Name} failed during {phase}: {r.Message}. Threshold will use the library.");
        }
    }

    /// <summary>Called from BeginIdle: aborts, failures and restarts never carry a result over.</summary>
    void ResetQDayRun()
    {
        liveQDay = null;
        qdayRun++;
    }

    /// <summary>The result Q-Day runs on when Threshold ends.</summary>
    AtlasResult ThresholdResult()
    {
        if (liveQDay != null)
        {
            Log.Write($"threshold: using {liveQDay.Name} ({(atlas.Offline ? "offline library pick" : "live IBM run")}) job={liveQDay.JobId}");
            return liveQDay;
        }
        Log.Write($"threshold: {QDayJobName} not back yet, using a library pick");
        return QDayLibrary.Pick();
    }
}
