using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Bankroll: saldo, buy-ins disponibles, riesgo de ruina como rango y movimientos (rakeback y bonos aparte).</summary>
public sealed class BankrollView : ViewBase
{
    public override string Title => "Bankroll";

    static readonly string[] KindNames = { "Depósito", "Retirada", "Rakeback", "Bono" };
    readonly WrapPanel _cards = new();
    readonly TextBlock _risk = Ui.Label("", 12, false, null, new Thickness(0, 4, 0, 4));
    readonly TextBlock _msg = Ui.Label("", 12, false, Theme.Green);
    readonly DataGrid _grid;
    readonly TextBox _amount = new() { Width = 90, Margin = new Thickness(0, 0, 14, 0) };
    readonly TextBox _note = new() { Width = 220, Margin = new Thickness(0, 0, 8, 0) };
    int _kind;

    public sealed record Row(long Id, string Fecha, string Tipo, double Importe, string Nota);

    public BankrollView(AppServices svc) : base(svc)
    {
        foreach (var t in new[] { _amount, _note })
        {
            t.Background = Theme.Panel; t.Foreground = Theme.Text; t.BorderBrush = Theme.Border; t.CaretBrush = Theme.Text; t.Padding = new Thickness(4, 3, 4, 3);
        }
        _grid = Ui.Grid(("Fecha", "Fecha", null, 130), ("Tipo", "Tipo", null, 110), ("Importe", "Importe", "0.00", 100), ("Nota", "Nota", null, 320));
        _grid.MaxHeight = 260;

        var add = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        add.Children.Add(Ui.Label("Tipo de movimiento", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        add.Children.Add(Ui.Combo(KindNames, 0, i => _kind = i, 110));
        add.Children.Add(Ui.Label("Importe", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        add.Children.Add(_amount);
        add.Children.Add(Ui.Label("Nota", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        add.Children.Add(_note);
        add.Children.Add(Ui.Btn("Añadir movimiento", (_, _) => Add()));
        add.Children.Add(Ui.Btn("Borrar", (_, _) => Remove()));

        var minRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        var min = new TextBox { Width = 60, Text = Svc.Settings.MinBuyins.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 8, 0),
            Background = Theme.Panel, Foreground = Theme.Text, BorderBrush = Theme.Border, CaretBrush = Theme.Text };
        min.TextChanged += (_, _) =>
        {
            if (double.TryParse(min.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0) { Svc.Settings.MinBuyins = v; Svc.Settings.Save(); }
        };
        minRow.Children.Add(Ui.Label("Buy-ins mínimos", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        minRow.Children.Add(min);
        minRow.Children.Add(Ui.Label("(avisa aquí si bajas de este número)", 11, false, Theme.Muted));

        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(_cards);
        top.Children.Add(_risk);
        top.Children.Add(minRow);
        top.Children.Add(add);
        top.Children.Add(_msg);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_grid);
        Content = root;
    }

    static string Kind(int i) => BankrollSummary.Kinds[i];

    void Add()
    {
        try
        {
            if (!double.TryParse(_amount.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new ArgumentException("Escribe el importe como un número");
            Svc.Bankroll.Add(Kind(_kind), v, _note.Text.Trim());
            _amount.Text = ""; _note.Text = "";
            _msg.Text = "Movimiento añadido.";
            Refresh();
        }
        catch (Exception ex) { _msg.Foreground = Theme.Red; _msg.Text = ex.Message; }
    }

    void Remove()
    {
        if (_grid.SelectedItem is Row r) { Svc.Bankroll.Delete(r.Id); Refresh(); }
    }

    public override async void Refresh()
    {
        try
        {
            _msg.Foreground = Theme.Green;
            var (s, tx) = await Task.Run(() => (Svc.Bankroll.Summarize(), Svc.Bankroll.List()));
            Show(s, tx);
        }
        catch (Exception ex) { _risk.Text = "No se pudo calcular: " + ex.Message; }
    }

    public void Show(BankrollSummary s, IReadOnlyList<BankrollTx> tx)
    {
        _cards.Children.Clear();
        _cards.Children.Add(Ui.Card("Saldo", s.Balance.ToString("0.00"), null, Theme.Signed(s.Balance)));
        _cards.Children.Add(Ui.Card("Resultado cash", s.CashResult.ToString("+0.00;-0.00;0.00"), null, Theme.Signed(s.CashResult)));
        _cards.Children.Add(Ui.Card("Resultado torneos", s.TournamentResult.ToString("+0.00;-0.00;0.00"), null, Theme.Signed(s.TournamentResult)));
        _cards.Children.Add(Ui.Card("Rakeback y bonos", s.Extras.ToString("0.00"), "aparte del juego"));
        _cards.Children.Add(Ui.Card("Buy-ins (cash)", s.CashBuyins is { } c ? c.ToString("0.0") : "-", s.AvgCashBuyin is { } a ? $"de {a:0.00}" : null,
            s.CashBuyins is { } cb && cb < Svc.Settings.MinBuyins ? Theme.Red : null));
        _cards.Children.Add(Ui.Card("Buy-ins (torneos)", s.TournamentBuyins is { } t ? t.ToString("0.0") : "-", s.AvgTournamentBuyin is { } ta ? $"de {ta:0.00}" : null,
            s.TournamentBuyins is { } tb && tb < Svc.Settings.MinBuyins ? Theme.Red : null));

        var lines = new List<string>();
        if (s.BelowMinimum(Svc.Settings.MinBuyins)) lines.Add($"⚠ Estás por debajo de tus {Svc.Settings.MinBuyins:0.#} buy-ins mínimos.");
        lines.Add("Cash: " + (s.CashRisk?.Message ?? "hacen falta al menos 500 manos de cash para estimar el riesgo."));
        lines.Add("Torneos: " + (s.TournamentRisk?.Message ?? "hacen falta al menos 5 torneos terminados para estimar el riesgo."));
        _risk.Text = string.Join("\n", lines);

        _grid.ItemsSource = tx.Select(x => new Row(x.Id, x.At.ToString("dd/MM/yy HH:mm"), KindNames[Array.IndexOf(BankrollSummary.Kinds, x.Kind)], x.Amount, x.Note)).ToList();
    }
}
