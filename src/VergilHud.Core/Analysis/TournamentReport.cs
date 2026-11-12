using System.Globalization;

namespace VergilHud.Core;

public sealed record TournamentRow(string Id, string Format, DateTime Started, double Buyin, int? Place, double? Prize, int Hands, int? Entrants)
{
    public bool Finished => Place != null;
    public double Profit => (Prize ?? 0) - Buyin;
    /// <summary>En un Spin (el ganador se lo lleva todo) el premio equivale a un múltiplo de la inscripción.</summary>
    public double? Multiplier => Format == "spin" && Place == 1 && Buyin > 0 && Prize is { } p ? Math.Round(p / Buyin, 1) : null;
}

/// <param name="Lo">Extremo inferior del intervalo del 90 % del ROI (fracción: 0,10 = +10 %).</param>
/// <param name="NeededN">Torneos que harían falta para que el intervalo deje de incluir el 0 si tu ROI real fuera el observado.</param>
public sealed record RoiReport(int N, double TotalBuyin, double TotalPrize, double Roi, double Lo, double Hi, double ItmPct,
    double AvgProfit, string Message, int? NeededN);

public sealed record SpinPhaseRow(string Phase, int Hands, double NetBb, double BbPer100);

public sealed record SpinReport(int Played, int Wins, double WinPct, RoiReport Roi, IReadOnlyList<(double Multiplier, int Count)> WinsByMultiplier,
    int HeadsUpReached, int HeadsUpWon, IReadOnlyList<SpinPhaseRow> Phases);

public static class TournamentReport
{
    /// <summary>
    /// Valor crítico del intervalo bilateral del 90 % para la distribución t de Student con <paramref name="df"/> grados de libertad
    /// (interpolado entre los valores de tabla; tiende a 1,645 de la normal). Con pocos torneos es muy grande: con 2 torneos, 6,3.
    /// Usar la normal con muestras pequeñas daría intervalos estrechísimos y conclusiones falsas.
    /// </summary>
    internal static double TCrit90(int df)
    {
        (int Df, double T)[] table =
        {
            (1, 6.314), (2, 2.920), (3, 2.353), (4, 2.132), (5, 2.015), (6, 1.943), (7, 1.895), (8, 1.860), (9, 1.833), (10, 1.812),
            (12, 1.782), (15, 1.753), (20, 1.725), (25, 1.708), (30, 1.697), (40, 1.684), (60, 1.671), (120, 1.658), (100000, 1.645),
        };
        if (df <= 1) return table[0].T;
        for (int i = 1; i < table.Length; i++)
            if (df <= table[i].Df)
            {
                var (d0, t0) = table[i - 1];
                var (d1, t1) = table[i];
                return t0 + (t1 - t0) * (df - d0) / (d1 - d0);
            }
        return 1.645;
    }

    /// <summary>
    /// ROI = Σ ganancia / Σ inscripciones, con su intervalo del 90 % (método delta para un cociente de sumas).
    /// El ROI de unos pocos cientos de torneos es casi ruido, sobre todo en Spin: el informe lo dice con palabras.
    /// Solo cuentan los torneos terminados.
    /// </summary>
    public static RoiReport Roi(IEnumerable<TournamentRow> rows)
    {
        var t = rows.Where(r => r.Finished && r.Buyin > 0).ToList();
        int n = t.Count;
        if (n == 0)
            return new RoiReport(0, 0, 0, 0, 0, 0, 0, 0, "Aún no hay torneos terminados.", null);

        double buyin = t.Sum(r => r.Buyin), prize = t.Sum(r => r.Prize ?? 0), profit = prize - buyin;
        double roi = profit / buyin;
        double sse = t.Sum(r => Math.Pow(r.Profit - roi * r.Buyin, 2));
        double se = n < 2 ? double.PositiveInfinity : Math.Sqrt(n / (n - 1.0) * sse) / buyin;
        double crit = TCrit90(n - 1);
        double lo = roi - crit * se, hi = roi + crit * se;
        double itm = t.Count(r => (r.Prize ?? 0) > 0) * 100.0 / n;

        int? needed = null;
        string msg;
        var pct = (roi * 100).ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
        if (double.IsInfinity(se))
            msg = "Con un solo torneo no se puede estimar nada.";
        else if (lo <= 0 && hi >= 0)
        {
            msg = $"Con {n} torneos aún no se puede distinguir tu ROI de 0 % (observado {pct} %, intervalo {lo * 100:0}–{hi * 100:0} %).";
            if (Math.Abs(roi) > 1e-9)
            {
                var k = Math.Pow(1.645 * se / Math.Abs(roi), 2) * n;   // para n grande el valor crítico tiende a 1,645
                needed = (int)Math.Ceiling(k);
                msg += $" Si tu ROI real fuera el observado, harían falta unos {needed:N0} torneos para confirmarlo.";
            }
        }
        else
            msg = roi > 0
                ? $"Con {n} torneos tu ROI es positivo con un 90 % de confianza: {pct} % (intervalo {lo * 100:0}–{hi * 100:0} %)."
                : $"Con {n} torneos tu ROI es negativo con un 90 % de confianza: {pct} % (intervalo {lo * 100:0}–{hi * 100:0} %).";

        return new RoiReport(n, buyin, prize, roi, lo, hi, itm, profit / n, msg, needed);
    }

    public static List<TournamentRow> Load(HudDatabase db, Period period, string? format = null)
    {
        return db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            var where = new List<string> { "hero IS NOT NULL" };
            if (period.From is { } f) { where.Add("started_at >= $f"); cmd.Parameters.AddWithValue("$f", f.ToString("s")); }
            if (period.To is { } t) { where.Add("started_at <= $t"); cmd.Parameters.AddWithValue("$t", t.ToString("s")); }
            if (format != null) { where.Add("format = $fmt"); cmd.Parameters.AddWithValue("$fmt", format); }
            cmd.CommandText = "SELECT id, format, started_at, buyin, finish_position, prize, hands, entrants FROM tournaments " +
                              $"WHERE {string.Join(" AND ", where)} ORDER BY started_at, id";
            using var r = cmd.ExecuteReader();
            var l = new List<TournamentRow>();
            while (r.Read())
                l.Add(new TournamentRow(r.GetString(0), r.GetString(1), DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                    r.IsDBNull(3) ? 0 : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetDouble(5),
                    r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7)));
            return l;
        });
    }

    /// <summary>Resultados de Spin & Go: ROI, victorias por multiplicador, mano a mano alcanzado/ganado y juego en 3 vs 2 jugadores.</summary>
    public static SpinReport Spin(HudDatabase db, Period period)
    {
        var rows = Load(db, period, "spin");
        var finished = rows.Where(r => r.Finished).ToList();
        var byMult = finished.Where(r => r.Multiplier != null).GroupBy(r => r.Multiplier!.Value)
            .OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();

        // Mano a mano: el héroe llegó si jugó alguna mano con 2 jugadores; lo ganó si además quedó 1.º.
        var minPlayers = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT tournament_id, MIN(players_dealt) FROM player_hand_facts WHERE is_hero = 1 AND format = 'spin' " +
                              "AND tournament_id IS NOT NULL GROUP BY tournament_id";
            using var r = cmd.ExecuteReader();
            var d = new Dictionary<string, int>();
            while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
            return d;
        });
        int reached = 0, won = 0;
        foreach (var t in finished)
            if (minPlayers.TryGetValue(t.Id, out var mp) && mp <= 2)
            {
                reached++;
                if (t.Place == 1) won++;
            }

        var phases = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            var where = "is_hero = 1 AND format = 'spin'";
            if (period.From is { } f) { where += " AND played_at >= $f"; cmd.Parameters.AddWithValue("$f", f.ToString("s")); }
            if (period.To is { } t) { where += " AND played_at <= $t"; cmd.Parameters.AddWithValue("$t", t.ToString("s")); }
            cmd.CommandText = "SELECT players_dealt, COUNT(*), COALESCE(SUM(net_bb),0) FROM player_hand_facts " +
                              $"WHERE {where} GROUP BY players_dealt ORDER BY players_dealt DESC";
            using var r = cmd.ExecuteReader();
            var l = new List<SpinPhaseRow>();
            while (r.Read())
            {
                int pd = r.GetInt32(0), n = r.GetInt32(1);
                double net = r.GetDouble(2);
                l.Add(new SpinPhaseRow(pd >= 3 ? "3 jugadores" : "Mano a mano", n, net, net * 100.0 / n));
            }
            return l;
        });

        int wins = finished.Count(r => r.Place == 1);
        return new SpinReport(finished.Count, wins, finished.Count == 0 ? 0 : wins * 100.0 / finished.Count, Roi(finished), byMult,
            reached, won, phases);
    }
}
