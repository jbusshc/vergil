using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

public sealed class ChartsView : ViewBase
{
    public override string Title => "Gráficas";

    static readonly string[] Weekdays = { "Dom", "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb" };

    readonly TabControl _tabs = new();
    readonly FilterBar _bar = new();
    readonly ProfitChartOptions _opt = new();
    readonly TextBlock _summary = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 0, 0, 8));
    readonly ScottPlot.WPF.WpfPlot _profit = new() { MinHeight = 380 };
    readonly ScottPlot.WPF.WpfPlot _hours = new() { MinHeight = 380 };
    readonly ScottPlot.WPF.WpfPlot _days = new() { MinHeight = 380 };
    readonly ScottPlot.WPF.WpfPlot _hist = new() { MinHeight = 380 };
    ProfitSeries? _series;

    public ChartsView(AppServices svc) : base(svc)
    {
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        options.Children.Add(Ui.Check("Resultado real", true, v => { _opt.ShowNet = v; Redraw(); }));
        options.Children.Add(Ui.Check("EV de all-ins", true, v => { _opt.ShowEv = v; Redraw(); }));
        options.Children.Add(Ui.Check("Sin showdown", true, v => { _opt.ShowNoShowdown = v; Redraw(); }));
        options.Children.Add(Ui.Check("Con showdown", true, v => { _opt.ShowShowdown = v; Redraw(); }));
        options.Children.Add(Ui.Check("Banda de varianza", true, v => { _opt.ShowBand = v; Redraw(); }));

        var profitTab = new DockPanel();
        DockPanel.SetDock(options, Dock.Top);
        profitTab.Children.Add(options);
        profitTab.Children.Add(_profit);

        _tabs.Background = Theme.Bg;
        _tabs.BorderThickness = new Thickness(0);
        var tabs = _tabs;
        tabs.Items.Add(new TabItem { Header = "Ganancias", Content = profitTab });
        tabs.Items.Add(new TabItem { Header = "Por hora del día", Content = _hours });
        tabs.Items.Add(new TabItem { Header = "Por día de la semana", Content = _days });
        tabs.Items.Add(new TabItem { Header = "Resultados por sesión", Content = _hist });

        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(_bar);
        top.Children.Add(Ui.Btn("Exportar PNG", (_, _) => ExportPng()));
        top.Children.Add(_summary);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(tabs);
        Content = root;
        _bar.Changed += Refresh;
    }

    /// <summary>Guarda la gráfica de la pestaña visible como imagen PNG.</summary>
    void ExportPng()
    {
        var plot = _tabs.SelectedIndex switch { 0 => _profit, 1 => _hours, 2 => _days, _ => _hist };
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "grafica.png", Filter = "Imagen PNG (*.png)|*.png" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            int w = Math.Max(800, (int)plot.ActualWidth), h = Math.Max(400, (int)plot.ActualHeight);
            plot.Plot.SavePng(dlg.FileName, w, h);
            _summary.Text = "Gráfica guardada: " + dlg.FileName;
        }
        catch (Exception ex) { _summary.Text = "No se pudo guardar: " + ex.Message; }
    }

    public void SelectTab(int i) => _tabs.SelectedIndex = i;

    public override async void Refresh()
    {
        try
        {
            var period = _bar.Period;
            var scope = _bar.Scope;
            var (series, hours, days, sessions) = await Bg(() =>
            {
                var a = Svc.Analytics;
                return (a.GetProfitSeries(period, scope, 2000), a.GroupBy("hour", period, scope), a.GroupBy("weekday", period, scope),
                        SessionService.GetSessions(Svc.Db, period));
            });
            Show(series, hours, days, sessions);
        }
        catch (Exception ex) { _summary.Text = "No se pudo calcular: " + ex.Message; }
    }

    public void Show(ProfitSeries series, IReadOnlyList<GroupRow> hours, IReadOnlyList<GroupRow> days, IReadOnlyList<SessionRow> sessions)
    {
        var ci = CultureInfo.InvariantCulture;
        _series = series;
        _summary.Text = series.Hands == 0
            ? "No hay manos en este periodo."
            : $"{series.Hands:N0} manos · {series.MeanPerHand * 100:+0.0;-0.0;0.0} bb/100 · desviación típica {series.SdPerHand:0.0} bb por mano. " +
              "La banda muestra el rango en el que cae el ~68 % (±1σ) y el ~95 % (±2σ) de los resultados con tu winrate: " +
              "si estás dentro, tu racha es normal.";
        Redraw();

        Charts.Bars(_hours.Plot, hours.Select(h => h.Key + "h").ToList(), hours.Select(h => h.BbPer100).ToList(), "bb/100");
        _hours.Refresh();

        Charts.Bars(_days.Plot,
            days.Select(d => int.TryParse(d.Key, out var i) && i is >= 0 and <= 6 ? Weekdays[i] : d.Key).ToList(),
            days.Select(d => d.BbPer100).ToList(), "bb/100");
        _days.Refresh();

        var bins = Histogram.Build(sessions.Select(s => s.NetBb).ToList(), 12);
        Charts.Bars(_hist.Plot, bins.Select(b => b.Label).ToList(), bins.Select(b => (double)b.Count).ToList(),
            "Sesiones", signColors: false);
        _hist.Plot.Axes.Bottom.Label.Text = "Resultado de la sesión (bb)";
        _hist.Refresh();
    }

    void Redraw()
    {
        if (_series == null) return;
        Charts.Profit(_profit.Plot, _series, _opt);
        _profit.Refresh();
    }
}
