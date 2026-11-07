using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Pricer.Presentation;
using Pricer.App.ViewModels;
using Pricer.Book;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.App;

public partial class MainWindow : Window
{
    private const int MeshSize = 40;
    private const double MaxLogMoneyness = 0.4;
    private const double HeightScale = 3;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        viewModel.Pricer.ProfileChanged += ShowProfile;
        viewModel.Market.SmileChanged += ShowSmile;
        viewModel.Market.SurfaceChanged += ShowSurface;
        viewModel.ModelLab.FitChanged += ShowModels;
        viewModel.Pnl.WaterfallChanged += ShowWaterfall;
        DataContext = viewModel;
        viewModel.Market.Reload();
    }

    private void ShowProfile(ProfileData profile)
    {
        var plot = ProfilePlot.Plot;
        plot.Clear();
        plot.Add.ScatterLine(profile.Spots, profile.Values).LineWidth = 2;
        plot.Title("PV vs spot");
        plot.XLabel("Spot");
        plot.YLabel("PV");
        plot.Axes.AutoScale();
        ProfilePlot.Refresh();
    }

    private void ShowSmile(SliceReport report)
    {
        var slice = report.Slice;
        double[] strikes = [.. report.Points.Select(p => p.Strike)];
        double[] market = [.. report.Points.Select(p => p.Vol * 100)];
        double[] fit = [.. strikes.Select(k => Math.Sqrt(slice.TotalVariance(Math.Log(k / slice.Forward)) / slice.T) * 100)];

        var plot = SmilePlot.Plot;
        plot.Clear();
        var points = plot.Add.ScatterPoints(strikes, market);
        points.LegendText = "Market";
        var line = plot.Add.ScatterLine(strikes, fit);
        line.LineWidth = 2;
        line.LegendText = "SVI";
        plot.ShowLegend();
        plot.Title($"Smile {report.Expiry:yyyy-MM-dd}");
        plot.XLabel("Strike");
        plot.YLabel("Implied vol (%)");
        plot.Axes.AutoScale();
        SmilePlot.Refresh();
    }

    private void ShowModels(SliceFit fit)
    {
        double[] strikes = [.. fit.Points.Select(p => p.Strike)];
        var plot = ModelPlot.Plot;
        plot.Clear();
        var points = plot.Add.ScatterPoints(strikes, [.. fit.Points.Select(p => p.Market * 100)]);
        points.LegendText = "Market";
        foreach (var (name, values) in new (string, IEnumerable<double>)[]
        {
            ("SVI", fit.Points.Select(p => p.Svi)),
            ("Heston", fit.Points.Select(p => p.Heston)),
            ("Local vol (PDE)", fit.Points.Select(p => p.LocalVol)),
        })
        {
            var line = plot.Add.ScatterLine(strikes, [.. values.Select(v => v * 100)]);
            line.LineWidth = 2;
            line.LegendText = name;
        }
        plot.ShowLegend();
        plot.Title($"Smile {fit.Expiry:yyyy-MM-dd}");
        plot.XLabel("Strike");
        plot.YLabel("Implied vol (%)");
        plot.Axes.AutoScale();
        ModelPlot.Refresh();
    }

    /// <summary>Each bar floats from the cumulated PnL before its factor to the one after it.</summary>
    private void ShowWaterfall(PnlRow row)
    {
        var plot = PnlPlot.Plot;
        plot.Clear();
        var bars = new List<ScottPlot.Bar>();
        var ticks = new List<ScottPlot.Tick>();
        double level = 0;
        int position = 0;
        foreach (var factor in Enum.GetValues<PnlFactor>())
        {
            double amount = row[factor];
            bars.Add(new ScottPlot.Bar
            {
                Position = position,
                ValueBase = level,
                Value = level + amount,
                FillColor = amount >= 0 ? ScottPlot.Colors.SteelBlue : ScottPlot.Colors.IndianRed,
            });
            ticks.Add(new ScottPlot.Tick(position++, factor.ToString()));
            level += amount;
        }
        bars.Add(new ScottPlot.Bar { Position = position, ValueBase = 0, Value = level, FillColor = ScottPlot.Colors.DimGray });
        ticks.Add(new ScottPlot.Tick(position, "Total"));

        plot.Add.Bars(bars);
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual([.. ticks]);
        plot.Title($"Waterfall: {row.Key}");
        plot.YLabel("PnL");
        plot.Axes.AutoScale();
        PnlPlot.Refresh();
    }

    private void ShowSurface(SviVolSurface surface)
    {
        double minT = surface.Slices[0].T;
        double maxT = surface.Slices[^1].T;
        var forward = surface.Slices[0].Forward;
        var vols = new double[MeshSize, MeshSize];
        for (int i = 0; i < MeshSize; i++)
        {
            double t = minT + (maxT - minT) * i / (MeshSize - 1.0);
            for (int j = 0; j < MeshSize; j++)
            {
                double k = -MaxLogMoneyness + 2 * MaxLogMoneyness * j / (MeshSize - 1.0);
                vols[i, j] = surface.Vol(forward * Math.Exp(k), t);
            }
        }

        double low = vols.Cast<double>().Min();
        double high = vols.Cast<double>().Max();
        var mesh = new MeshGeometry3D();
        for (int i = 0; i < MeshSize; i++)
        {
            for (int j = 0; j < MeshSize; j++)
            {
                double u = (vols[i, j] - low) / Math.Max(high - low, 1e-9);
                mesh.Positions.Add(new Point3D(j / (MeshSize - 1.0) * 4 - 2, i / (MeshSize - 1.0) * 4 - 2, vols[i, j] * HeightScale / high));
                mesh.TextureCoordinates.Add(new Point(u, 0));
            }
        }
        for (int i = 0; i < MeshSize - 1; i++)
        {
            for (int j = 0; j < MeshSize - 1; j++)
            {
                int a = i * MeshSize + j;
                int b = a + 1;
                int c = a + MeshSize;
                int d = c + 1;
                foreach (int index in new[] { a, b, d, a, d, c }) mesh.TriangleIndices.Add(index);
            }
        }

        var brush = new LinearGradientBrush(Colors.SteelBlue, Colors.Goldenrod, new Point(0, 0), new Point(1, 0));
        var material = new DiffuseMaterial(brush);
        SurfaceModel.Content = new GeometryModel3D(mesh, material) { BackMaterial = material };
        SurfaceCaption.Text = $"X: ln(K/F) ±{MaxLogMoneyness} · Y: maturity {minT:F2} to {maxT:F2} y · Z: vol {low:P1} to {high:P1}";
        SurfaceView.ZoomExtents();
    }
}
