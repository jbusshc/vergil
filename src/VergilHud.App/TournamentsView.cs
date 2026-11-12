using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

public sealed class TournamentsView : ViewBase
{
    public override string Title => "Torneos";

    static readonly string[] FormatNames = { "Todos", "Spin & Go", "MTT / SNG" };
    static readonly string?[] FormatKeys = { null, "spin", "mtt" };

    readonly FilterBar _bar = new(withScope: false);
    readonly WrapPanel _cards = new();
    readonly TextBlock _message = Ui.Label("", 13, false, Theme.Text, new Thickness(0, 0, 0, 8));
    readonly StackPanel _spin = new();
    readonly DataGrid _grid;
    readonly ScottPlot.WPF.WpfPlot _plot = new() { Height = 280 };
    int _format;

    public TournamentsView(AppServices svc) : base(svc)
    {
        _grid = Ui.Grid(("Inicio", "Started", "dd/MM/yy HH:mm", 120), ("Formato", "Format", null, 80), ("Inscripción", "Buyin", "0.##", 90),
            ("Puesto", "Place", null, 70), ("Premio", "Prize", "0.##", 90), ("Beneficio", "Profit", "+0.##;-0.##;0", 90),
            ("Multiplicador", "Multiplier", "0.#", 100), ("Manos", "Hands", null, 70));
        _grid.MaxHeight = 320;

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(_bar);
        bar.Children.Add(Ui.Label("Tipo", 13, false, Theme.Muted, new Thickness(0, 0, 8, 10)));
        bar.Children.Add(Ui.Combo(FormatNames, 0, i => { _format = i; Refresh(); }, 120));
        bar.Children.Add(Ui.Btn("Análisis ICM…", (_, _) =>
        {
            var blocked = Svc.Play.BlockedMessage(new IcmAccess());
            if (blocked != null) { MessageBox.Show(blocked + ".", "Vergil HUD · Modo Juego"); return; }
            new IcmWindow(Svc) { Owner = Window.GetWindow(this) }.Show();
        }));

        var root = new StackPanel();
        root.Children.Add(bar);
        root.Children.Add(_cards);
        root.Children.Add(_message);
        root.Children.Add(_spin);
        root.Children.Add(Ui.Section("Beneficio acumulado", _plot));
        root.Children.Add(Ui.Section("Torneos", _grid));
        Content = Scroll(root);
        _bar.Changed += Refresh;
    }

    public override async void Refresh()
    {
        try
        {
            var period = _bar.Period;
            var format = FormatKeys[_format];
            var (rows, spin) = await Bg(() => (TournamentReport.Load(Svc.Db, period, format), TournamentReport.Spin(Svc.Db, period)));
            Show(rows, spin, format);
        }
        catch (Exception ex) { _message.Text = "No se pudo calcular: " + ex.Message; }
    }

    public void Show(IReadOnlyList<TournamentRow> rows, SpinReport spin, string? format)
    {
        var ci = CultureInfo.InvariantCulture;
        var roi = TournamentReport.Roi(rows);
        _cards.Children.Clear();
        _cards.Children.Add(Ui.Card("Torneos terminados", roi.N.ToString("N0", ci), $"{rows.Count - roi.N} en curso o sin final"));
        _cards.Children.Add(Ui.Card("ROI", roi.N == 0 ? "—" : (roi.Roi * 100).ToString("+0.0;-0.0;0.0", ci) + " %",
            roi.N < 2 ? null : $"90 %: {roi.Lo * 100:+0;-0;0} a {roi.Hi * 100:+0;-0;0} %", Theme.Signed(roi.Roi)));
        _cards.Children.Add(Ui.Card("ITM", roi.N == 0 ? "—" : roi.ItmPct.ToString("0.0", ci) + " %", "torneos con premio"));
        _cards.Children.Add(Ui.Card("Beneficio medio", roi.N == 0 ? "—" : roi.AvgProfit.ToString("+#,0.##;-#,0.##;0", ci),
            "por torneo", Theme.Signed(roi.AvgProfit)));
        _cards.Children.Add(Ui.Card("Beneficio total", roi.N == 0 ? "—" : (roi.TotalPrize - roi.TotalBuyin).ToString("+#,0.##;-#,0.##;0", ci),
            valueBrush: Theme.Signed(roi.TotalPrize - roi.TotalBuyin)));
        _message.Text = roi.Message;

        _spin.Children.Clear();
        if (format is null or "spin" && spin.Played > 0)
        {
            var lines = new StackPanel();
            lines.Children.Add(Ui.Label($"{spin.Played} Spins terminados · {spin.Wins} victorias ({spin.WinPct:0.#} %)", 13));
            lines.Children.Add(Ui.Label(spin.HeadsUpReached == 0
                ? "Aún no has llegado a ningún mano a mano."
                : $"Mano a mano alcanzado: {spin.HeadsUpReached} veces · ganado: {spin.HeadsUpWon} ({spin.HeadsUpWon * 100.0 / spin.HeadsUpReached:0} %)", 13,
                false, null, new Thickness(0, 3, 0, 0)));
            lines.Children.Add(Ui.Label(spin.WinsByMultiplier.Count == 0
                ? "Multiplicadores de tus victorias: —"
                : "Multiplicadores de tus victorias: " + string.Join(" · ", spin.WinsByMultiplier.Select(m => $"x{m.Multiplier:0.#} ({m.Count})")), 13,
                false, null, new Thickness(0, 3, 0, 0)));
            var spinRows = rows.Where(r => r.Format == "spin" && r.Buyin > 0).ToList();
            if (spinRows.Count > 0)
            {
                double buyin = SpinMultipliers.Nearest(spinRows.GroupBy(r => SpinMultipliers.Nearest(r.Buyin)).OrderByDescending(g => g.Count()).First().Key);
                var cmp = SpinMultipliers.Compare(buyin, spin.WinsByMultiplier);
                lines.Children.Add(Ui.Label($"Frente a la tabla oficial de PokerStars (Spin de {buyin:0}): ROI de un jugador sin ventaja {SpinMultipliers.BreakEvenRoi(buyin) * 100:+0.0;-0.0}%, " +
                    $"multiplicador medio ×{SpinMultipliers.ExpectedMultiplier(buyin):0.00}.", 12, false, Theme.Muted, new Thickness(0, 3, 0, 0)));
                if (spin.Wins >= 10)
                {
                    foreach (var c in cmp.Where(c => c.Expected >= 1 || c.Observed > 0))
                        lines.Children.Add(Ui.Label($"×{c.Multiplier:0.#}: {c.Observed} vistos, {c.Expected:0.0} esperados (normal entre {c.Lo:0} y {c.Hi:0})" + (c.Outside ? "  ← fuera de lo normal" : ""),
                            12, false, c.Outside ? Theme.Yellow : null, new Thickness(0, 1, 0, 0)));
                    lines.Children.Add(Ui.Label("Que un multiplicador se aleje de lo esperado es suerte, no habilidad: la ruleta se decide antes de jugar. Tabla de 3 jugadores.", 11,
                        false, Theme.Muted, new Thickness(0, 3, 0, 4)));
                }
                else lines.Children.Add(Ui.Label("Con menos de 10 victorias aún no se puede comparar la distribución.", 11, false, Theme.Muted, new Thickness(0, 3, 0, 4)));
            }
            lines.Children.Add(Ui.Label("El multiplicador solo se conoce cuando ganas (el historial no dice qué premio había en las derrotas).", 11,
                false, Theme.Muted, new Thickness(0, 3, 0, 6)));
            foreach (var ph in spin.Phases)
                lines.Children.Add(Ui.Label($"{ph.Phase}: {ph.Hands} manos · {ph.BbPer100:+0.0;-0.0;0.0} bb/100", 13, false, null, new Thickness(0, 2, 0, 0)));
            _spin.Children.Add(Ui.Section("Spin & Go", lines));
        }

        Charts.TournamentProfit(_plot.Plot, rows);
        _plot.Refresh();
        _grid.ItemsSource = rows.OrderByDescending(r => r.Started).ToList();
    }
}
