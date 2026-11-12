using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VergilHud.Core;

/// <summary>Rango objetivo de una estadística (en proporción 0..1), opcionalmente para ciertas posiciones.</summary>
public sealed class LeakBaseline
{
    public string Id { get; set; } = "";
    public string Stat { get; set; } = "";
    public string Label { get; set; } = "";
    public double Lo { get; set; }
    public double Hi { get; set; }
    public List<string>? Positions { get; set; }
    public string? Hint { get; set; }
}

public sealed record Leak(LeakBaseline Baseline, double X, double N, double Posterior, double Lo, double Hi, bool Above,
    double SpotNetBb, double ImpactBb100)
{
    public string Direction => Above ? "por encima" : "por debajo";
}

/// <summary>
/// Detector de leaks bayesiano (DESIGN.md §10): solo es leak si TODO el intervalo del 90 % cae fuera del rango objetivo.
/// El impacto es lo que pierdes (o ganas) en esos spots respecto a tu media, en bb/100 de todas tus manos.
/// </summary>
public sealed class LeakDetector
{
    const double Z90 = 1.6448536269514722;
    const double PriorStrength = 15;
    public const int MinSample = 15;

    readonly HudDatabase _db;
    readonly StatEngine _engine;

    public LeakDetector(HudDatabase db, StatEngine engine)
    {
        _db = db;
        _engine = engine;
    }

    public static List<LeakBaseline> LoadBaselines(string? json = null)
    {
        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (json != null) return JsonSerializer.Deserialize<List<LeakBaseline>>(json, opt) ?? new();
        using var s = typeof(LeakDetector).Assembly.GetManifestResourceStream("leak-baselines.json")
                      ?? throw new InvalidOperationException("Falta el recurso leak-baselines.json");
        return JsonSerializer.Deserialize<List<LeakBaseline>>(s, opt) ?? new();
    }

    /// <summary>Intervalo del 90 % de la proporción real con prior centrado en el rango objetivo (así 20 muestras no bastan para condenar).</summary>
    public static (double Mean, double Lo, double Hi) Credible(double x, double n, double priorMean)
    {
        double a = x + priorMean * PriorStrength, b = n - x + (1 - priorMean) * PriorStrength, s = a + b;
        double mean = a / s, sd = Math.Sqrt(a * b / (s * s * (s + 1)));
        return (mean, Math.Max(0, mean - Z90 * sd), Math.Min(1, mean + Z90 * sd));
    }

    public List<Leak> Detect(string hero, IReadOnlyList<LeakBaseline>? baselines = null, IReadOnlyList<string>? formats = null)
    {
        baselines ??= LoadBaselines();
        var result = new List<Leak>();
        var (allNet, allN) = Overall(formats);
        double overallAvg = allN > 0 ? allNet / allN : 0;

        foreach (var b in baselines)
        {
            if (!_engine.Has(b.Stat)) continue;
            var filter = new StatFilter { Positions = b.Positions, Formats = formats };
            var v = _engine.Get(hero, b.Stat, filter);
            if (v.Denominator < MinSample) continue;
            var (mean, lo, hi) = Credible(v.Numerator, v.Denominator, (b.Lo + b.Hi) / 2);
            bool above = lo > b.Hi, below = hi < b.Lo;
            if (!above && !below) continue;

            var (spotNet, spotN) = SpotNet(_engine.GetDefinition(b.Stat).Denominator, b.Positions, formats);
            double spotAvg = spotN > 0 ? spotNet / spotN : 0;
            double impact = allN > 0 ? (spotAvg - overallAvg) * spotN / allN * 100 : 0;
            result.Add(new Leak(b, v.Numerator, v.Denominator, mean, lo, hi, above, spotAvg, impact));
        }
        // Primero lo que más dinero cuesta; a igualdad, lo más desviado del rango
        return result.OrderBy(l => l.ImpactBb100).ThenByDescending(Distance).ToList();
    }

    static double Distance(Leak l) => l.Above ? l.Lo - l.Baseline.Hi : l.Baseline.Lo - l.Hi;

    (double Net, int N) Overall(IReadOnlyList<string>? formats)
    {
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            var fmt = FormatClause(cmd, formats);
            cmd.CommandText = $"SELECT COALESCE(SUM(net_bb),0), COUNT(*) FROM player_hand_facts WHERE is_hero = 1{fmt}";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetDouble(0), r.GetInt32(1));
        });
    }

    (double Net, int N) SpotNet(string denominatorDsl, IReadOnlyList<string>? positions, IReadOnlyList<string>? formats)
    {
        var c = StatDsl.Compile(denominatorDsl, "lk");
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            foreach (var (k, v) in c.Parameters) cmd.Parameters.AddWithValue(k, v);
            var fmt = FormatClause(cmd, formats);
            var pos = "";
            if (positions is { Count: > 0 })
            {
                pos = $" AND position IN ({string.Join(",", positions.Select((_, i) => "$lp" + i))})";
                for (int i = 0; i < positions.Count; i++) cmd.Parameters.AddWithValue("$lp" + i, positions[i]);
            }
            cmd.CommandText = $"SELECT COALESCE(SUM(net_bb),0), COUNT(*) FROM player_hand_facts WHERE is_hero = 1{fmt}{pos} " +
                              $"AND ({c.Sql}) IS NOT NULL AND ({c.Sql}) != 0";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetDouble(0), r.GetInt32(1));
        });
    }

    static string FormatClause(SqliteCommand cmd, IReadOnlyList<string>? formats)
    {
        if (formats is not { Count: > 0 }) return "";
        for (int i = 0; i < formats.Count; i++) cmd.Parameters.AddWithValue("$lf" + i, formats[i]);
        return $" AND format IN ({string.Join(",", formats.Select((_, i) => "$lf" + i))})";
    }

    /// <summary>Filtro de la lista de manos que representa el spot de un leak (para «Ver manos»).</summary>
    public HandFilter SpotFilter(Leak l) =>
        new(Scope: "all", SpotDsl: _engine.GetDefinition(l.Baseline.Stat).Denominator, Positions: l.Baseline.Positions);

    public static string Describe(Leak l)
    {
        string pos = l.Baseline.Positions is { Count: > 0 } ? $" ({string.Join("/", l.Baseline.Positions)})" : "";
        return $"{l.Baseline.Label}{pos}: observado {l.X / l.N * 100:0}%, estimación {l.Posterior * 100:0}% (intervalo del 90 %: {l.Lo * 100:0}–{l.Hi * 100:0}%) frente a un objetivo de " +
               $"{l.Baseline.Lo * 100:0}–{l.Baseline.Hi * 100:0}%, con {l.N:0} oportunidades.";
    }
}
