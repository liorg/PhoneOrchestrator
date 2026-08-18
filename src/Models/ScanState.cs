using System.Collections.Concurrent;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// In-memory view of the last sweep. Singleton, read by the dashboard.
/// Deliberately not persisted: Swarm reschedules this service and a fresh
/// instance should start counting failures from zero rather than inherit
/// a stale verdict.
/// </summary>
public sealed class ScanState
{
    private readonly ConcurrentDictionary<Guid, ProbeSnapshot> _hosts = new();
    private readonly ConcurrentQueue<DrainRecord> _drains = new();
    private const int MaxDrainHistory = 50;

    public DateTime? LastScanUtc { get; private set; }
    public int       ScanCount   { get; private set; }
    public string?   LastError   { get; set; }

    public void Record(ProbeSnapshot snap) => _hosts[snap.HostId] = snap;

    public void CompleteScan()
    {
        LastScanUtc = DateTime.UtcNow;
        ScanCount++;
    }

    public void RecordDrain(DrainRecord rec)
    {
        _drains.Enqueue(rec);
        while (_drains.Count > MaxDrainHistory) _drains.TryDequeue(out _);
    }

    public ProbeSnapshot? Get(Guid hostId) =>
        _hosts.TryGetValue(hostId, out var s) ? s : null;

    public IReadOnlyCollection<ProbeSnapshot> All() => _hosts.Values.ToList();

    public IReadOnlyList<DrainRecord> RecentDrains() =>
        _drains.Reverse().ToList();
}
