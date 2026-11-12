using ScottPlot;
using VergilHud.Core;
using Color = ScottPlot.Color;

namespace VergilHud.App;

/// <summary>Opciones de la gráfica de ganancias.</summary>
public sealed class ProfitChartOptions
{
    public bool ShowNet { get; set; } = true;
    public bool ShowEv { get; set; } = true;
    public bool ShowShowdown { get; set; } = true;
    public bool ShowNoShowdown { get; set; } = true;
    public bool ShowBand { get; set; } = true;
}

/// <summary>Construye las gráficas sobre un Plot de ScottPlot (sirve tanto para el control en pantalla como para dibujar a PNG).</summary>
public static class Charts
{
    public static readonly Color Blue = Color.FromHex("#6AA9FF");
    public static readonly Color Green = Color.FromHex("#6EDC78");
    public static readonly Color Red = Color.FromHex("#FF6E5F");
    public static readonly Color Yellow = Color.FromHex("#F0DC5A");
    public static readonly Color Grey = Color.FromHex("#8A8A96");

    public static void Style(Plot p)
    {
        p.FigureBackground.Color = Color.FromHex("#1B1C21");
        p.DataBackground.Color = Color.FromHex("#23242B");
        p.Axes.Color(Color.FromHex("#C8C8D0"));
        p.Grid.MajorLineColor = Color.FromHex("#3A3C45");
        p.Legend.BackgroundColor = Color.FromHex("#23242B");
        p.Legend.FontColor = Color.FromHex("#E6E6E6");
        p.Legend.OutlineColor = Color.FromHex("#3A3C45");
    }

    static void Line(Plot p, double[] xs, double[] ys, Color color, string legend, double width = 2, bool dashed = false)
    {
        var s = p.Add.ScatterLine(xs, ys);
        s.Color = color;
        s.LineWidth = (float)width;
        s.LegendText = legend;
        if (dashed) s.LinePattern = LinePattern.Dashed;
    }

    /// <summary>Ganancia acumulada en bb: real, EV de all-ins, con y sin showdown, y banda de varianza alrededor de tu winrate.</summary>
    public static void Profit(Plot p, ProfitSeries s, ProfitChartOptions o)
    {
        p.Clear();
        Style(p);
        var pts = s.Points;
        if (pts.Count == 0)
        {
            p.Title("Sin manos en este periodo");
            return;
        }
        var x = pts.Select(t => (double)t.Hand).ToArray();

        if (o.ShowBand && s.SdPerHand > 0)
        {
            // La banda parte del origen: tu ganancia esperada (media × manos) ± 1 y 2 desviaciones típicas.
            foreach (var (k, a) in new[] { (2.0, 0.55), (1.0, 0.85) })
            {
                var lo = x.Select(i => s.Band((int)i, k).Lo).ToArray();
                var hi = x.Select(i => s.Band((int)i, k).Hi).ToArray();
                Line(p, x, lo, Grey.WithAlpha(a), k == 1 ? "Banda ±1σ" : "Banda ±2σ", 1, dashed: true);
                Line(p, x, hi, Grey.WithAlpha(a), "", 1, dashed: true);
            }
            Line(p, x, x.Select(i => s.MeanPerHand * i).ToArray(), Yellow.WithAlpha(0.8), "Tu winrate medio", 1.5, dashed: true);
        }
        if (o.ShowNoShowdown) Line(p, x, pts.Select(t => t.NoShowdown).ToArray(), Red, "Sin showdown", 1.5);
        if (o.ShowShowdown) Line(p, x, pts.Select(t => t.Showdown).ToArray(), Blue.WithAlpha(0.8), "Con showdown", 1.5);
        if (o.ShowEv) Line(p, x, pts.Select(t => t.Ev).ToArray(), Green, "EV de all-ins", 2);
        if (o.ShowNet) Line(p, x, pts.Select(t => t.Net).ToArray(), Color.FromHex("#FFFFFF"), "Resultado real", 2.5);

        p.Axes.Bottom.Label.Text = "Manos";
        p.Axes.Left.Label.Text = "Ganancia acumulada (bb)";
        p.ShowLegend(Alignment.UpperLeft);
        p.Axes.AutoScale();
    }

    /// <summary>Barras con colores según el signo (verde gana, rojo pierde) y etiquetas bajo cada barra.</summary>
    public static void Bars(Plot p, IReadOnlyList<string> labels, IReadOnlyList<double> values, string yLabel, string? title = null, bool signColors = true)
    {
        p.Clear();
        Style(p);
        if (values.Count == 0)
        {
            p.Title("Sin datos");
            return;
        }
        var bars = p.Add.Bars(values.ToArray());
        for (int i = 0; i < bars.Bars.Count; i++)
        {
            bars.Bars[i].FillColor = !signColors ? Blue.WithAlpha(0.85) : values[i] >= 0 ? Green.WithAlpha(0.85) : Red.WithAlpha(0.85);
            bars.Bars[i].LineWidth = 0;
        }
        var ticks = labels.Select((l, i) => new Tick(i, l)).ToArray();
        p.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
        p.Axes.Left.Label.Text = yLabel;
        if (title != null) p.Title(title);
        p.Axes.Margins(bottom: values.Min() < 0 ? 0.1 : 0, top: 0.1);
        p.Axes.AutoScale();
    }

    /// <summary>Beneficio acumulado por torneo (en la unidad de la inscripción).</summary>
    public static void TournamentProfit(Plot p, IReadOnlyList<TournamentRow> rows)
    {
        p.Clear();
        Style(p);
        var done = rows.Where(r => r.Finished).ToList();
        if (done.Count == 0)
        {
            p.Title("Aún no hay torneos terminados");
            return;
        }
        double acc = 0;
        var xs = new List<double> { 0 };
        var ys = new List<double> { 0 };
        for (int i = 0; i < done.Count; i++)
        {
            acc += done[i].Profit;
            xs.Add(i + 1);
            ys.Add(acc);
        }
        Line(p, xs.ToArray(), ys.ToArray(), Green, "Beneficio acumulado", 2.5);
        Line(p, new[] { 0.0, xs[^1] }, new[] { 0.0, 0.0 }, Grey, "", 1, dashed: true);
        p.Axes.Bottom.Label.Text = "Torneos";
        p.Axes.Left.Label.Text = "Beneficio acumulado";
        p.Axes.AutoScale();
    }
}
