using Pricer.Presentation;
using Pricer.Data;

namespace Pricer.App.ViewModels;

public sealed class MainViewModel
{
    private readonly BookStore _store = new(CaptureSettings.DatabasePath);

    public MainViewModel()
    {
        Book = new BookViewModel(_store);
        Runs = new RunsViewModel(_store);
        Pnl = new PnlViewModel(_store, new SnapshotStore(CaptureSettings.DatabasePath));
        Book.RunSaved += Runs.Reload;
        Book.RunSaved += Pnl.Reload;
        Pricer.PositionRequested += Book.Add;
        Market.MarketSelected += (snapshot, underlying) =>
            Book.Use(Market.SelectedCapture!.Id, snapshot, underlying, Market.SelectedCapture.Source);
        Market.MarketSelected += Pricer.UseMarket;
        Market.MarketSelected += (snapshot, underlying) => ModelLab.Use(snapshot, underlying, Market.SelectedReport!);
        ModelLab.HestonFitted += Pricer.UseHeston;
    }

    public PricerViewModel Pricer { get; } = new();
    public MarketViewModel Market { get; } = new();
    public ModelLabViewModel ModelLab { get; } = new();
    public BookViewModel Book { get; }
    public RunsViewModel Runs { get; }
    public PnlViewModel Pnl { get; }
}
