using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

public sealed class SessionsView : ViewBase
{
    public override string Title => "Sesiones";

    readonly FilterBar _bar = new(withScope: false);
    readonly DataGrid _grid;
    readonly ScottPlot.WPF.WpfPlot _tilt = new() { Height = 170 };
    readonly TextBlock _info = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 0, 0, 8));

    public SessionsView(AppServices svc) : base(svc)
    {
        _grid = Ui.Grid(("Inicio", "Start", "dd/MM/yy HH:mm", 120), ("Duración", "Duration", @"hh\:mm", 80), ("Manos", "Hands", "N0", 70),
            ("Neto (bb)", "NetBb", "0.0", 90), ("bb/100", "BbPer100", "0.0", 80), ("EV (bb)", "EvBb", "0.0", 90),
            ("Mesas máx.", "TablesMax", null, 90), ("Manos/h", "HandsPerHour", "0", 80));
        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(_bar);
        top.Children.Add(_info);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(_tilt, Dock.Bottom);
        root.Children.Add(_tilt);
        root.Children.Add(_grid);
        Content = root;
        _bar.Changed += Refresh;
    }

    public override async void Refresh()
    {
        try
        {
            var period = _bar.Period;
            var rows = await Bg(() =>
            {
                SessionService.EnsureUpToDate(Svc.Db);
                return SessionService.GetSessions(Svc.Db, period);
            });
            Show(rows);
            var hero = Svc.Db.GetHeroName();
            if (hero != null)
            {
                var hist = await Bg(() => TiltService.ScoreHistory(Svc.Db, Svc.Engine, hero));
                ShowTilt(hist.Where(h => rows.Any(r => r.Start == h.Start)).ToList());
            }
        }
        catch (Exception ex) { _info.Text = "No se pudo calcular: " + ex.Message; }
    }

    public void ShowTilt(IReadOnlyList<(DateTime Start, int Score)> hist)
    {
        var last = hist.TakeLast(40).ToList();
        Charts.Bars(_tilt.Plot, last.Select(h => h.Start.ToString("dd/MM")).ToList(), last.Select(h => (double)h.Score).ToList(), "Tilt score por sesión (0-100)");
        _tilt.Refresh();
    }

    public void Show(IReadOnlyList<SessionRow> rows)
    {
        var list = rows.OrderByDescending(r => r.Start).ToList();
        _grid.ItemsSource = list;
        _info.Text = list.Count == 0
            ? "Todavía no hay sesiones. Una pausa de más de 30 minutos entre manos abre una sesión nueva."
            : $"{list.Count} sesiones · {list.Sum(r => r.Hands):N0} manos · {list.Sum(r => r.Duration.TotalHours):0.0} horas · " +
              $"{list.Sum(r => r.NetBb):+0.0;-0.0;0.0} bb";
    }
}
