// One playthrough's lockdown: request it when the riot starts, keep the result whenever it
// arrives, settle on a layout when the escape begins. No game types, so tests/LockdownCheck runs it.
//
// The job name changes every run, so a job still running from an aborted run can't land in the
// next one. A missing, failed or late result means the recorded bank, never a stall.

using System;

namespace QDay;

public sealed class LockdownRun(DistrictGraph graph)
{
    int runs;
    AtlasResult arrived;
    bool certified;
    string qdayBackend;

    public DistrictGraph Graph => graph;
    public string JobName { get; private set; }
    public LockdownLayout Layout { get; private set; }

    public void Reset()
    {
        JobName = null;
        arrived = null;
        certified = false;
        qdayBackend = null;
        Layout = null;
    }

    /// <summary>Call as the riot starts. Without Q-Day numbers (F6, F7, the recorded Q-Day result) no job is sent and the escape uses the bank.</summary>
    public void Submit(Atlas atlas, AtlasResult qday)
    {
        Reset();
        if (graph is null) return;
        certified = qday.Certified;
        qdayBackend = qday.Backend;
        if (qday.Ints is not { Count: > 0 })
        {
            Log.Write("lockdown: no Q-Day numbers on this path, the escape will use the recorded bank");
            return;
        }
        JobName = $"lockdown{++runs}";
        atlas.Lockdown(JobName, Lockdown.BuildParams(graph, qday.Ints));
    }

    /// <summary>Call for every lockdown result Atlas hands over, whatever the phase.</summary>
    public void Store(AtlasResult r)
    {
        if (r.Name != JobName) Log.Write($"lockdown: ignoring {r.Name} from an earlier run: {r}");
        else if (Layout != null) Log.Write($"lockdown: {r.Name} arrived after the escape began, staying on the layout in use: {r}");
        else if (r.Status != "done") Log.Write($"lockdown: {r.Name} failed, the escape will use the recorded bank: {r}");
        else
        {
            arrived = r;
            Log.Write($"lockdown: {r.Name} stored: {r}");
        }
    }

    /// <summary>Settles the layout. False only when the district file failed to load, and then there is no lockdown to run.</summary>
    public bool Begin()
    {
        if (graph is null)
        {
            Log.Write("lockdown: no district file, skipping the escape");
            return false;
        }
        var r = arrived;
        if (r is null)
        {
            Log.Write("lockdown: no result in hand, using the recorded bank");
            try { r = Atlas.RecordedLockdown(); }
            catch (Exception e)
            {
                Log.Write($"lockdown: the recorded bank failed: {e.Message}");
                r = new() { FromBank = true };
            }
        }
        Layout = Lockdown.Choose(graph, r.Measurements);
        Layout.JobId = r.JobId;
        Layout.Backend = r.Backend;
        Layout.TargetSource = Lockdown.TargetSource(r.FromBank, certified, qdayBackend);
        foreach (var line in Lockdown.LogLines(graph, Layout)) Log.Write(line);
        return true;
    }
}
