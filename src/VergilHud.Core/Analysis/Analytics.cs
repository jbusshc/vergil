using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VergilHud.Core;

/// <summary>Intervalo de tiempo (en UTC, como las manos). Null = sin límite.</summary>
public readonly record struct Period(DateTime? From, DateTime? To)
{
    public static Period All => new(null, null);
    public static Period Today(DateTime nowUtc) => new(nowUtc.Date, null);
    public static Period LastDays(int days, DateTime nowUtc) => new(nowUtc.AddDays(-days), null);
    public static Period ThisYear(DateTime nowUtc) => new(new DateTime(nowUtc.Year, 1, 1), null);
}

public sealed record Overview(int Hands, double NetBb, double BbPer100, double EvNetBb, double EvBbPer100, double RakeBb, double Hours, double HandsPerHour);

public sealed record ProfitPoint(int Hand, DateTime Time, double Net, double Ev, double NoShowdown, double Showdown);

/// <param name="MeanPerHand">Ganancia media por mano en bb.</param>
/// <param name="SdPerHand">Desviación típica por mano en bb.</param>
public sealed record ProfitSeries(IReadOnlyList<ProfitPoint> Points, int Hands, double MeanPerHand, double SdPerHand)
{
    /// <summary>Banda de varianza: tu ganancia esperada ± k desviaciones típicas tras n manos (la varianza crece con √n).</summary>
    public (double Lo, double Hi) Band(int hand, double sigmas) =>
        (MeanPerHand * hand - sigmas * SdPerHand * Math.Sqrt(hand), MeanPerHand * hand + sigmas * SdPerHand * Math.Sqrt(hand));
}

public sealed record GroupRow(string Key, int Hands, double NetBb, double BbPer100, double EvBbPer100, double VpipPct, double PfrPct);

public sealed record MatrixCell(string Name, int Hands, int Played, double NetBb)
{
    public double BbPer100 => Hands == 0 ? 0 : NetBb * 100.0 / Hands;
    public double PlayedPct => Hands == 0 ? 0 : Played * 100.0 / Hands;
}

public sealed record HandRow(string HandId, DateTime Time, string? Cards, string Position, double NetBb, double EvNetBb);

public sealed record HandFilter(string Scope = "all", string? Tag = null, string? Text = null, bool OnlyShowdown = false, Period? Period = null, int Limit = 500,
    string? SpotDsl = null, IReadOnlyList<string>? Positions = null);

public sealed record HandListRow(string HandId, DateTime Time, string Format, string? Cards, string Position, double NetBb, double EvNetBb, string Tags);

/// <summary>Consultas de análisis sobre las manos del héroe (todos sus nicks, gracias a is_hero).</summary>
public sealed class Analytics
{
    readonly HudDatabase _db;

    public Analytics(HudDatabase db) => _db = db;

    public static readonly string[] PositionOrder = { "UTG", "UTG1", "UTG2", "MP", "HJ", "CO", "BTN", "SB", "BB" };

    /// <summary>Dimensiones por las que se pueden agrupar los informes (lista blanca: el SQL sale de aquí, no del usuario).</summary>
    public static readonly IReadOnlyDictionary<string, string> Dimensions = new Dictionary<string, string>
    {
        ["position"] = "position",
        ["stack"] = "stack_bucket",
        ["players"] = "CAST(players_dealt AS TEXT)",
        ["month"] = "substr(played_at, 1, 7)",
        ["limit"] = "CAST(big_blind AS TEXT)",
        ["hour"] = "strftime('%H', played_at)",
        ["weekday"] = "strftime('%w', played_at)",
        ["format"] = "format",
    };

    static (string Where, Action<SqliteCommand> Bind) Filter(Period p, string scope)
    {
        var w = new List<string> { "is_hero = 1" };
        switch (scope)
        {
            case "cash": w.Add("format IN ('cash','zoom')"); break;
            case "tournament": w.Add("format IN ('mtt','sng','spin')"); break;
            case "spin": w.Add("format = 'spin'"); break;
            case "mtt": w.Add("format IN ('mtt','sng')"); break;
            case "all": break;
            default: throw new ArgumentException($"Ámbito desconocido '{scope}'");
        }
        if (p.From != null) w.Add("played_at >= $from");
        if (p.To != null) w.Add("played_at <= $to");
        return (string.Join(" AND ", w), cmd =>
        {
            if (p.From is { } f) cmd.Parameters.AddWithValue("$from", f.ToString("s"));
            if (p.To is { } t) cmd.Parameters.AddWithValue("$to", t.ToString("s"));
        });
    }

    // ---------- resumen ----------
    public Overview GetOverview(Period period, string scope = "cash")
    {
        SessionService.EnsureUpToDate(_db);
        var (where, bind) = Filter(period, scope);
        var (hands, net, ev, rake) = _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            bind(cmd);
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(net_bb),0), COALESCE(SUM(ev_net_bb),0), " +
                              "COALESCE(SUM(CASE WHEN big_blind > 0 THEN rake_paid / big_blind ELSE 0 END),0) " +
                              $"FROM player_hand_facts WHERE {where}";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt32(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
        });

        var sessions = SessionService.GetSessions(_db, period);
        double hours = sessions.Sum(s => Math.Max(s.Duration.TotalHours, s.Hands > 0 ? 1.0 / 60 : 0));
        return new Overview(hands, net, hands == 0 ? 0 : net * 100.0 / hands, ev, hands == 0 ? 0 : ev * 100.0 / hands, rake,
            hours, hours <= 0 ? 0 : sessions.Sum(s => s.Hands) / hours);
    }

    // ---------- ganancias acumuladas ----------
    /// <summary>
    /// Ganancia acumulada en bb: real, EV de all-ins y desglose sin showdown / con showdown. Se reduce a unos pocos
    /// miles de puntos para dibujarla, sin perder el último.
    /// </summary>
    public ProfitSeries GetProfitSeries(Period period, string scope = "cash", int maxPoints = 2000)
    {
        var (where, bind) = Filter(period, scope);
        return _db.Query(conn =>
        {
            using var count = conn.CreateCommand();
            bind(count);
            count.CommandText = $"SELECT COUNT(*) FROM player_hand_facts WHERE {where}";
            int n = Convert.ToInt32(count.ExecuteScalar());
            int step = Math.Max(1, (int)Math.Ceiling(n / (double)Math.Max(1, maxPoints)));

            using var cmd = conn.CreateCommand();
            bind(cmd);
            cmd.CommandText = $"SELECT played_at, net_bb, ev_net_bb, wtsd FROM player_hand_facts WHERE {where} ORDER BY played_at, hand_id";
            using var r = cmd.ExecuteReader();

            // Se recorre en streaming: solo se lee la fecha de los puntos que se dibujan.
            var points = new List<ProfitPoint>(Math.Min(n, maxPoints) + 1);
            double net = 0, ev = 0, noSd = 0, sd = 0;
            double mean = 0, m2 = 0;   // Welford: media y varianza por mano
            int i = 0;
            while (r.Read())
            {
                double nb = r.IsDBNull(1) ? 0 : r.GetDouble(1), eb = r.IsDBNull(2) ? 0 : r.GetDouble(2);
                net += nb; ev += eb;
                if (r.GetInt32(3) == 1) sd += nb; else noSd += nb;
                double d = nb - mean;
                mean += d / (i + 1);
                m2 += d * (nb - mean);
                if (i % step == 0 || i == n - 1)
                    points.Add(new ProfitPoint(i + 1, DateTime.Parse(r.GetString(0), CultureInfo.InvariantCulture), net, ev, noSd, sd));
                i++;
            }
            return new ProfitSeries(points, i, i == 0 ? 0 : net / i, i < 2 ? 0 : Math.Sqrt(m2 / (i - 1)));
        });
    }

    // ---------- informes por grupo ----------
    public List<GroupRow> GroupBy(string dimension, Period period, string scope = "cash")
    {
        if (!Dimensions.TryGetValue(dimension, out var expr)) throw new ArgumentException($"Dimensión desconocida '{dimension}'");
        var (where, bind) = Filter(period, scope);
        var rows = _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            bind(cmd);
            cmd.CommandText = $"SELECT {expr} AS k, COUNT(*), COALESCE(SUM(net_bb),0), COALESCE(SUM(ev_net_bb),0), SUM(pf_vpip), SUM(pf_pfr) " +
                              $"FROM player_hand_facts WHERE {where} GROUP BY k";
            using var r = cmd.ExecuteReader();
            var l = new List<GroupRow>();
            while (r.Read())
            {
                int n = r.GetInt32(1);
                l.Add(new GroupRow(r.IsDBNull(0) ? "?" : r.GetString(0), n, r.GetDouble(2), r.GetDouble(2) * 100.0 / n,
                    r.GetDouble(3) * 100.0 / n, r.GetInt32(4) * 100.0 / n, r.GetInt32(5) * 100.0 / n));
            }
            return l;
        });

        return dimension switch
        {
            "position" => rows.OrderBy(x => Array.IndexOf(PositionOrder, x.Key) is var i && i < 0 ? 99 : i).ToList(),
            "stack" => rows.OrderBy(x => x.Key switch { "<10" => 0, "10-20" => 1, "20-40" => 2, "40+" => 3, _ => 4 }).ToList(),
            "players" or "hour" or "weekday" => rows.OrderBy(x => int.TryParse(x.Key, out var v) ? v : 99).ToList(),
            "limit" => rows.OrderBy(x => double.TryParse(x.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList(),
            _ => rows.OrderBy(x => x.Key, StringComparer.Ordinal).ToList(),
        };
    }

    // ---------- matriz de manos 13×13 ----------
    /// <summary>Resultado por cartas de salida. Filas y columnas de A (índice 0) a 2 (12): pares en la diagonal, suited arriba, offsuit abajo.</summary>
    public MatrixCell[,] GetHandMatrix(Period period, string scope = "cash")
    {
        var (where, bind) = Filter(period, scope);
        var agg = new Dictionary<string, (int N, int Played, double Net)>();
        _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            bind(cmd);
            cmd.CommandText = $"SELECT hole_cards, COUNT(*), SUM(pf_vpip), COALESCE(SUM(net_bb),0) FROM player_hand_facts " +
                              $"WHERE {where} AND hole_cards IS NOT NULL GROUP BY hole_cards";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = Cards.HandName(r.GetString(0));
                if (name == "") continue;
                var a = agg.GetValueOrDefault(name);
                agg[name] = (a.N + r.GetInt32(1), a.Played + r.GetInt32(2), a.Net + r.GetDouble(3));
            }
            return 0;
        });

        var m = new MatrixCell[13, 13];
        string R(int i) => Cards.RankChars[12 - i].ToString();
        for (int i = 0; i < 13; i++)
            for (int j = 0; j < 13; j++)
            {
                string name = i == j ? R(i) + R(j) : i < j ? R(i) + R(j) + "s" : R(j) + R(i) + "o";
                var a = agg.GetValueOrDefault(name);
                m[i, j] = new MatrixCell(name, a.N, a.Played, a.Net);
            }
        return m;
    }

    /// <summary>Lista de manos del héroe, más recientes primero, con filtros por formato, etiqueta, texto y showdown.</summary>
    public List<HandListRow> ListHands(HandFilter f)
    {
        var (where, bind) = Filter(f.Period ?? Period.All, f.Scope);
        var extra = new List<string>();
        if (f.OnlyShowdown) extra.Add("wtsd = 1");
        if (!string.IsNullOrWhiteSpace(f.Tag)) extra.Add("EXISTS (SELECT 1 FROM hand_tags t WHERE t.hand_id = f.hand_id AND t.tag = $tag)");
        if (!string.IsNullOrWhiteSpace(f.Text)) extra.Add("(f.hand_id LIKE $q OR f.hole_cards LIKE $q OR f.position LIKE $q)");
        CompiledExpr? spot = null;
        if (!string.IsNullOrWhiteSpace(f.SpotDsl))
        {
            spot = StatDsl.Compile(f.SpotDsl, "sp");
            extra.Add($"(({spot.Sql}) IS NOT NULL AND ({spot.Sql}) != 0)");
        }
        if (f.Positions is { Count: > 0 })
            extra.Add($"position IN ({string.Join(",", f.Positions.Select((_, i) => "$hp" + i))})");
        var all = string.Join(" AND ", new[] { where }.Concat(extra));
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            bind(cmd);
            if (spot != null) foreach (var (k, v) in spot.Parameters) cmd.Parameters.AddWithValue(k, v);
            if (f.Positions is { Count: > 0 }) for (int i = 0; i < f.Positions.Count; i++) cmd.Parameters.AddWithValue("$hp" + i, f.Positions[i]);
            if (!string.IsNullOrWhiteSpace(f.Tag)) cmd.Parameters.AddWithValue("$tag", f.Tag.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(f.Text)) cmd.Parameters.AddWithValue("$q", "%" + f.Text.Trim() + "%");
            cmd.Parameters.AddWithValue("$n", Math.Clamp(f.Limit, 1, 5000));
            cmd.CommandText = "SELECT f.hand_id, f.played_at, f.format, f.hole_cards, f.position, f.net_bb, f.ev_net_bb, " +
                              "(SELECT GROUP_CONCAT(tag, ', ') FROM hand_tags t WHERE t.hand_id = f.hand_id) " +
                              $"FROM player_hand_facts f WHERE {all} ORDER BY f.played_at DESC, f.hand_id DESC LIMIT $n";
            using var r = cmd.ExecuteReader();
            var l = new List<HandListRow>();
            while (r.Read())
                l.Add(new HandListRow(r.GetString(0), DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), r.IsDBNull(5) ? 0 : r.GetDouble(5),
                    r.IsDBNull(6) ? 0 : r.GetDouble(6), r.IsDBNull(7) ? "" : r.GetString(7)));
            return l;
        });
    }

    /// <summary>Manos concretas de una casilla de la matriz (más recientes primero).</summary>
    public List<HandRow> GetHandsFor(string handName, Period period, string scope = "cash", int limit = 200)
    {
        var (where, bind) = Filter(period, scope);
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            bind(cmd);
            cmd.CommandText = $"SELECT hand_id, played_at, hole_cards, position, net_bb, ev_net_bb FROM player_hand_facts " +
                              $"WHERE {where} AND hole_cards IS NOT NULL ORDER BY played_at DESC, hand_id DESC";
            using var r = cmd.ExecuteReader();
            var l = new List<HandRow>();
            while (r.Read() && l.Count < limit)
            {
                var cards = r.GetString(2);
                if (Cards.HandName(cards) != handName) continue;
                l.Add(new HandRow(r.GetString(0), DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture), cards,
                    r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? 0 : r.GetDouble(4), r.IsDBNull(5) ? 0 : r.GetDouble(5)));
            }
            return l;
        });
    }
}

/// <summary>Exportación a CSV de las tablas de informes.</summary>
public static class ReportExport
{
    static string Esc(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string ToCsv(IEnumerable<GroupRow> rows)
    {
        var sb = new StringBuilder("grupo,manos,neto_bb,bb_100,ev_bb_100,vpip,pfr\n");
        var ci = CultureInfo.InvariantCulture;
        foreach (var r in rows)
            sb.Append(Esc(r.Key)).Append(',').Append(r.Hands).Append(',').Append(r.NetBb.ToString("0.##", ci)).Append(',')
              .Append(r.BbPer100.ToString("0.##", ci)).Append(',').Append(r.EvBbPer100.ToString("0.##", ci)).Append(',')
              .Append(r.VpipPct.ToString("0.#", ci)).Append(',').Append(r.PfrPct.ToString("0.#", ci)).Append('\n');
        return sb.ToString();
    }

    public static string ToCsv(IEnumerable<HandRow> rows)
    {
        var sb = new StringBuilder("mano,fecha,cartas,posicion,neto_bb,ev_bb\n");
        var ci = CultureInfo.InvariantCulture;
        foreach (var r in rows)
            sb.Append(r.HandId).Append(',').Append(r.Time.ToString("s", ci)).Append(',').Append(Esc(r.Cards ?? "")).Append(',')
              .Append(r.Position).Append(',').Append(r.NetBb.ToString("0.##", ci)).Append(',').Append(r.EvNetBb.ToString("0.##", ci)).Append('\n');
        return sb.ToString();
    }

    public static string ToCsv(MatrixCell[,] m)
    {
        var sb = new StringBuilder("mano,manos,jugadas,neto_bb,bb_100\n");
        var ci = CultureInfo.InvariantCulture;
        foreach (var c in m.Cast<MatrixCell>().Where(c => c.Hands > 0).OrderBy(c => c.Name, StringComparer.Ordinal))
            sb.Append(c.Name).Append(',').Append(c.Hands).Append(',').Append(c.Played).Append(',')
              .Append(c.NetBb.ToString("0.##", ci)).Append(',').Append(c.BbPer100.ToString("0.##", ci)).Append('\n');
        return sb.ToString();
    }
}

/// <summary>Agrupa valores en intervalos iguales (para el histograma de resultados por sesión).</summary>
public static class Histogram
{
    public sealed record Bin(double From, double To, int Count)
    {
        public string Label => $"{From:0}…{To:0}";
    }

    public static List<Bin> Build(IReadOnlyList<double> values, int bins = 12)
    {
        if (values.Count == 0 || bins < 1) return new();
        double min = values.Min(), max = values.Max();
        if (max - min < 1e-9) return new() { new Bin(min, max, values.Count) };
        double w = (max - min) / bins;
        var counts = new int[bins];
        foreach (var v in values) counts[Math.Min(bins - 1, (int)((v - min) / w))]++;
        return Enumerable.Range(0, bins).Select(i => new Bin(min + i * w, min + (i + 1) * w, counts[i])).ToList();
    }
}
