using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Análisis ICM posterior de tus all-in en MTT/SNG: fichas frente a premio (solo fuera de juego).</summary>
public sealed class IcmWindow : Window
{
    readonly AppServices _svc;
    readonly TextBox _payouts = new() { Text = "50, 30, 20", Width = 160, Margin = new Thickness(0, 0, 12, 0) };
    readonly DataGrid _grid;
    readonly TextBlock _info = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 8, 0, 8));

    public IcmWindow(AppServices svc)
    {
        _svc = svc;
        Title = "Análisis ICM";
        Width = 900;
        Height = 560;
        Background = Theme.Bg;
        _payouts.Background = Theme.Panel;
        _payouts.Foreground = Theme.Text;
        _payouts.BorderBrush = Theme.Border;
        _grid = Ui.Grid(("Mano", "HandId", null, 120), ("Cartas", "HeroCards", null, 70), ("Rival", "Villain", null, 110), ("Equity", "EquityPct", "0.0", 70),
            ("Stack ef. (bb)", "EffectiveBb", "0.0", 100), ("EV fichas (bb)", "ChipEv", "0.0", 110), ("$EV (% del bote)", "DollarEvPct", "0.00", 120),
            ("Impuesto ICM", "Tax", null, 0));
        _grid.MouseDoubleClick += (_, _) => { if (_grid.SelectedItem is IcmRow r) ReplayerLauncher.TryOpen(_svc, this, r.HandId); };

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(Ui.Label("Premios (% de 1.º, 2.º, 3.º…)", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        bar.Children.Add(_payouts);
        bar.Children.Add(Ui.Btn("Analizar", (_, _) => Run()));
        var note = Ui.Label("Aproximación para estudiar: usa los stacks de la mesa al empezar la mano como todo el campo (SNG o mesa final), " +
                            "solo all-in contra un rival con cartas visibles, e ignora ciegas y antes. No aplica a Spin & Go (ahí el ICM es el EV de fichas).",
            11, false, Theme.Muted, new Thickness(0, 8, 0, 0));
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(note);
        top.Children.Add(_info);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_grid);
        Content = root;
        Loaded += (_, _) => Run();
    }

    public sealed record IcmRow(string HandId, string HeroCards, string Villain, double EquityPct, double EffectiveBb, double ChipEv, double DollarEvPct, string Tax);

    async void Run()
    {
        try
        {
            var payouts = _payouts.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, CultureInfo.InvariantCulture) / 100).ToArray();
            if (payouts.Length == 0 || payouts.Any(p => p <= 0)) throw new FormatException("Escribe los premios como porcentajes positivos");
            _info.Text = "Calculando…";
            var spots = await Task.Run(() => IcmAnalysis.ScanHero(_svc.Db, payouts));
            _grid.ItemsSource = spots.Select(s => new IcmRow(s.HandId, s.HeroCards, s.Villain, s.Equity * 100, s.EffectiveBb, s.ChipEv, s.DollarEv * 100,
                s.IcmTax ? "gana fichas pero pierde $" : "")).ToList();
            _info.Text = spots.Count == 0
                ? "No hay all-in de MTT/SNG con las cartas de ambos visibles."
                : $"{spots.Count} all-in. Los que ganan fichas pero pierden valor en premio: {spots.Count(s => s.IcmTax)}.";
        }
        catch (Exception ex) { _info.Text = "No se pudo analizar: " + ex.Message; }
    }
}
