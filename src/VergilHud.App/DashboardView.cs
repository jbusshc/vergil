using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

public sealed class DashboardView : ViewBase
{
    public override string Title => "Dashboard";

    readonly FilterBar _bar = new();
    readonly WrapPanel _cards = new();
    readonly TextBlock _hint = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 0, 0, 8));
    readonly ScottPlot.WPF.WpfPlot _profit = new() { Height = 300 };
    readonly ScottPlot.WPF.WpfPlot _sessions = new() { Height = 240 };
    readonly StackPanel _goals = new();

    public DashboardView(AppServices svc) : base(svc)
    {
        var root = new StackPanel();
        root.Children.Add(_bar);
        root.Children.Add(_hint);
        root.Children.Add(_cards);
        root.Children.Add(_goals);
        root.Children.Add(Ui.Section("Resultado acumulado (real y esperado)", _profit));
        root.Children.Add(Ui.Section("Últimas 30 sesiones", _sessions));
        Content = Scroll(root);
        _bar.Changed += Refresh;
    }

    void ShowGoals()
    {
        _goals.Children.Clear();
        try
        {
            var items = GoalService.Progress(Svc.Db, GoalService.Load(Svc.Db));
            var report = new Button { Content = Loc.T("Abrir informe semanal"), Background = Theme.Button, Foreground = Theme.Text, BorderBrush = Theme.ButtonBorder,
                Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            report.Click += (_, _) =>
            {
                try
                {
                    var path = System.IO.Path.Combine(AppSettings.ReportsDir, WeeklyReport.FileName(DateTime.Now));
                    if (!System.IO.File.Exists(path))
                        path = WeeklyReport.Generate(Svc.Db, Svc.Engine, Svc.Study, Svc.Leaks, Svc.LeakBaselines, AppSettings.ReportsDir, DateTime.Now);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex) { _hint.Text = "No se pudo abrir el informe: " + ex.Message; }
            };
            _goals.Children.Add(report);
            if (items.Count > 0)
            {
                _goals.Children.Add(Ui.Label("Objetivos de esta semana", 13, true, null, new Thickness(0, 0, 0, 4)));
                foreach (var p in items)
                {
                    _goals.Children.Add(Ui.Label($"{Loc.T(p.Name)}: {p.Done} / {p.Target}", 12, false, Theme.Muted, new Thickness(0, 2, 0, 2)));
                    _goals.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = p.Fraction, Height = 6, Margin = new Thickness(0, 0, 0, 6), Foreground = p.Fraction >= 1 ? Theme.Green : Theme.Blue });
                }
            }
        }
        catch (Exception ex) { _hint.Text = "No se pudieron leer los objetivos: " + ex.Message; }
    }

    public override async void Refresh()
    {
        try
        {
            var period = _bar.Period;
            var scope = _bar.Scope;
            var (o, sessions, series) = await Bg(() =>
            {
                var a = Svc.Analytics;
                return (a.GetOverview(period, scope), SessionService.GetSessions(Svc.Db, period).TakeLast(30).ToList(),
                        a.GetProfitSeries(period, scope, 1500));
            });
            Show(o, sessions, series, scope);
        }
        catch (Exception ex) { _hint.Text = "No se pudo calcular: " + ex.Message; }
    }

    /// <summary>Pinta los datos ya calculados (separado de Refresh para poder dibujarlo también fuera de pantalla).</summary>
    public void Show(Overview o, IReadOnlyList<SessionRow> sessions, ProfitSeries series, string scope)
    {
        var ci = CultureInfo.InvariantCulture;
        ShowGoals();
        _cards.Children.Clear();
        if (o.Hands == 0)
        {
            _hint.Text = "No hay manos de este formato en el periodo elegido.";
        }
        else _hint.Text = scope == "cash"
            ? ""
            : "En torneos las ciegas suben durante el torneo: las ciegas grandes por 100 manos sirven de referencia, pero el resultado real está en la vista Torneos (ROI).";

        double luck = o.BbPer100 - o.EvBbPer100;
        _cards.Children.Add(Ui.Card("Manos", o.Hands.ToString("N0", ci)));
        _cards.Children.Add(Ui.Card("bb/100", o.BbPer100.ToString("+0.0;-0.0;0.0", ci), valueBrush: Theme.Signed(o.BbPer100)));
        _cards.Children.Add(Ui.Card("EV bb/100", o.EvBbPer100.ToString("+0.0;-0.0;0.0", ci),
            $"suerte: {luck:+0.0;-0.0;0.0} bb/100", Theme.Signed(o.EvBbPer100)));
        _cards.Children.Add(Ui.Card("Neto", o.NetBb.ToString("+#,0.0;-#,0.0;0.0", ci) + " bb", valueBrush: Theme.Signed(o.NetBb)));
        _cards.Children.Add(Ui.Card("Rake pagado", o.RakeBb > 0 ? o.RakeBb.ToString("#,0.0", ci) + " bb" : "—"));
        _cards.Children.Add(Ui.Card("Horas jugadas", o.Hours.ToString("0.0", ci)));
        _cards.Children.Add(Ui.Card("Manos por hora", o.HandsPerHour.ToString("N0", ci)));

        Charts.Profit(_profit.Plot, series, new ProfitChartOptions { ShowShowdown = false, ShowNoShowdown = false, ShowBand = false });
        _profit.Refresh();

        Charts.Bars(_sessions.Plot, sessions.Select(s => s.Start.ToString("dd/MM", ci)).ToList(),
            sessions.Select(s => s.NetBb).ToList(), "Resultado de la sesión (bb)");
        _sessions.Refresh();
    }
}
