using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VergilHud.Core;

public enum StudyGrade { Bad = 1, Doubtful = 3, Good = 5 }

public sealed record ReviewCard(string HandId, string Source, string Category, DateTime Added, DateTime Due, double Ease, int IntervalDays, int Reps, int? LastGrade);

public sealed record StudyStats(int Total, int Pending, int Streak, int ReviewedToday, IReadOnlyList<(string Category, int Reviews, int Good)> ByCategory);

/// <summary>
/// Cola de estudio con repaso espaciado SM-2 (DESIGN.md §11). Las manos entran por etiqueta (estudiar/error),
/// desde un leak, desde Nash o con la regla automática de botes grandes perdidos en showdown.
/// </summary>
public sealed class StudyService
{
    public const double BigLossBb = 50;
    readonly HudDatabase _db;

    public StudyService(HudDatabase db) => _db = db;

    static string Iso(DateTime d) => d.ToString("s", CultureInfo.InvariantCulture);
    static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Añade una mano a la cola (si ya estaba, no cambia nada salvo reactivarla). Devuelve true si es nueva.</summary>
    public bool Add(string handId, string source, string category = "", DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO review_cards(hand_id, source, category, added_at, due) VALUES($h,$s,$c,$a,$a)";
            cmd.Parameters.AddWithValue("$h", handId);
            cmd.Parameters.AddWithValue("$s", source);
            cmd.Parameters.AddWithValue("$c", category);
            cmd.Parameters.AddWithValue("$a", Iso(t));
            bool added = cmd.ExecuteNonQuery() > 0;
            if (!added)
            {
                using var re = conn.CreateCommand();
                re.CommandText = "UPDATE review_cards SET retired = 0 WHERE hand_id = $h";
                re.Parameters.AddWithValue("$h", handId);
                re.ExecuteNonQuery();
            }
            return added;
        });
    }

    public void Remove(string handId) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE review_cards SET retired = 1 WHERE hand_id = $h";
        cmd.Parameters.AddWithValue("$h", handId);
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Mete en la cola las manos con etiqueta estudiar/error y los botes grandes perdidos en showdown. Devuelve cuántas son nuevas.</summary>
    public int SyncAutomatic(DateTime? now = null)
    {
        int added = 0;
        var t = now ?? DateTime.Now;
        var rows = _db.Query(conn =>
        {
            var l = new List<(string Hand, string Source, string Cat)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT hand_id, tag FROM hand_tags WHERE tag IN ('estudiar','error')";
                using var r = cmd.ExecuteReader();
                while (r.Read()) l.Add((r.GetString(0), "etiqueta", r.GetString(1)));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT hand_id FROM player_hand_facts WHERE is_hero = 1 AND wtsd = 1 AND net_bb <= $l";
                cmd.Parameters.AddWithValue("$l", -BigLossBb);
                using var r = cmd.ExecuteReader();
                while (r.Read()) l.Add((r.GetString(0), "bote grande", "bote grande"));
            }
            return l;
        });
        foreach (var (h, s, c) in rows)
            if (Add(h, s, c, t)) added++;
        return added;
    }

    public List<ReviewCard> GetDue(DateTime? now = null, int limit = 50) => Query(
        "WHERE retired = 0 AND due <= $now ORDER BY due, added_at LIMIT $n", c =>
        {
            c.Parameters.AddWithValue("$now", Iso(now ?? DateTime.Now));
            c.Parameters.AddWithValue("$n", limit);
        });

    public List<ReviewCard> GetAll(int limit = 500) => Query("WHERE retired = 0 ORDER BY due LIMIT $n", c => c.Parameters.AddWithValue("$n", limit));

    List<ReviewCard> Query(string tail, Action<SqliteCommand> bind) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hand_id, source, category, added_at, due, ease, interval_days, reps, last_grade FROM review_cards " + tail;
        bind(cmd);
        using var r = cmd.ExecuteReader();
        var l = new List<ReviewCard>();
        while (r.Read())
            l.Add(new ReviewCard(r.GetString(0), r.GetString(1), r.GetString(2), Parse(r.GetString(3)), Parse(r.GetString(4)), r.GetDouble(5),
                r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8)));
        return l;
    });

    /// <summary>
    /// SM-2: con nota &lt; 3 se empieza de cero (mañana); con ≥ 3 los intervalos son 1 día, 6 días y luego ×facilidad.
    /// La facilidad nunca baja de 1,3.
    /// </summary>
    public static (double Ease, int Interval, int Reps) Schedule(double ease, int interval, int reps, StudyGrade grade)
    {
        int q = (int)grade;
        if (q < 3) return (Math.Max(1.3, ease - 0.2), 1, 0);
        double e = Math.Max(1.3, ease + 0.1 - (5 - q) * (0.08 + (5 - q) * 0.02));
        int next = reps == 0 ? 1 : reps == 1 ? 6 : (int)Math.Round(interval * e);
        return (e, Math.Max(1, next), reps + 1);
    }

    public void Review(string handId, StudyGrade grade, string? guess = null, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        _db.Query(conn =>
        {
            double ease; int interval, reps; string category;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ease, interval_days, reps, category FROM review_cards WHERE hand_id = $h";
                cmd.Parameters.AddWithValue("$h", handId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) throw new KeyNotFoundException($"La mano {handId} no está en la cola de estudio");
                ease = r.GetDouble(0); interval = r.GetInt32(1); reps = r.GetInt32(2); category = r.GetString(3);
            }
            var (e, i, n) = Schedule(ease, interval, reps, grade);
            using (var up = conn.CreateCommand())
            {
                up.CommandText = "UPDATE review_cards SET ease=$e, interval_days=$i, reps=$n, due=$d, last_grade=$g WHERE hand_id=$h";
                up.Parameters.AddWithValue("$e", e);
                up.Parameters.AddWithValue("$i", i);
                up.Parameters.AddWithValue("$n", n);
                up.Parameters.AddWithValue("$d", Iso(t.Date.AddDays(i)));
                up.Parameters.AddWithValue("$g", (int)grade);
                up.Parameters.AddWithValue("$h", handId);
                up.ExecuteNonQuery();
            }
            using var log = conn.CreateCommand();
            log.CommandText = "INSERT INTO study_log(hand_id, reviewed_at, grade, category, guess) VALUES($h,$t,$g,$c,$u)";
            log.Parameters.AddWithValue("$h", handId);
            log.Parameters.AddWithValue("$t", Iso(t));
            log.Parameters.AddWithValue("$g", (int)grade);
            log.Parameters.AddWithValue("$c", category);
            log.Parameters.AddWithValue("$u", (object?)guess ?? DBNull.Value);
            log.ExecuteNonQuery();
            return 0;
        });
    }

    public StudyStats GetStats(DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        return _db.Query(conn =>
        {
            int total, pending, today;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(CASE WHEN due <= $now THEN 1 ELSE 0 END),0) FROM review_cards WHERE retired = 0";
                cmd.Parameters.AddWithValue("$now", Iso(t));
                using var r = cmd.ExecuteReader();
                r.Read();
                total = r.GetInt32(0);
                pending = r.GetInt32(1);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM study_log WHERE reviewed_at >= $d";
                cmd.Parameters.AddWithValue("$d", Iso(t.Date));
                today = Convert.ToInt32(cmd.ExecuteScalar());
            }
            var days = new HashSet<DateTime>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT substr(reviewed_at,1,10) FROM study_log";
                using var r = cmd.ExecuteReader();
                while (r.Read()) days.Add(DateTime.Parse(r.GetString(0), CultureInfo.InvariantCulture));
            }
            // Racha: días seguidos con repasos, contando desde hoy (o desde ayer si hoy aún no se ha repasado)
            int streak = 0;
            var d = days.Contains(t.Date) ? t.Date : t.Date.AddDays(-1);
            while (days.Contains(d)) { streak++; d = d.AddDays(-1); }

            var cats = new List<(string, int, int)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT category, COUNT(*), SUM(CASE WHEN grade = 5 THEN 1 ELSE 0 END) FROM study_log GROUP BY category ORDER BY COUNT(*) DESC";
                using var r = cmd.ExecuteReader();
                while (r.Read()) cats.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
            }
            return new StudyStats(total, pending, streak, today, cats);
        });
    }

    /// <summary>
    /// Paso del replayer en el que se pregunta: justo antes de la última decisión del héroe (la más cargada de consecuencias).
    /// Los pasos posteriores —y por tanto el resultado— quedan ocultos hasta que el usuario responde.
    /// </summary>
    public static int QuestionStep(HandReplay replay)
    {
        int idx = -1;
        for (int i = 0; i < replay.Steps.Count; i++)
            if (replay.Steps[i].Decision is { ToCall: > 0 }) idx = i;
        if (idx < 0)
            for (int i = 0; i < replay.Steps.Count; i++)
                if (replay.Steps[i].Decision != null) idx = i;
        return Math.Max(0, idx - 1);
    }
}
