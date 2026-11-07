using Pricer.Instruments;
using Pricer.Models;

namespace Pricer.Engines;

public sealed class EngineRegistry
{
    private readonly Dictionary<(Type Instrument, Type Model, Type Config), IPricingEngine> _engines = [];

    public static EngineRegistry Default()
    {
        var analytic = new BlackScholesAnalyticEngine();
        var pde = new FiniteDifferenceEngine();
        var monteCarlo = new BlackScholesMonteCarloEngine();
        var simulation = new PathSimulationEngine();
        return new EngineRegistry()
            .Register<Stock, BlackScholesConfig, AnalyticEngineConfig>(analytic)
            .Register<EquityForward, BlackScholesConfig, AnalyticEngineConfig>(analytic)
            .Register<EuropeanOption, BlackScholesConfig, AnalyticEngineConfig>(analytic)
            .Register<DigitalOption, BlackScholesConfig, AnalyticEngineConfig>(analytic)
            .Register<BarrierOption, BlackScholesConfig, AnalyticEngineConfig>(analytic)
            .Register<EuropeanOption, BlackScholesConfig, PdeEngineConfig>(pde)
            .Register<AmericanOption, BlackScholesConfig, PdeEngineConfig>(pde)
            .Register<DigitalOption, BlackScholesConfig, PdeEngineConfig>(pde)
            .Register<BarrierOption, BlackScholesConfig, PdeEngineConfig>(pde)
            .Register<EuropeanOption, BlackScholesConfig, MonteCarloEngineConfig>(monteCarlo)
            .Register<DigitalOption, BlackScholesConfig, MonteCarloEngineConfig>(monteCarlo)
            .Register<BarrierOption, BlackScholesConfig, MonteCarloEngineConfig>(monteCarlo)
            .Register<AsianOption, BlackScholesConfig, MonteCarloEngineConfig>(monteCarlo)
            .Register<ReverseConvertible, BlackScholesConfig, MonteCarloEngineConfig>(monteCarlo)
            .Register<Autocall, BlackScholesConfig, MonteCarloEngineConfig>(monteCarlo)
            .Register<Phoenix, BlackScholesConfig, MonteCarloEngineConfig>(monteCarlo)
            .Register<EuropeanOption, LocalVolConfig, PdeEngineConfig>(pde)
            .Register<AmericanOption, LocalVolConfig, PdeEngineConfig>(pde)
            .Register<DigitalOption, LocalVolConfig, PdeEngineConfig>(pde)
            .Register<BarrierOption, LocalVolConfig, PdeEngineConfig>(pde)
            .Register<EuropeanOption, LocalVolConfig, MonteCarloEngineConfig>(simulation)
            .Register<DigitalOption, LocalVolConfig, MonteCarloEngineConfig>(simulation)
            .Register<BarrierOption, LocalVolConfig, MonteCarloEngineConfig>(simulation)
            .Register<AsianOption, LocalVolConfig, MonteCarloEngineConfig>(simulation)
            .Register<ReverseConvertible, LocalVolConfig, MonteCarloEngineConfig>(simulation)
            .Register<Autocall, LocalVolConfig, MonteCarloEngineConfig>(simulation)
            .Register<Phoenix, LocalVolConfig, MonteCarloEngineConfig>(simulation)
            .Register<WorstOfPhoenix, LocalVolConfig, MonteCarloEngineConfig>(simulation)
            .Register<EuropeanOption, HestonConfig, AnalyticEngineConfig>(new HestonAnalyticEngine())
            .Register<EuropeanOption, HestonConfig, MonteCarloEngineConfig>(simulation)
            .Register<DigitalOption, HestonConfig, MonteCarloEngineConfig>(simulation)
            .Register<BarrierOption, HestonConfig, MonteCarloEngineConfig>(simulation)
            .Register<AsianOption, HestonConfig, MonteCarloEngineConfig>(simulation)
            .Register<ReverseConvertible, HestonConfig, MonteCarloEngineConfig>(simulation)
            .Register<Autocall, HestonConfig, MonteCarloEngineConfig>(simulation)
            .Register<Phoenix, HestonConfig, MonteCarloEngineConfig>(simulation);
    }

    public EngineRegistry Register<TInstrument, TModel, TConfig>(IPricingEngine engine)
        where TInstrument : Instrument
        where TModel : ModelConfig
        where TConfig : EngineConfig
    {
        _engines[(typeof(TInstrument), typeof(TModel), typeof(TConfig))] = engine;
        return this;
    }

    public IPricingEngine Resolve(Instrument instrument, ModelConfig model, EngineConfig config) =>
        _engines.TryGetValue((instrument.GetType(), model.GetType(), config.GetType()), out var engine)
            ? engine
            : throw new NotSupportedException(
                $"No engine for {instrument.GetType().Name} under {model.GetType().Name} with {config.GetType().Name}.");

    /// <summary>Engine configurations available for a product, for the screen's engine choice.</summary>
    public IReadOnlyList<Type> ConfigsFor(Type instrument, Type model) =>
        [.. _engines.Keys.Where(k => k.Instrument == instrument && k.Model == model).Select(k => k.Config)];
}
