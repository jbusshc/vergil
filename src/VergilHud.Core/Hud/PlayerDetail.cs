using Microsoft.Data.Sqlite;

namespace VergilHud.Core;

public sealed record SummaryRow(string Id, string Label, string Description, string Raw, string Shown, string Interval, int N, double Reliability);

public sealed record ShowdownRow(DateTime Time, string HandId, string? Cards, string Line, double NetBb);

public sealed record PostflopRow(string Label, IReadOnlyDictionary<string, StatValue> ByStreet);

public sealed record SizeFoldRow(string Bucket, IReadOnlyDictionary<string, StatValue> ByStreet);

public sealed record PostflopReport(
    IReadOnlyList<PostflopRow> Rows,
    IReadOnlyList<SizeFoldRow> FoldBySize,
    IReadOnlyDictionary<string, double?> MedianBetPct);

/// <summary>Datos de las pestañas del popup de un jugador (§7.2). Solo estadísticas históricas de manos ya jugadas.</summary>
public sealed class PlayerDetailService
{
    public static readonly string[] Positions = { "UTG", "UTG1", "UTG2", "MP", "HJ", "CO", "BTN", "SB", "BB" };
    public static readonly string[] PositionStats = { "rfi", "3bet", "f3b", "steal", "fts", "cold_call" };
    public static readonly string[] Buckets = { "small", "medium", "large", "over" };
    static readonly string[] Streets = { "f", "t", "r" };

    readonly HudDatabase _db;
    readonly StatEngine _engine;
    readonly PopulationPriors _priors;

    public PlayerDetailService(HudDatabase db, StatEngine engine, PopulationPriors priors)
    {
        _db = db;
        _engine = engine;
        _priors = priors;
        // "Fold según el tamaño de la apuesta que enfrenta", por calle y cubo.
        foreach (var x in Streets)
            foreach (var b in Buckets)
            {
                var id = $"fold_{x}_{b}";
                if (!_engine.Has(id))
                    _engine.Register(new StatDefinition
                    {
                        Id = id,
                        Label = $"F {x} {b}",
                        Numerator = $"{x}_folds > 0 && {x}_faced_size_bucket == \"{b}\"",
                        Denominator = $"{x}_faced_size_bucket == \"{b}\"",
                        Format = "pct0",
                    });
            }
    }

    /// <summary>Todas las estadísticas con crudo, valor posterior, intervalo creíble y fiabilidad.</summary>
    public List<SummaryRow> Summary(string player, StatFilter? filter = null, IReadOnlyList<string>? priorFormats = null)
    {
        var defs = _engine.Definitions.Where(d => !d.Id.StartsWith("fold_", StringComparison.Ordinal)).ToList();
        var vals = _engine.GetMany(new[] { player }, defs.Select(d => d.Id).ToList(), filter)[player];
        var rows = new List<SummaryRow>();
        foreach (var d in defs)
        {
            var v = vals[d.Id];
            if (d.IsProportion)
            {
                var b = Bayes.Posterior(v.Numerator, v.Denominator, _priors.Mean(d, priorFormats), d.Prior?.Strength ?? 10);
                string Pct(double x) => d.FormatValue(new StatValue(x, 1));
                rows.Add(new SummaryRow(d.Id, d.Label, d.Description,
                    v.N == 0 ? "-" : $"{d.FormatValue(v)} ({v.Numerator:0}/{v.Denominator:0})",
                    Pct(b.Posterior), $"{Pct(b.Lo)}–{Pct(b.Hi)}", v.N, b.Reliability));
            }
            else
            {
                var t = d.FormatValue(v);
                rows.Add(new SummaryRow(d.Id, d.Label, d.Description, t, t, "", v.N, Math.Clamp(v.Denominator / 30.0, 0, 1)));
            }
        }
        return rows;
    }

    /// <summary>Estadísticas de preflop por posición (tabla posición × estadística).</summary>
    public Dictionary<string, Dictionary<string, StatValue>> ByPosition(string player, StatFilter? filter = null)
    {
        var result = new Dictionary<string, Dictionary<string, StatValue>>();
        foreach (var pos in Positions)
        {
            var f = (filter ?? new StatFilter()) with { Positions = new[] { pos } };
            result[pos] = _engine.GetMany(new[] { player }, PositionStats, f)[player];
        }
        return result;
    }

    public PostflopReport Postflop(string player, StatFilter? filter = null)
    {
        (string Label, string[] Ids)[] map =
        {
            ("C-Bet / barril", new[] { "cbet_flop", "cbet_turn", "cbet_river" }),
            ("Fold a C-Bet", new[] { "fcb_flop", "fcb_turn", "fcb_river" }),
            ("Check-raise", new[] { "check_raise_flop", "check_raise_turn", "" }),
            ("Donk", new[] { "donk_flop", "", "" }),
            ("Float", new[] { "float_flop", "", "" }),
            ("Probe", new[] { "", "probe_turn", "probe_river" }),
        };
        var ids = map.SelectMany(m => m.Ids).Where(i => i != "").ToList();
        ids.AddRange(Buckets.SelectMany(b => Streets.Select(x => $"fold_{x}_{b}")));
        var vals = _engine.GetMany(new[] { player }, ids, filter)[player];

        var rows = map.Select(m => new PostflopRow(m.Label, new Dictionary<string, StatValue>
        {
            ["flop"] = m.Ids[0] != "" ? vals[m.Ids[0]] : default,
            ["turn"] = m.Ids[1] != "" ? vals[m.Ids[1]] : default,
            ["river"] = m.Ids[2] != "" ? vals[m.Ids[2]] : default,
        })).ToList();

        var sizes = Buckets.Select(b => new SizeFoldRow(b, new Dictionary<string, StatValue>
        {
            ["flop"] = vals[$"fold_f_{b}"],
            ["turn"] = vals[$"fold_t_{b}"],
            ["river"] = vals[$"fold_r_{b}"],
        })).ToList();

        return new PostflopReport(rows, sizes, new Dictionary<string, double?>
        {
            ["flop"] = MedianBetPct(player, "f_bet_size_pct"),
            ["turn"] = MedianBetPct(player, "t_bet_size_pct"),
            ["river"] = MedianBetPct(player, "r_bet_size_pct"),
        });
    }

    /// <summary>Mediana del tamaño de sus apuestas (% del bote) en esa calle. Null si nunca ha apostado.</summary>
    public double? MedianBetPct(string player, string factColumn)
    {
        if (!FactCatalog.Contains(factColumn) || FactCatalog.Get(factColumn).Kind != FactKind.Real)
            throw new ArgumentException($"'{factColumn}' no es un hecho numérico");
        var names = _engine.HeroNames.Contains(player) ? _engine.HeroNames.ToList() : new List<string> { player };
        var values = _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            var ps = new List<string>();
            for (int i = 0; i < names.Count; i++) { ps.Add("$p" + i); cmd.Parameters.AddWithValue("$p" + i, names[i]); }
            // El nombre de la columna sale de la lista blanca del catálogo (validado arriba).
            cmd.CommandText = $"SELECT {factColumn} FROM player_hand_facts WHERE player IN ({string.Join(",", ps)}) AND {factColumn} IS NOT NULL ORDER BY {factColumn}";
            using var r = cmd.ExecuteReader();
            var l = new List<double>();
            while (r.Read()) l.Add(r.GetDouble(0));
            return l;
        });
        if (values.Count == 0) return null;
        int m = values.Count / 2;
        return values.Count % 2 == 1 ? values[m] : (values[m - 1] + values[m]) / 2;
    }

    /// <summary>Últimos showdowns con cartas conocidas, con la línea jugada en una sola fila.</summary>
    public List<ShowdownRow> Showdowns(string player, int limit = 20)
    {
        var names = _engine.HeroNames.Contains(player) ? _engine.HeroNames.ToList() : new List<string> { player };
        var rows = _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            var ps = new List<string>();
            for (int i = 0; i < names.Count; i++) { ps.Add("$p" + i); cmd.Parameters.AddWithValue("$p" + i, names[i]); }
            cmd.CommandText = "SELECT h.hand_id, h.played_at, h.raw, f.hole_cards, f.net_bb, f.player FROM player_hand_facts f " +
                              "JOIN hands h ON h.hand_id = f.hand_id " +
                              $"WHERE f.player IN ({string.Join(",", ps)}) AND f.wtsd = 1 AND f.hole_cards IS NOT NULL " +
                              "ORDER BY f.played_at DESC, f.hand_id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var l = new List<(string, string, string, string?, double, string)>();
            while (r.Read())
                l.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? 0 : r.GetDouble(4), r.GetString(5)));
            return l;
        });

        var result = new List<ShowdownRow>();
        foreach (var (id, at, raw, cards, netBb, who) in rows)
        {
            var hand = PokerStarsParser.Parse(raw);
            if (hand == null) continue;
            DateTime.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t);
            result.Add(new ShowdownRow(t, id, cards, HandLine.Build(hand, who), netBb));
        }
        return result;
    }
}
