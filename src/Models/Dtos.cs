namespace PhoneOrchestrator.Models;

/// <summary>One host as the orchestrator last saw it.</summary>
public sealed record ProbeSnapshot(
    Guid     HostId,
    string   HostName,
    string?  IpAddress,
    bool     Reachable,
    int?     StatusCode,
    string?  Error,
    long     ElapsedMs,
    int      ConsecutiveFailures,
    bool     DrainPending,
    DateTime CheckedAtUtc);

/// <summary>Result of one drain attempt, kept for the activity feed.</summary>
public sealed record DrainRecord(
    Guid     HostId,
    string   HostName,
    int      Moved,
    int      Failed,
    string?  Error,
    DateTime AtUtc);

/// <summary>
/// HostAgent's heartbeat payload. Every field is optional on purpose - the
/// orchestrator treats HTTP 200 as the health signal and uses the body only
/// to enrich the dashboard, so a shape change upstream cannot break the loop.
/// </summary>
public sealed class HeartbeatPayload
{
    public bool?    Healthy        { get; set; }
    public bool?    IsStale        { get; set; }
    public string?  HostName       { get; set; }
    public decimal? CpuPercent     { get; set; }
    public int?     RamTotalMb     { get; set; }
    public int?     RamUsedMb      { get; set; }
    public int?     DiskTotalGb    { get; set; }
    public int?     DiskUsedGb     { get; set; }
    public int?     PhoneCount     { get; set; }
    public int?     ContainerCount { get; set; }
}

public static class BuildInfo
{
    public const string Version = "1.0.1";
    /// <summary>Unique per build - the reliable way to confirm what Swarm is actually running.</summary>
    public const string Marker  = "orch-2026-08-18-a";
}
