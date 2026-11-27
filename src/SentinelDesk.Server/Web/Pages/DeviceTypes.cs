namespace SentinelDesk.Server.Web.Pages;

/// <summary>One point of the device page's live history (heartbeat or sub-second stream sample).</summary>
public sealed record HistPoint(string T, double In, double Out, double Cpu, double Ram);

/// <summary>The sub-second live stream is running at this sample interval.</summary>
public sealed record StreamState(int IntervalMs);
