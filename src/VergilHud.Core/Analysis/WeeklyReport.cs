using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace VergilHud.Core;

/// <summary>Objetivos semanales (DESIGN.md §19.9). 0 = sin objetivo. Los repasos de Estudio sustituyen a los «minutos de estudio», que la app no mide.</summary>
public sealed class Goals
{
    public int WeeklyHands { get; set; }
    public int WeeklyTournaments { get; set; }
    public int WeeklyReviews { get; set; }
}

public sealed record GoalProgressItem(string Name, int Done, int Target)
{
    public double Fraction => Target <= 0 ? 0 : Math.Min(1, Done / (double)Target);
}

/// <summary>Claves de configuración guardadas en la tabla meta (objetivos, último informe).</summary>
public static class MetaStore
{
    public static string? Get(HudDatabase db, string key) => db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    });

    public static void Set(HudDatabase db, string key, string value) => db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        return cmd.ExecuteNonQuery();
    });
}

public static class GoalService
{
    public static Goals Load(HudDatabase db)
    {
        var json = MetaStore.Get(db, "goals");
        try { return json == null ? new Goals() : JsonSerializer.Deserialize<Goals>(json) ?? new Goals(); }
        catch (JsonException) { return new Goals(); }
    }

    public static void Save(HudDatabase db, Goals g) =>
        MetaStore.Set(db, "goals", JsonSerializer.Serialize(new Goals
        {
            WeeklyHands = Math.Max(0, g.WeeklyHands), WeeklyTournaments = Math.Max(0, g.WeeklyTournaments), WeeklyReviews = Math.Max(0, g.WeeklyReviews),
        }));

    /// <summary>Lunes de la semana de <paramref name="d"/>.</summary>
    public static DateTime WeekStart(DateTime d)
    {
        int back = ((int)d.DayOfWeek + 6) % 7;
        return d.Date.AddDays(-back);
    }

    /// <summary>Progreso de la semana en curso (de lunes a hoy). Solo aparecen los objetivos definidos.</summary>
    public static List<GoalProgressItem> Progress(HudDatabase db, Goals g, DateTime? now = null)
    {
        var from = WeekStart(now ?? DateTime.Now).ToString("s", CultureInfo.InvariantCulture);
        var items = new List<GoalProgressItem>();
        if (g.WeeklyHands > 0)
            items.Add(new GoalProgressItem("Manos", Count(db, "SELECT COUNT(*) FROM player_hand_facts WHERE is_hero = 1 AND played_at >= $f", from), g.WeeklyHands));
        if (g.WeeklyTournaments > 0)
            items.Add(new GoalProgressItem("Torneos", Count(db, "SELECT COUNT(DISTINCT tournament_id) FROM player_hand_facts WHERE is_hero = 1 AND tournament_id IS NOT NULL AND played_at >= $f", from), g.WeeklyTournaments));
        if (g.WeeklyReviews > 0)
            items.Add(new GoalProgressItem("Repasos de estudio", Count(db, "SELECT COUNT(*) FROM study_log WHERE reviewed_at >= $f", from), g.WeeklyReviews));
        return items;
    }

    static int Count(HudDatabase db, string sql, string from) => db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$f", from);
        return Convert.ToInt32(cmd.ExecuteScalar());
    });
}

/// <summary>Informe semanal local en HTML (DESIGN.md §19.8): volumen, resultados, leaks nuevos o resueltos, estudio, ROI de Spin y mejores/peores manos.</summary>
public static class WeeklyReport
{
    static string H(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Hay que generar el informe: todavía no se hizo en la semana (lunes a domingo) en curso.</summary>
    public static bool IsDue(HudDatabase db, DateTime now) =>
        MetaStore.Get(db, "weekly_last") != GoalService.WeekStart(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string FileName(DateTime now) => $"semanal-{GoalService.WeekStart(now):yyyy-MM-dd}.html";

    public static string Build(HudDatabase db, StatEngine engine, StudyService study, LeakDetector detector, IReadOnlyList<LeakBaseline> baselines, DateTime now)
    {
        var to = now;
        var from = now.Date.AddDays(-7);
        var period = new Period(from, to);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Informe semanal</title><style>");
        sb.Append("body{font-family:Segoe UI,sans-serif;background:#1b1c21;color:#e6e6e6;max-width:820px;margin:24px auto;padding:0 16px}");
        sb.Append("h1{font-size:22px}h2{font-size:16px;margin-top:28px;border-bottom:1px solid #3a3c45;padding-bottom:4px}");
        sb.Append("table{border-collapse:collapse;width:100%}td,th{padding:5px 8px;border-bottom:1px solid #2a2b32;text-align:left}");
        sb.Append(".pos{color:#6edc78}.neg{color:#ff6e5f}.muted{color:#9a9aa6}</style></head><body>");
        sb.Append($"<h1>Informe semanal · {from:dd/MM/yyyy} – {to:dd/MM/yyyy}</h1><p class=\"muted\">Generado en tu equipo con tus propias manos. No es consejo de juego.</p>");

        // Volumen y resultados por formato
        var fmt = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT format, COUNT(*), COALESCE(SUM(net_bb),0), COALESCE(SUM(ev_net_bb),0) FROM player_hand_facts WHERE is_hero = 1 AND played_at >= $a AND played_at <= $b GROUP BY format ORDER BY COUNT(*) DESC";
            cmd.Parameters.AddWithValue("$a", from.ToString("s", ci));
            cmd.Parameters.AddWithValue("$b", to.ToString("s", ci));
            using var r = cmd.ExecuteReader();
            var l = new List<(string F, int N, double Net, double Ev)>();
            while (r.Read()) l.Add((r.GetString(0), r.GetInt32(1), r.GetDouble(2), r.GetDouble(3)));
            return l;
        });
        sb.Append("<h2>Volumen y resultados</h2>");
        if (fmt.Count == 0) sb.Append("<p class=\"muted\">No hay manos esta semana.</p>");
        else
        {
            sb.Append("<table><tr><th>Formato</th><th>Manos</th><th>Neto (bb)</th><th>EV (bb)</th><th>bb/100</th></tr>");
            foreach (var x in fmt)
                sb.Append($"<tr><td>{H(x.F)}</td><td>{x.N}</td><td class=\"{(x.Net >= 0 ? "pos" : "neg")}\">{x.Net:+0.0;-0.0;0.0}</td><td>{x.Ev:+0.0;-0.0;0.0}</td><td>{x.Net * 100 / x.N:+0.0;-0.0;0.0}</td></tr>");
            sb.Append("</table>");
        }

        // ROI de Spin & Go con su intervalo
        var spins = TournamentReport.Load(db, period, "spin");
        sb.Append("<h2>Spin &amp; Go</h2>");
        if (spins.Count(s => s.Finished) == 0) sb.Append("<p class=\"muted\">Sin Spins terminados esta semana.</p>");
        else
        {
            var roi = TournamentReport.Roi(spins);
            sb.Append($"<p>{roi.N} Spins · ROI {roi.Roi * 100:+0.0;-0.0}% (intervalo del 90 %: {roi.Lo * 100:+0.0;-0.0}% a {roi.Hi * 100:+0.0;-0.0}%)</p><p class=\"muted\">{H(roi.Message)}</p>");
        }

        // Leaks nuevos y resueltos respecto al último informe
        sb.Append("<h2>Leaks</h2>");
        var hero = db.GetHeroName();
        if (hero == null) sb.Append("<p class=\"muted\">Todavía no hay manos tuyas.</p>");
        else
        {
            var leaks = detector.Detect(hero, baselines);
            var prevJson = MetaStore.Get(db, "leaks_last_report");
            var prev = prevJson == null ? new List<string>() : JsonSerializer.Deserialize<List<string>>(prevJson) ?? new();
            var ids = leaks.Select(l => l.Baseline.Id).ToList();
            var added = leaks.Where(l => !prev.Contains(l.Baseline.Id)).ToList();
            var resolved = prev.Where(p => !ids.Contains(p)).ToList();
            if (leaks.Count == 0) sb.Append("<p class=\"muted\">Ningún leak con la muestra actual.</p>");
            foreach (var l in leaks)
                sb.Append($"<p>{(added.Contains(l) ? "<b>NUEVO</b> · " : "")}{H(LeakDetector.Describe(l))}</p>");
            if (resolved.Count > 0) sb.Append($"<p class=\"pos\">Resueltos desde el último informe: {H(string.Join(", ", resolved))}</p>");
            MetaStore.Set(db, "leaks_last_report", JsonSerializer.Serialize(ids));
        }

        // Estudio
        var st = study.GetStats(now);
        int reviews = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM study_log WHERE reviewed_at >= $a";
            cmd.Parameters.AddWithValue("$a", from.ToString("s", ci));
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
        sb.Append($"<h2>Estudio</h2><p>{reviews} repasos esta semana · racha {st.Streak} días · {st.Pending} pendientes de {st.Total} en la cola.</p>");

        // Mejores y peores manos
        var hands = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hand_id, hole_cards, net_bb FROM player_hand_facts WHERE is_hero = 1 AND played_at >= $a AND played_at <= $b ORDER BY net_bb";
            cmd.Parameters.AddWithValue("$a", from.ToString("s", ci));
            cmd.Parameters.AddWithValue("$b", to.ToString("s", ci));
            using var r = cmd.ExecuteReader();
            var l = new List<(string Id, string Cards, double Net)>();
            while (r.Read()) l.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? 0 : r.GetDouble(2)));
            return l;
        });
        sb.Append("<h2>Mejores y peores manos</h2>");
        if (hands.Count == 0) sb.Append("<p class=\"muted\">Sin manos esta semana.</p>");
        else
        {
            sb.Append("<table><tr><th>Mano</th><th>Cartas</th><th>Neto (bb)</th></tr>");
            foreach (var h in hands.TakeLast(3).Reverse().Concat(hands.Take(3)).Distinct())
                sb.Append($"<tr><td>#{H(h.Id)}</td><td>{H(h.Cards)}</td><td class=\"{(h.Net >= 0 ? "pos" : "neg")}\">{h.Net:+0.0;-0.0;0.0}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>Genera el informe en la carpeta indicada y lo marca como hecho esta semana. Devuelve la ruta.</summary>
    public static string Generate(HudDatabase db, StatEngine engine, StudyService study, LeakDetector detector, IReadOnlyList<LeakBaseline> baselines,
        string folder, DateTime now)
    {
        var html = Build(db, engine, study, detector, baselines, now);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName(now));
        File.WriteAllText(path, html, new UTF8Encoding(false));
        MetaStore.Set(db, "weekly_last", GoalService.WeekStart(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return path;
    }
}
