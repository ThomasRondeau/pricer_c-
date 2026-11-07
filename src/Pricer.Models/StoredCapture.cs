using System.Collections.Immutable;
using Pricer.Market;

namespace Pricer.Models;

/// <summary>A captured market with the calibration report of each underlying.</summary>
public sealed record StoredCapture(MarketSnapshot Snapshot, ImmutableArray<CalibrationReport> Reports);

/// <summary>One line of a static capture catalogue, as shipped with the web demo.</summary>
public sealed record CaptureIndexEntry(string File, string Label, DateOnly AsOf, string Source, ImmutableArray<string> Underlyings);
