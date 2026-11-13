using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Popup de un jugador con pestañas: Resumen, Por posición, Postflop y Showdowns (DESIGN.md §7.2).</summary>
public static class PlayerPopup
{
    static readonly Brush Grey = Brush(150, 150, 160);
    static readonly Brush White = Brush(238, 238, 242);
    static readonly Brush Panel = Brush(24, 24, 30, 245);
    static readonly Brush Green = Brush(110, 220, 120);
    static readonly Brush Red = Brush(255, 110, 95);

    static Brush Brush(byte r, byte g, byte b, byte a = 255)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    /// <summary>
    /// Ficha del jugador. Las estadísticas se calculan sobre un ámbito que se cambia con el botón de arriba: en una mesa, el mismo tipo de juego /
    /// todo el historial / solo esa mesa; fuera de una mesa, por tipo de juego. Nunca se mezcla dinero real con ficticio.
    /// </summary>
    public static UIElement Create(AppServices svc, string player, HudProfile profile, int initialTab = 0, bool editableNotes = false,
        HandContext? ctx = null, HudScope? initialScope = null)
    {
        var scopes = ctx != null ? StatScopes.ForHand(ctx) : StatScopes.General();
        int idx = ctx != null ? (int)(initialScope ?? svc.Settings.HudScope) : StatScopes.BestGeneral(svc.Engine, player);
        idx = Math.Clamp(idx, 0, scopes.Count - 1);

        var title = new TextBlock { Foreground = White, FontWeight = FontWeights.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var scopeBtn = new Button
        {
            Foreground = White, Background = Brush(48, 48, 58), BorderBrush = Brush(90, 90, 100), Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(10, 0, 10, 0), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = ctx != null ? Visibility.Collapsed : Visibility.Visible,   // en una mesa manda el botón flotante del HUD
        };
        var host = new ContentControl();
        int tab = Math.Clamp(initialTab, 0, 4);

        void Build()
        {
            var sc = scopes[idx];
            var tabs = new TabControl { Background = Panel, Foreground = White, BorderThickness = new Thickness(0), Width = 560, MaxHeight = 470 };
            tabs.Items.Add(Tab("Resumen", () => Summary(svc, player, sc)));
            tabs.Items.Add(Tab("Por posición", () => ByPosition(svc, player, sc.Filter)));
            tabs.Items.Add(Tab("Postflop", () => Postflop(svc, player, sc.Filter)));
            tabs.Items.Add(Tab("Showdowns", () => Showdowns(svc, player, sc.Filter)));
            tabs.Items.Add(Tab("Notas", () => NotesPanel.Create(svc, player, editableNotes)));
            tabs.SelectedIndex = Math.Clamp(tab, 0, tabs.Items.Count - 1);
            tabs.SelectionChanged += (_, e) => { if (e.OriginalSource == tabs) tab = tabs.SelectedIndex; };
            host.Content = tabs;
            title.Text = $"{player}   {(int)svc.Engine.Get(player, "vpip", sc.Filter).Denominator} manos";
            scopeBtn.Content = $"Ámbito: {sc.Name}  ⟳";
        }
        scopeBtn.Click += (_, _) => { idx = (idx + 1) % scopes.Count; Build(); };
        Build();

        var head = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        title.Margin = new Thickness(10, 0, 10, 4);
        head.Children.Add(title);
        head.Children.Add(scopeBtn);
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(host);
        return new Border
        {
            Background = Panel,
            BorderBrush = Brush(90, 90, 100),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = root,
        };
    }
    static TabItem Tab(string header, Func<UIElement> content)
    {
        UIElement body;
        try { body = content(); }
        catch (Exception ex) { body = new TextBlock { Text = "No se pudo cargar: " + ex.Message, Foreground = Red, Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap }; }
        return new TabItem
        {
            Header = header,
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 400, Content = body },
        };
    }

    // ---------- Resumen ----------
    static UIElement Summary(AppServices svc, string player, StatScope scope)
    {
        var rows = svc.Detail.Summary(player, scope.Filter, scope.PriorFormats);
        var g = Table(new[] { "Estadística", "Crudo", "Honesto", "Intervalo 80 %", "N", "Fiab." }, new[] { 150.0, 90, 60, 100, 40, 50 });
        int r = 1;
        foreach (var s in rows)
        {
            AddRow(g, r++, new[] { s.Label, s.Raw, s.Shown, s.Interval, s.N.ToString() }, s.Description);
            var bar = new Grid { Height = 4, Width = 40, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.02, s.Reliability), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.02 - Math.Max(0.02, s.Reliability), GridUnitType.Star) });
            var fill = new Border { Background = s.Reliability < 0.35 ? Red : s.Reliability < 0.7 ? Brush(220, 180, 70) : Green };
            bar.Children.Add(fill);
            Grid.SetRow(bar, r - 1);
            Grid.SetColumn(bar, 5);
            g.Children.Add(bar);
        }
        var host = new StackPanel { Margin = new Thickness(8) };
        host.Children.Add(Note("Honesto = valor ajustado con la media de la población (≈ lo que se espera de él). Crudo = x/n."));
        host.Children.Add(g);
        return host;
    }

    // ---------- Por posición ----------
    static UIElement ByPosition(AppServices svc, string player, StatFilter filter)
    {
        var data = svc.Detail.ByPosition(player, filter);
        var cols = new[] { "" }.Concat(PlayerDetailService.Positions).ToArray();
        var g = Table(cols, new[] { 70.0 }.Concat(PlayerDetailService.Positions.Select(_ => 66.0)).ToArray());
        int r = 1;
        foreach (var id in PlayerDetailService.PositionStats)
        {
            var def = svc.Engine.GetDefinition(id);
            var cells = new List<string> { def.Label };
            foreach (var pos in PlayerDetailService.Positions)
            {
                var v = data[pos][id];
                cells.Add(v.N == 0 ? "·" : $"{def.FormatValue(v)} ({v.N})");
            }
            AddRow(g, r++, cells.ToArray(), def.Description);
        }
        var host = new StackPanel { Margin = new Thickness(8) };
        host.Children.Add(Note("Entre paréntesis, el tamaño de la muestra. Valores en crudo: con pocas manos no son fiables."));
        host.Children.Add(g);
        return host;
    }

    // ---------- Postflop ----------
    static UIElement Postflop(AppServices svc, string player, StatFilter filter)
    {
        var rep = svc.Detail.Postflop(player, filter);
        var host = new StackPanel { Margin = new Thickness(8) };

        string Fmt(StatValue v) => v.N == 0 ? "·" : $"{(v.Value!.Value * 100):0} ({v.N})";

        var g1 = Table(new[] { "", "Flop", "Turn", "River" }, new[] { 130.0, 80, 80, 80 });
        int r = 1;
        foreach (var row in rep.Rows)
            AddRow(g1, r++, new[] { row.Label, Fmt(row.ByStreet["flop"]), Fmt(row.ByStreet["turn"]), Fmt(row.ByStreet["river"]) });
        host.Children.Add(g1);

        host.Children.Add(Heading("Fold según el tamaño de la apuesta que enfrenta"));
        var g2 = Table(new[] { "Tamaño", "Flop", "Turn", "River" }, new[] { 130.0, 80, 80, 80 });
        r = 1;
        var labels = new Dictionary<string, string>
        {
            ["small"] = "Pequeña (<40 %)", ["medium"] = "Media (40-80 %)", ["large"] = "Grande (80-120 %)", ["over"] = "Overbet (>120 %)",
        };
        foreach (var row in rep.FoldBySize)
            AddRow(g2, r++, new[] { labels[row.Bucket], Fmt(row.ByStreet["flop"]), Fmt(row.ByStreet["turn"]), Fmt(row.ByStreet["river"]) });
        host.Children.Add(g2);

        host.Children.Add(Heading("Tamaño habitual de sus apuestas (mediana, % del bote)"));
        string M(string k) => rep.MedianBetPct[k] is { } m ? $"{m:0} %" : "·";
        host.Children.Add(new TextBlock { Text = $"Flop {M("flop")}    Turn {M("turn")}    River {M("river")}", Foreground = White, Margin = new Thickness(0, 2, 0, 0) });
        return host;
    }

    // ---------- Showdowns ----------
    static UIElement Showdowns(AppServices svc, string player, StatFilter filter)
    {
        var rows = svc.Detail.Showdowns(player, 20, filter);
        var host = new StackPanel { Margin = new Thickness(8) };
        if (rows.Count == 0)
        {
            host.Children.Add(Note("Aún no hay showdowns con cartas vistas de este jugador."));
            return host;
        }
        host.Children.Add(Note("Línea jugada en cada mano mostrada. X = check, C = call, F = fold, R = raise, B n % = apuesta de n % del bote."));
        var g = Table(new[] { "Cartas", "Línea", "bb" }, new[] { 70.0, 380, 55 });
        int r = 1;
        foreach (var s in rows)
        {
            AddRow(g, r, new[] { s.Cards ?? "?", s.Line, $"{s.NetBb:+0.0;-0.0;0.0}" });
            var tb = g.Children.OfType<TextBlock>().Last();
            tb.Foreground = s.NetBb > 0 ? Green : s.NetBb < 0 ? Red : Grey;
            r++;
        }
        host.Children.Add(g);
        return host;
    }

    // ---------- ayudas de interfaz ----------
    static TextBlock Note(string t) => new() { Text = t, Foreground = Grey, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), MaxWidth = 520 };

    static TextBlock Heading(string t) => new() { Text = t, Foreground = White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    static Grid Table(string[] headers, double[] widths)
    {
        var g = new Grid();
        for (int c = 0; c < headers.Length; c++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths.Length > c ? widths[c] : 60) });
            var h = new TextBlock { Text = headers[c], Foreground = Grey, FontSize = 11, Margin = new Thickness(0, 0, 8, 3) };
            Grid.SetColumn(h, c);
            g.Children.Add(h);
        }
        g.RowDefinitions.Add(new RowDefinition());
        return g;
    }

    static void AddRow(Grid g, int row, string[] cells, string? tooltip = null)
    {
        while (g.RowDefinitions.Count <= row) g.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < cells.Length; c++)
        {
            var tb = new TextBlock
            {
                Text = cells[c],
                Foreground = c == 0 ? White : Brush(215, 215, 222),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 1, 8, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (tooltip != null && c == 0) tb.ToolTip = tooltip;
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, c);
            g.Children.Add(tb);
        }
    }
}
