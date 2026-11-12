using System.Globalization;

namespace VergilHud.Core;

public sealed record JournalEntry(long Id, DateTime At, string Phase, int? Mood, int? Fatigue, string Note);

/// <summary>Diario de sesión (DESIGN.md §19.7): ánimo y cansancio opcionales al empezar y al cerrar.</summary>
public sealed class JournalService
{
    readonly HudDatabase _db;

    public JournalService(HudDatabase db) => _db = db;

    public long Add(string phase, int? mood, int? fatigue, string note = "", DateTime? at = null)
    {
        if (phase is not ("start" or "end")) throw new ArgumentException("La fase debe ser 'start' o 'end'");
        if (mood is < 1 or > 5 || fatigue is < 1 or > 5) throw new ArgumentException("Ánimo y cansancio van de 1 a 5");
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO journal(at, phase, mood, fatigue, note) VALUES($a,$p,$m,$f,$n); SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("$a", (at ?? DateTime.Now).ToString("s", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$p", phase);
            cmd.Parameters.AddWithValue("$m", (object?)mood ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$f", (object?)fatigue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", note.Length > 1000 ? note[..1000] : note);
            return (long)cmd.ExecuteScalar()!;
        });
    }

    public List<JournalEntry> List(int limit = 200) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, at, phase, mood, fatigue, note FROM journal ORDER BY at DESC, id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var l = new List<JournalEntry>();
        while (r.Read())
            l.Add(new JournalEntry(r.GetInt64(0), DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetString(5)));
        return l;
    });
}

/// <param name="Diff">Media del grupo A menos la del B. El intervalo es del 90 %; si contiene el 0 no hay diferencia clara.</param>
public sealed record GroupComparison(string Title, string LabelA, string LabelB, int NA, int NB, double MeanA, double MeanB, double Diff, double Lo, double Hi, string Unit)
{
    public bool Clear => Lo > 0 || Hi < 0;
}

public sealed record CorrelationReport(int Sessions, IReadOnlyList<GroupComparison> Comparisons, IReadOnlyList<string> Messages)
{
    public const int MinPerGroup = 5;

    sealed record Sess(DateTime Start, double Minutes, int Hands, double NetBb, double Vpip, int Tables, int? Mood, int? Fatigue);

    /// <summary>
    /// Correlaciones entre tu estado y tu juego, sesión a sesión. Cada comparación necesita al menos 5 sesiones por grupo y lleva su intervalo;
    /// si no hay muestra, lo dice en vez de inventar una conclusión.
    /// </summary>
    public static CorrelationReport Build(HudDatabase db, JournalService journal)
    {
        SessionService.EnsureUpToDate(db);
        var entries = journal.List(5000).Where(e => e.Phase == "start" && e.Mood != null).OrderBy(e => e.At).ToList();
        var sessions = new List<Sess>();
        foreach (var s in SessionService.GetSessions(db, Period.All))
        {
            double vpip = db.Query(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(AVG(pf_vpip),0) FROM player_hand_facts WHERE is_hero = 1 AND played_at >= $a AND played_at <= $b";
                cmd.Parameters.AddWithValue("$a", s.Start.ToString("s", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$b", s.End.ToString("s", CultureInfo.InvariantCulture));
                return Convert.ToDouble(cmd.ExecuteScalar());
            });
            // El ánimo es el del último registro de inicio hecho hasta 30 min antes de empezar o durante la sesión
            var e = entries.LastOrDefault(x => x.At >= s.Start.AddMinutes(-30) && x.At <= s.End);
            sessions.Add(new Sess(s.Start, s.Duration.TotalMinutes, s.Hands, s.NetBb, vpip, s.TablesMax, e?.Mood, e?.Fatigue));
        }

        var comps = new List<GroupComparison>();
        var msgs = new List<string>();
        void Add(string title, string la, string lb, IEnumerable<double> a, IEnumerable<double> b, string unit)
        {
            var xa = a.ToList(); var xb = b.ToList();
            if (xa.Count < MinPerGroup || xb.Count < MinPerGroup)
            {
                msgs.Add($"{title}: aún no hay datos suficientes (hacen falta {MinPerGroup} sesiones por grupo; hay {xa.Count} y {xb.Count}).");
                return;
            }
            var c = Compare(title, la, lb, xa, xb, unit);
            comps.Add(c);
            msgs.Add(c.Clear
                ? $"{title}: {la} {c.MeanA:+0.0;-0.0} {unit} frente a {lb} {c.MeanB:+0.0;-0.0} {unit} (diferencia {c.Diff:+0.0;-0.0}, intervalo {c.Lo:+0.0;-0.0} a {c.Hi:+0.0;-0.0})."
                : $"{title}: sin diferencia clara entre {la} y {lb} (intervalo {c.Lo:+0.0;-0.0} a {c.Hi:+0.0;-0.0} {unit}).");
        }

        double Bb100(Sess s) => s.Hands == 0 ? 0 : s.NetBb * 100 / s.Hands;
        var withHands = sessions.Where(s => s.Hands >= 20).ToList();
        Add("Ánimo y winrate", "con ánimo ≤ 2", "con ánimo ≥ 4", withHands.Where(s => s.Mood is <= 2).Select(Bb100), withHands.Where(s => s.Mood is >= 4).Select(Bb100), "bb/100");
        Add("Ánimo y VPIP", "con ánimo ≤ 2", "con ánimo ≥ 4", withHands.Where(s => s.Mood is <= 2).Select(s => s.Vpip * 100), withHands.Where(s => s.Mood is >= 4).Select(s => s.Vpip * 100), "puntos de VPIP");
        Add("Duración y winrate", "sesiones de más de 2 h", "sesiones de 2 h o menos", withHands.Where(s => s.Minutes > 120).Select(Bb100), withHands.Where(s => s.Minutes <= 120).Select(Bb100), "bb/100");
        Add("Mesas y winrate", "con 4 o más mesas", "con 3 o menos", withHands.Where(s => s.Tables >= 4).Select(Bb100), withHands.Where(s => s.Tables <= 3).Select(Bb100), "bb/100");
        Add("Hora y winrate", "empezando de noche (≥ 22 h)", "empezando antes", withHands.Where(s => s.Start.Hour >= 22 || s.Start.Hour < 5).Select(Bb100), withHands.Where(s => s.Start.Hour is >= 5 and < 22).Select(Bb100), "bb/100");
        return new CorrelationReport(sessions.Count, comps, msgs);
    }

    public static GroupComparison Compare(string title, string la, string lb, IReadOnlyList<double> a, IReadOnlyList<double> b, string unit)
    {
        double ma = a.Average(), mb = b.Average();
        double va = a.Sum(x => (x - ma) * (x - ma)) / Math.Max(1, a.Count - 1), vb = b.Sum(x => (x - mb) * (x - mb)) / Math.Max(1, b.Count - 1);
        double se = Math.Sqrt(va / a.Count + vb / b.Count);
        double t = TournamentReport.TCrit90(Math.Max(1, Math.Min(a.Count, b.Count) - 1));
        double diff = ma - mb;
        return new GroupComparison(title, la, lb, a.Count, b.Count, ma, mb, diff, diff - t * se, diff + t * se, unit);
    }
}
