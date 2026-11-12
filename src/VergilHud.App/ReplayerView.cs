using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>El replayer: la mesa, los controles y la línea de tiempo de una mano (DESIGN.md §9).</summary>
public sealed class ReplayerView : UserControl
{
    const double W = 820, H = 470;

    readonly AppServices _svc;
    readonly Hand _hand;
    HandReplay _replay;
    bool _inBb;
    int _step;
    bool _syncing;
    int _max = int.MaxValue;
    bool _quiz;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    readonly Canvas _table = new() { Width = W, Height = H, ClipToBounds = true };
    readonly TextBlock _title = Ui.Label("", 15, true);
    readonly TextBlock _tags = Ui.Label("", 12, false, Theme.Yellow);
    readonly TextBlock _desc = Ui.Label("", 15, true, null, new Thickness(0, 8, 0, 2));
    readonly TextBlock _decision = Ui.Label("", 12, false, Theme.Blue, new Thickness(0, 0, 0, 2));
    readonly StackPanel _equity = new();
    readonly TextBlock _status = Ui.Label("", 11, false, Theme.Muted, new Thickness(0, 6, 0, 0));
    readonly ListBox _timeline = new() { Background = Theme.Panel, BorderBrush = Theme.Border, Foreground = Theme.Text, FontSize = 12 };
    readonly Button _play;
    readonly ToggleButton _units;
    readonly Slider _speed = new() { Minimum = 0.3, Maximum = 3, Value = 1, Width = 100, VerticalAlignment = VerticalAlignment.Center, IsDirectionReversed = true,
        ToolTip = "Velocidad de la reproducción automática" };

    public int Step => _step;
    public int StepCount => _replay.Steps.Count;
    public HandReplay Replay => _replay;

    /// <summary>Modo estudio: se ve la mano solo hasta el paso <paramref name="max"/>; lo posterior (y el resultado) queda oculto.</summary>
    public void EnterQuiz(int max)
    {
        _quiz = true;
        _max = max;
        FillTimeline();
        Go(max);
    }

    /// <summary>Sale del modo estudio y va al paso indicado, con la mano entera ya visible.</summary>
    public void Reveal(int goTo)
    {
        _quiz = false;
        _max = int.MaxValue;
        FillTimeline();
        Go(goTo);
    }

    public ReplayerView(AppServices svc, Hand hand)
    {
        _svc = svc;
        _hand = hand;
        _replay = HandReplay.Build(hand, Money);
        Background = Theme.Bg;

        _timeline.SelectionChanged += (_, _) => { if (!_syncing && _timeline.SelectedIndex >= 0) Go(_timeline.SelectedIndex); };
        _timer.Tick += (_, _) => { if (_step < StepCount - 1) Go(_step + 1); else Stop(); };
        _speed.ValueChanged += (_, _) => _timer.Interval = TimeSpan.FromSeconds(_speed.Value);

        _play = Ui.Btn("▶ Reproducir", (_, _) => TogglePlay(), 110);
        _units = new ToggleButton
        {
            Content = "Fichas", Background = Theme.B("#2E3038"), Foreground = Theme.Text, BorderBrush = Theme.B("#444754"),
            Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), MinWidth = 70,
            ToolTip = "Cambiar entre fichas y ciegas grandes",
        };
        _units.Click += (_, _) => { _inBb = _units.IsChecked == true; _units.Content = _inBb ? "bb" : "Fichas"; Rebuild(); };

        var transport = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        transport.Children.Add(Ui.Btn("⏮", (_, _) => First(), 40));
        transport.Children.Add(Ui.Btn("◀", (_, _) => Prev(), 40));
        transport.Children.Add(Ui.Btn("▶", (_, _) => Next(), 40));
        transport.Children.Add(Ui.Btn("⏭", (_, _) => Last(), 40));
        transport.Children.Add(_play);
        transport.Children.Add(Ui.Label("rápido", 11, false, Theme.Muted, new Thickness(4, 0, 4, 0)));
        transport.Children.Add(_speed);
        transport.Children.Add(Ui.Label("lento", 11, false, Theme.Muted, new Thickness(4, 0, 14, 0)));
        foreach (var (name, st) in new[] { ("Preflop", Street.Preflop), ("Flop", Street.Flop), ("Turn", Street.Turn), ("River", Street.River) })
        {
            var s = st;
            transport.Children.Add(Ui.Btn(name, (_, _) => { var i = _replay.FirstStepOf(s); if (i >= 0) Go(i); }, 60));
        }

        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        tools.Children.Add(_units);
        tools.Children.Add(Ui.Btn("Etiquetas ▾", (s, _) => ShowTagMenu((Button)s)));
        tools.Children.Add(Ui.Btn("Marcar para estudiar", (_, _) => { _svc.Notes.AddTag(_hand.Id, "estudiar"); RefreshTags(); }));
        tools.Children.Add(Ui.Btn("Copiar texto", (_, _) => CopyText()));
        tools.Children.Add(Ui.Btn("Exportar PNG", (_, _) => ExportPng()));

        var left = new StackPanel { Margin = new Thickness(14) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(_title);
        head.Children.Add(_tags);
        _tags.Margin = new Thickness(14, 0, 0, 0);
        left.Children.Add(head);
        left.Children.Add(new Border { Child = _table, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        left.Children.Add(_desc);
        left.Children.Add(_decision);
        left.Children.Add(_equity);
        left.Children.Add(transport);
        left.Children.Add(tools);
        left.Children.Add(_status);

        var right = new DockPanel { Margin = new Thickness(0, 14, 14, 14), Width = 320 };
        var tl = Ui.Label("Línea de tiempo", 13, true, null, new Thickness(0, 0, 0, 6));
        DockPanel.SetDock(tl, Dock.Top);
        right.Children.Add(tl);
        right.Children.Add(_timeline);

        var root = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        root.Children.Add(right);
        root.Children.Add(left);
        Content = root;
        FillTimeline();
        RefreshTags();
        Go(0);
    }

    // ---------- cantidades ----------
    string Money(decimal v) => _inBb ? (v / BigBlind).ToString("0.##", CultureInfo.InvariantCulture) + " bb" : v.ToString("0.##", CultureInfo.InvariantCulture);
    decimal BigBlind => _hand.BigBlind > 0 ? _hand.BigBlind : 1;

    void Rebuild()
    {
        _replay = HandReplay.Build(_hand, Money);
        FillTimeline();
        Go(_step);
    }

    void FillTimeline()
    {
        _syncing = true;
        _timeline.Items.Clear();
        foreach (var s in _replay.Steps.Where(x => x.Index <= _max))
        {
            string tag = s.Street switch { Street.Preflop => "PF", Street.Flop => "F", Street.Turn => "T", _ => "R" };
            _timeline.Items.Add($"{s.Index,2}  {tag,-2}  {s.Description}");
        }
        _syncing = false;
    }

    // ---------- navegación ----------
    public void Go(int i)
    {
        _step = Math.Clamp(i, 0, Math.Min(_max, StepCount - 1));
        _syncing = true;
        _timeline.SelectedIndex = _step;
        _timeline.ScrollIntoView(_timeline.SelectedItem);
        _syncing = false;
        Draw();
    }

    public void First() => Go(0);
    public void Last() => Go(StepCount - 1);
    public void Prev() => Go(_step - 1);
    public void Next() => Go(_step + 1);

    public void TogglePlay()
    {
        if (_timer.IsEnabled) Stop();
        else
        {
            if (_step >= StepCount - 1) Go(0);
            _timer.Interval = TimeSpan.FromSeconds(_speed.Value);
            _timer.Start();
            _play.Content = "⏸ Pausar";
        }
    }

    public void Stop()
    {
        _timer.Stop();
        _play.Content = "▶ Reproducir";
    }

    // ---------- dibujo ----------
    void Draw()
    {
        var step = _replay.Steps[_step];
        var ci = CultureInfo.InvariantCulture;
        _table.Children.Clear();
        _table.Background = Theme.B("#14161F");

        var felt = new Ellipse
        {
            Width = W - 120, Height = H - 90, Stroke = Theme.B("#46465A"), StrokeThickness = 6,
            Fill = new RadialGradientBrush(Color.FromRgb(34, 128, 66), Color.FromRgb(20, 96, 48)),
        };
        Canvas.SetLeft(felt, 60);
        Canvas.SetTop(felt, 45);
        _table.Children.Add(felt);

        // Tablero y bote
        for (int i = 0; i < 5; i++)
        {
            var card = Card(i < step.Board.Count ? step.Board[i] : null, back: false, empty: i >= step.Board.Count);
            Canvas.SetLeft(card, W / 2 - 2.5 * 38 + i * 38 - 2);
            Canvas.SetTop(card, H / 2 - 44);
            _table.Children.Add(card);
        }
        var pot = Ui.Label($"Bote: {Money(step.Pot)}", 14, true, Theme.B("#F4F4F8"));
        pot.FontFamily = new FontFamily("Consolas");
        Canvas.SetLeft(pot, W / 2 - 62);
        Canvas.SetTop(pot, H / 2 + 12);
        _table.Children.Add(pot);

        // Asientos: el héroe abajo, en sentido horario
        int n = step.Seats.Count;
        int heroIdx = Math.Max(0, step.Seats.ToList().FindIndex(s => s.IsHero));
        for (int k = 0; k < n; k++)
        {
            var seat = step.Seats[k];
            int visual = ((k - heroIdx) % n + n) % n;
            double ang = Math.PI / 2 + visual * 2 * Math.PI / n;
            double cx = W / 2 + (W / 2 - 100) * Math.Cos(ang), cy = H / 2 + (H / 2 - 62) * Math.Sin(ang);
            bool actor = step.Actor == seat.Player && !step.IsDeal;
            AddSeat(seat, cx, cy, actor, step.IsFinal);

            if (seat.Bet > 0)
            {
                double bx = cx + (W / 2 - cx) * 0.38, by = cy + (H / 2 - cy) * 0.40;
                var chip = Ui.Label(Money(seat.Bet), 12, true, Theme.B("#F0DC5A"));
                chip.FontFamily = new FontFamily("Consolas");
                var b = new Border { Background = Theme.B("#CC1A1B22"), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 1), Child = chip };
                Canvas.SetLeft(b, bx - 24);
                Canvas.SetTop(b, by - 10);
                _table.Children.Add(b);
            }
        }

        _desc.Text = step.Description;
        _decision.Text = step.Decision is { } d && !_quiz
            ? (d.ToCall > 0
                ? $"Tu decisión: bote {Money(d.PotBefore)} · a pagar {Money(d.ToCall)} · odds de bote {d.PotOddsPct:0.#}% · SPR {d.Spr:0.#}"
                : $"Tu decisión: sin apuesta que pagar · bote {Money(d.PotBefore)} · SPR {d.Spr:0.#}")
            : "";

        _equity.Children.Clear();
        if (step.Equity != null && !_quiz)
        {
            _equity.Children.Add(Ui.Label("Equity de los jugadores que siguen en la mano", 11, false, Theme.Muted, new Thickness(0, 2, 0, 2)));
            foreach (var (p, e) in step.Equity.OrderByDescending(x => x.Value))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                row.Children.Add(Ui.Label(p, 12, false, null, new Thickness(0, 0, 8, 0)));
                row.Children[0].SetValue(WidthProperty, 140.0);
                var bar = new Border { Width = Math.Max(2, 260 * e), Height = 10, Background = p == _hand.Hero ? Theme.Green : Theme.Blue, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(bar);
                row.Children.Add(Ui.Label($"  {e * 100:0.0}%", 12, true, null, new Thickness(6, 0, 0, 0)));
                _equity.Children.Add(row);
            }
        }
        _status.Text = _quiz ? "Modo estudio: el resto de la mano está oculto hasta que respondas." : $"Paso {_step + 1} de {StepCount} · ← → para moverte, espacio para reproducir";
        _title.Text = $"Mano #{_hand.Id} · {(_hand.IsTournament ? $"Torneo, nivel {_hand.Level}" : "Cash")} {_hand.SmallBlind.ToString("0.##", ci)}/{_hand.BigBlind.ToString("0.##", ci)}";
    }

    void AddSeat(ReplaySeat seat, double cx, double cy, bool actor, bool final)
    {
        const double bw = 124, bh = 58;
        var name = seat.Player.Length > 13 ? seat.Player[..12] + "…" : seat.Player;
        var stack = Ui.Label(Money(seat.Stack), 12, false, Theme.B("#DADAE2"));
        stack.FontFamily = new FontFamily("Consolas");
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Ui.Label(name, 12, true, seat.Folded ? Theme.Muted : Theme.Text));
        header.Children.Add(Ui.Label("  " + seat.Position, 10, false, Theme.Muted));
        var body = new StackPanel { Margin = new Thickness(6, 3, 6, 3) };
        body.Children.Add(header);
        body.Children.Add(stack);
        if (seat.AllIn && !seat.Folded) body.Children.Add(Ui.Label("ALL-IN", 9, true, Theme.Red));
        if (final && seat.Won > 0) body.Children.Add(Ui.Label("+" + Money(seat.Won), 11, true, Theme.Green));

        var box = new Border
        {
            Width = bw, Height = bh, CornerRadius = new CornerRadius(8), Child = body,
            Background = Theme.B("#CC20222C"),
            BorderThickness = new Thickness(actor ? 2.5 : 1.2),
            BorderBrush = actor ? Theme.Yellow : seat.IsHero ? Theme.B("#D6B248") : final && seat.Won > 0 ? Theme.Green : Theme.B("#55586A"),
            Opacity = seat.Folded ? 0.42 : 1,
        };
        Canvas.SetLeft(box, cx - bw / 2);
        Canvas.SetTop(box, cy - bh / 2 + 18);
        _table.Children.Add(box);

        // Cartas: las del héroe, las que se han mostrado, o el dorso si sigue en la mano
        var cards = new StackPanel { Orientation = Orientation.Horizontal, Opacity = seat.Folded ? 0.3 : 1 };
        var hole = (seat.Cards ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < 2; i++)
            cards.Children.Add(Card(hole.Length == 2 ? hole[i] : null, back: hole.Length != 2, empty: false, small: true));
        Canvas.SetLeft(cards, cx - 33);
        Canvas.SetTop(cards, cy - bh / 2 - 22);
        _table.Children.Add(cards);

        if (seat.IsButton)
        {
            var d = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Theme.B("#F4F4F8"),
                Child = new TextBlock { Text = "D", FontWeight = FontWeights.Bold, FontSize = 11, Foreground = Theme.B("#111111"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Canvas.SetLeft(d, cx + bw / 2 - 6);
            Canvas.SetTop(d, cy - bh / 2 + 6);
            _table.Children.Add(d);
        }
    }

    static UIElement Card(string? card, bool back, bool empty, bool small = false)
    {
        double w = small ? 32 : 36, h = small ? 44 : 50;
        if (empty)
            return new Border { Width = w, Height = h, CornerRadius = new CornerRadius(4), Margin = new Thickness(1), BorderBrush = Theme.B("#33FFFFFF"), BorderThickness = new Thickness(1), Background = Theme.B("#18000000") };
        if (back || card is not { Length: 2 })
            return new Border { Width = w, Height = h, CornerRadius = new CornerRadius(4), Margin = new Thickness(1), Background = Theme.B("#2B4C8C"), BorderBrush = Theme.B("#8FB2F0"), BorderThickness = new Thickness(1) };
        char suit = char.ToLowerInvariant(card[1]);
        string sym = suit switch { 'h' => "♥", 'd' => "♦", 'c' => "♣", _ => "♠" };
        var color = suit is 'h' or 'd' ? Theme.B("#D63A3A") : Theme.B("#1A1A22");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = card[0].ToString().Replace("T", "10"), FontWeight = FontWeights.Bold, FontSize = small ? 14 : 16, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = sym, FontSize = small ? 14 : 16, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -3, 0, 0) });
        return new Border { Width = w, Height = h, CornerRadius = new CornerRadius(4), Margin = new Thickness(1), Background = Theme.B("#F4F4F8"), Child = stack };
    }

    // ---------- etiquetas, texto y PNG ----------
    void RefreshTags()
    {
        var t = _svc.Notes.GetTags(_hand.Id);
        _tags.Text = t.Count == 0 ? "" : "🏷 " + string.Join(", ", t);
    }

    void ShowTagMenu(Button anchor)
    {
        var current = _svc.Notes.GetTags(_hand.Id);
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var tag in NotesService.CommonTags.Concat(current).Distinct())
        {
            var t = tag;
            var item = new MenuItem { Header = t, IsCheckable = true, IsChecked = current.Contains(t) };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) _svc.Notes.AddTag(_hand.Id, t); else _svc.Notes.RemoveTag(_hand.Id, t);
                RefreshTags();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Nueva etiqueta…" };
        custom.Click += (_, _) =>
        {
            var name = Prompt.Ask(Window.GetWindow(this), "Nueva etiqueta", "Escribe la etiqueta (máx. 40 letras):");
            if (string.IsNullOrWhiteSpace(name)) return;
            try { _svc.Notes.AddTag(_hand.Id, name); RefreshTags(); }
            catch (ArgumentException ex) { MessageBox.Show(ex.Message, "Vergil HUD"); }
        };
        menu.Items.Add(custom);
        menu.IsOpen = true;
    }

    void CopyText()
    {
        try
        {
            Clipboard.SetText(HandText.Build(_hand, _inBb, anonymize: true));
            _status.Text = "Mano copiada al portapapeles (sin nombres: los rivales aparecen por su posición).";
        }
        catch (Exception ex) { _status.Text = "No se pudo copiar: " + ex.Message; }
    }

    void ExportPng()
    {
        var dlg = new SaveFileDialog { FileName = $"mano-{_hand.Id}.png", Filter = "Imagen PNG (*.png)|*.png" };
        if (dlg.ShowDialog() != true) return;
        SaveTablePng(dlg.FileName);
        _status.Text = "Imagen guardada: " + dlg.FileName;
    }

    /// <summary>Guarda la mesa tal como se ve ahora (con el paso actual) en un PNG.</summary>
    public void SaveTablePng(string path)
    {
        _table.Measure(new Size(W, H));
        _table.Arrange(new Rect(0, 0, W, H));
        _table.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)W * 2, (int)H * 2, 192, 192, PixelFormats.Pbgra32);
        rtb.Render(_table);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

/// <summary>Cuadro mínimo para pedir una línea de texto.</summary>
public static class Prompt
{
    public static string? Ask(Window? owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12), MinWidth = 300 };
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = Theme.Bg, Foreground = Theme.Text, ShowInTaskbar = false,
        };
        string? result = null;
        var ok = new Button { Content = "Aceptar", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        ok.Click += (_, _) => { result = box.Text; win.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Label(label, 13));
        root.Children.Add(box);
        root.Children.Add(buttons);
        win.Content = root;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        win.ShowDialog();
        return result;
    }
}
