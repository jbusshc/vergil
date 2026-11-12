using System.Globalization;

namespace VergilHud.Core;

public sealed record BankrollTx(long Id, DateTime At, string Kind, double Amount, string Note);

/// <param name="Low">Menor riesgo plausible (si tu winrate real fuera el extremo alto del intervalo del 80 %).</param>
/// <param name="High">Mayor riesgo plausible (extremo bajo del intervalo; 100 % si no se descarta que pierdas).</param>
public sealed record RiskRange(double Low, double High, double Point, string Message);

public sealed record BankrollSummary(double Balance, double Deposits, double Withdrawals, double Extras, double CashResult, double TournamentResult,
    double? AvgCashBuyin, double? AvgTournamentBuyin, double? CashBuyins, double? TournamentBuyins, RiskRange? CashRisk, RiskRange? TournamentRisk)
{
    public static readonly string[] Kinds = { "deposit", "withdrawal", "rakeback", "bonus" };

    /// <summary>Hay menos buy-ins que el mínimo que tú configuraste (en cash o en torneos).</summary>
    public bool BelowMinimum(double minBuyins) =>
        (CashBuyins is { } c && c < minBuyins) || (TournamentBuyins is { } t && t < minBuyins);
}

public static class RiskOfRuin
{
    /// <summary>Riesgo de ruina clásico para un jugador con ganancia media <paramref name="mu"/> y desviación <paramref name="sigma"/> por unidad y un bankroll en esas unidades.</summary>
    public static double Closed(double mu, double sigma, double bankroll)
    {
        if (bankroll <= 0) return 1;
        if (mu <= 0 || sigma <= 0) return 1;
        return Math.Min(1, Math.Exp(-2 * mu * bankroll / (sigma * sigma)));
    }

    /// <summary>Simulación Monte Carlo del mismo proceso (pasos normales) para contrastar la fórmula.</summary>
    public static double Simulate(double mu, double sigma, double bankroll, int steps = 20000, int runs = 2000, int seed = 3)
    {
        var rng = new Random(seed);
        int ruined = 0;
        for (int r = 0; r < runs; r++)
        {
            double b = bankroll;
            for (int s = 0; s < steps; s++)
            {
                double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
                b += mu + sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                if (b <= 0) { ruined++; break; }
            }
        }
        return ruined / (double)runs;
    }

    /// <summary>
    /// Riesgo de ruina como rango: con el winrate observado y los extremos de su intervalo del 80 %. Si el extremo bajo no es positivo no se puede
    /// descartar un jugador perdedor y el máximo es 100 %.
    /// </summary>
    static string Pct(double x) => x < 0.001 ? "menos del 0.1%" : (x * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static RiskRange Range(double mu, double sigma, int samples, double bankroll, string unit)
    {
        if (samples < 5 || sigma <= 0) return new RiskRange(double.NaN, double.NaN, double.NaN, $"Aún no hay datos suficientes de {unit} para estimar el riesgo.");
        double se = sigma / Math.Sqrt(samples);
        double lo = mu - 1.2816 * se, hi = mu + 1.2816 * se;
        double worst = Closed(lo, sigma, bankroll), best = Closed(hi, sigma, bankroll), point = Closed(mu, sigma, bankroll);
        string msg = lo <= 0
            ? $"Con tu muestra todavía no se puede descartar que tu winrate sea ≤ 0: el riesgo va de {Pct(best)} hasta el 100%."
            : $"Riesgo de ruina estimado entre {Pct(best)} y {Pct(worst)} (valor central {Pct(point)}).";
        return new RiskRange(best, worst, point, msg);
    }
}

public sealed class BankrollService
{
    readonly HudDatabase _db;

    public BankrollService(HudDatabase db) => _db = db;

    public long Add(string kind, double amount, string note = "", DateTime? at = null)
    {
        if (!BankrollSummary.Kinds.Contains(kind)) throw new ArgumentException($"Tipo de movimiento desconocido '{kind}'");
        if (amount <= 0 || double.IsNaN(amount) || double.IsInfinity(amount)) throw new ArgumentException("El importe debe ser positivo");
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO bankroll_tx(at, kind, amount, note) VALUES($a,$k,$m,$n); SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("$a", (at ?? DateTime.Now).ToString("s", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$m", amount);
            cmd.Parameters.AddWithValue("$n", note.Length > 200 ? note[..200] : note);
            return (long)cmd.ExecuteScalar()!;
        });
    }

    public void Delete(long id) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bankroll_tx WHERE id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteNonQuery();
    });

    public List<BankrollTx> List() => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, at, kind, amount, note FROM bankroll_tx ORDER BY at DESC, id DESC";
        using var r = cmd.ExecuteReader();
        var l = new List<BankrollTx>();
        while (r.Read()) l.Add(new BankrollTx(r.GetInt64(0), DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2), r.GetDouble(3), r.GetString(4)));
        return l;
    });

    public BankrollSummary Summarize()
    {
        var tx = List();
        double dep = tx.Where(t => t.Kind == "deposit").Sum(t => t.Amount), wd = tx.Where(t => t.Kind == "withdrawal").Sum(t => t.Amount);
        double extras = tx.Where(t => t.Kind is "rakeback" or "bonus").Sum(t => t.Amount);

        // Cash: resultado en dinero y desviación del resultado en bloques de 100 manos (en bb)
        var cash = _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT net, net_bb, big_blind FROM player_hand_facts WHERE is_hero = 1 AND format IN ('cash','zoom') ORDER BY played_at, hand_id";
            using var r = cmd.ExecuteReader();
            var l = new List<(double Net, double NetBb, double Bb)>();
            while (r.Read()) l.Add((r.IsDBNull(0) ? 0 : r.GetDouble(0), r.IsDBNull(1) ? 0 : r.GetDouble(1), r.IsDBNull(2) ? 0 : r.GetDouble(2)));
            return l;
        });
        double cashResult = cash.Sum(x => x.Net);
        double? cashBuyin = cash.Count > 0 && cash.Average(x => x.Bb) > 0 ? cash.Average(x => x.Bb) * 100 : null;

        var tournaments = TournamentReport.Load(_db, Period.All).Where(t => t.Finished).ToList();
        double tResult = tournaments.Sum(t => t.Profit);
        double? tBuyin = tournaments.Count > 0 ? tournaments.Average(t => t.Buyin) : null;

        double balance = dep - wd + extras + cashResult + tResult;

        RiskRange? cashRisk = null, tRisk = null;
        if (cash.Count >= 500 && cash.Average(x => x.Bb) > 0)
        {
            var blocks = cash.Chunk(100).Where(c => c.Length == 100).Select(c => c.Sum(x => x.NetBb)).ToList();
            double mu = blocks.Average(), sd = Math.Sqrt(blocks.Sum(b => (b - mu) * (b - mu)) / Math.Max(1, blocks.Count - 1));
            cashRisk = RiskOfRuin.Range(mu, sd, blocks.Count, balance / cash.Average(x => x.Bb), "cash");
        }
        if (tournaments.Count >= 5 && tBuyin is > 0)
        {
            var rs = tournaments.Where(t => t.Buyin > 0).Select(t => t.Profit / t.Buyin).ToList();
            double mu = rs.Average(), sd = Math.Sqrt(rs.Sum(x => (x - mu) * (x - mu)) / Math.Max(1, rs.Count - 1));
            tRisk = RiskOfRuin.Range(mu, sd, rs.Count, balance / tBuyin.Value, "torneos");
        }
        return new BankrollSummary(balance, dep, wd, extras, cashResult, tResult, cashBuyin, tBuyin,
            cashBuyin is > 0 ? balance / cashBuyin : null, tBuyin is > 0 ? balance / tBuyin : null, cashRisk, tRisk);
    }
}
