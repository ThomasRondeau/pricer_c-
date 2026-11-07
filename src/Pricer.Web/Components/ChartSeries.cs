namespace Pricer.Web.Components;

/// <summary>One line, or one set of markers when <paramref name="Markers"/> is set. Non-finite values leave a gap.</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<double> X, IReadOnlyList<double> Y, bool Markers = false);
