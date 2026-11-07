using System.Collections.Immutable;
using Pricer.Engines;
using Pricer.Instruments;

namespace Pricer.Book;

public enum BookGrouping
{
    Total,
    Underlying,
    Product,
    Tag,
    Source,
}

/// <summary>Deltas and gammas of different underlyings only add up in cash: delta times spot, and gamma as
/// the change of cash delta for a 1% spot move. Vega per vol point, theta per day, rho per rate point.</summary>
/// <param name="StandardError">In quadrature, so understated when trades share seeds and move together.</param>
/// <param name="ModelGreekTrades">Trades whose greeks are not all in market convention, and so not strictly additive.</param>
public sealed record BookRow(
    string Key, int Trades, int Failed, int ModelGreekTrades, double Pv, double StandardError, double Realised,
    double CashDelta, double CashGamma, double Vega, double Theta, double Rho);

public static class BookAggregator
{
    public static ImmutableArray<BookRow> Group(IEnumerable<TradeResult> trades, BookGrouping grouping) =>
        [.. trades.GroupBy(t => Key(t.Position, grouping)).OrderBy(g => g.Key).Select(g => Row(g.Key, [.. g]))];

    public static string Key(Position position, BookGrouping grouping) => grouping switch
    {
        BookGrouping.Underlying => string.Join("+", position.Instrument.Underlyings),
        BookGrouping.Product => position.Instrument.GetType().Name,
        BookGrouping.Tag => position.Tag.Length == 0 ? "(no tag)" : position.Tag,
        BookGrouping.Source => position.Source,
        _ => "Book",
    };

    private static BookRow Row(string key, List<TradeResult> trades)
    {
        var priced = trades.Where(t => t.Result is not null).ToList();
        double cashDelta = 0, cashGamma = 0;
        foreach (var trade in priced)
        {
            foreach (var greek in trade.Result!.Greeks.Where(g => g.Measure is Measure.Delta or Measure.Gamma))
            {
                string name = greek.Underlying ?? trade.Position.Instrument.Underlying;
                double spot = trade.Market.First(p => p.Underlying == name).Spot;
                if (greek.Measure == Measure.Delta) cashDelta += greek.Value * spot;
                else cashGamma += greek.Value * spot * spot / 100;
            }
        }

        double Sum(Measure measure) =>
            priced.Sum(t => t.Result!.Greeks.Where(g => g.Measure == measure).Sum(g => g.Value));

        return new BookRow(
            key, trades.Count, trades.Count - priced.Count,
            priced.Count(t => t.Result!.Greeks.Any(g => g.Convention == GreekConvention.Model)),
            priced.Sum(t => t.Result!.Pv),
            Math.Sqrt(priced.Sum(t => Math.Pow(t.Result!.PvStandardError ?? 0, 2))),
            priced.Sum(t => t.Result!.Realised),
            cashDelta, cashGamma, Sum(Measure.Vega), Sum(Measure.Theta), Sum(Measure.Rho));
    }
}
