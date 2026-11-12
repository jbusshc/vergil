using System.Globalization;

namespace VergilHud.Core;

/// <param name="HistVpip">Tu VPIP histórico (0..1) y <paramref name="HistPfr"/>, para medir cuánto te alejas hoy.</param>
/// <param name="MinutesNoBreak">Minutos seguidos sin una pausa de al menos 5 minutos.</param>
public sealed record TiltInputs(int Hands, double SessionVpip, double SessionPfr, double HistVpip, double HistPfr, double NetBb,
    double MinutesNoBreak, double SessionMinutes, int Tables);

public sealed record TiltScore(int Score, string Level, IReadOnlyList<string> Reasons);

/// <summary>
/// Tilt score de 0 a 100 (DESIGN.md §12): combina cuánto se aleja tu estilo de hoy del habitual, las pérdidas de la sesión, el tiempo sin pausa y
/// las mesas simultáneas. Es información sobre ti, no un consejo de juego.
/// </summary>
public static class Tilt
{
    public const int MinHandsForStyle = 30;

    public static TiltScore Compute(TiltInputs i)
    {
        var reasons = new List<string>();
        double style = 0;
        if (i.Hands >= MinHandsForStyle)
        {
            double zV = Z(i.SessionVpip, i.HistVpip, i.Hands), zP = Z(i.SessionPfr, i.HistPfr, i.Hands);
            style = Math.Min(30, (Math.Max(0, Math.Abs(zV) - 1) + Math.Max(0, Math.Abs(zP) - 1)) * 9);
            if (Math.Abs(zV) > 2) reasons.Add($"Tu VPIP de hoy ({i.SessionVpip * 100:0}%) se aleja del habitual ({i.HistVpip * 100:0}%)");
            if (Math.Abs(zP) > 2) reasons.Add($"Tu PFR de hoy ({i.SessionPfr * 100:0}%) se aleja del habitual ({i.HistPfr * 100:0}%)");
        }
        double loss = i.NetBb < 0 ? Math.Min(35, -i.NetBb / 100 * 35) : 0;
        if (loss >= 10) reasons.Add($"Llevas {i.NetBb:0} bb en la sesión");
        double time = i.MinutesNoBreak > 60 ? Math.Min(20, (i.MinutesNoBreak - 60) / 60 * 20) : 0;
        if (time >= 5) reasons.Add($"Sin pausa desde hace {i.MinutesNoBreak:0} minutos");
        double tables = i.Tables > 3 ? Math.Min(15, (i.Tables - 3) * 5) : 0;
        if (tables > 0) reasons.Add($"{i.Tables} mesas a la vez");

        int score = (int)Math.Round(Math.Min(100, style + loss + time + tables));
        string level = score < 25 ? "Tranquilo" : score < 50 ? "Atención" : score < 75 ? "Alto" : "Muy alto";
        return new TiltScore(score, level, reasons);
    }

    static double Z(double observed, double expected, int n)
    {
        double p = Math.Clamp(expected, 0.01, 0.99);
        return (observed - p) / Math.Sqrt(p * (1 - p) / n);
    }
}

public sealed class WellbeingLimits
{
    /// <summary>Pérdida de la sesión (en bb, positiva) a partir de la cual se avisa. Null = sin límite.</summary>
    public double? StopLossBb { get; set; }
    public int? MaxSessionMinutes { get; set; }
    public int? BreakEveryMinutes { get; set; }
}

/// <summary>
/// Avisa cuando se cruza un límite que tú definiste. Nunca cierra mesas ni toca el juego: solo devuelve textos para mostrar.
/// Cada aviso salta una vez (el de pausa, cada vez que pasan los minutos indicados).
/// </summary>
public sealed class WellbeingMonitor
{
    readonly WellbeingLimits _limits;
    bool _stopLossFired, _maxFired;
    DateTime? _lastBreakAlert;

    public WellbeingMonitor(WellbeingLimits limits) => _limits = limits;

    public List<string> Check(TiltInputs i, DateTime now, DateTime sessionStart)
    {
        var alerts = new List<string>();
        if (_limits.StopLossBb is { } sl && sl > 0 && -i.NetBb >= sl && !_stopLossFired)
        {
            _stopLossFired = true;
            alerts.Add($"Has alcanzado tu stop-loss: {i.NetBb:0} bb en la sesión (límite {sl:0} bb).");
        }
        if (_limits.MaxSessionMinutes is { } max && max > 0 && i.SessionMinutes >= max && !_maxFired)
        {
            _maxFired = true;
            alerts.Add($"Llevas {i.SessionMinutes:0} minutos jugando (tu límite es {max}).");
        }
        if (_limits.BreakEveryMinutes is { } br && br > 0)
        {
            var since = _lastBreakAlert ?? sessionStart;
            if (i.MinutesNoBreak >= br && (now - since).TotalMinutes >= br)
            {
                _lastBreakAlert = now;
                alerts.Add($"Hace {i.MinutesNoBreak:0} minutos que no haces una pausa.");
            }
        }
        return alerts;
    }

    public void Reset()
    {
        _stopLossFired = _maxFired = false;
        _lastBreakAlert = null;
    }
}

/// <summary>Calcula los datos de la sesión actual (la última) a partir de la base de datos.</summary>
public static class TiltService
{
    const int BreakMinutes = 5;

    public static (TiltInputs Inputs, DateTime Start)? Current(HudDatabase db, StatEngine engine, string hero, DateTime? now = null)
    {
        var rows = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT f.played_at, h.table_name, f.pf_vpip, f.pf_pfr, f.net_bb FROM player_hand_facts f JOIN hands h ON h.hand_id = f.hand_id " +
                              "WHERE f.is_hero = 1 ORDER BY f.played_at DESC, f.hand_id DESC LIMIT 5000";
            using var r = cmd.ExecuteReader();
            var l = new List<(DateTime T, string Table, int Vpip, int Pfr, double Net)>();
            while (r.Read())
                l.Add((DateTime.Parse(r.GetString(0), CultureInfo.InvariantCulture), r.IsDBNull(1) ? "" : r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                    r.IsDBNull(4) ? 0 : r.GetDouble(4)));
            return l;
        });
        if (rows.Count == 0) return null;
        var t = now ?? DateTime.Now;
        if ((t - rows[0].T).TotalMinutes > SessionService.GapMinutes) return null;     // no hay sesión en curso

        // Mano más antigua de la sesión y del tramo sin pausa (recorriendo hacia atrás)
        int sessionEnd = 0, breakEnd = 0;
        for (int i = 1; i < rows.Count; i++)
        {
            double gap = (rows[i - 1].T - rows[i].T).TotalMinutes;
            if (gap > SessionService.GapMinutes) break;
            sessionEnd = i;
            if (gap < BreakMinutes && breakEnd == i - 1) breakEnd = i;
        }
        var session = rows.Take(sessionEnd + 1).ToList();
        int n = session.Count;
        var hv = engine.Get(hero, "vpip");
        var hp = engine.Get(hero, "pfr");
        // El histórico no incluye la sesión actual para que no se compare consigo misma
        double histV = hv.Denominator > n ? (hv.Numerator - session.Sum(x => x.Vpip)) / (hv.Denominator - n) : 0.25;
        double histP = hp.Denominator > n ? (hp.Numerator - session.Sum(x => x.Pfr)) / (hp.Denominator - n) : 0.15;

        var start = session[^1].T;
        var tablesWindow = session.Where(x => (t - x.T).TotalMinutes <= 5).Select(x => x.Table).Distinct().Count();
        var inputs = new TiltInputs(n, session.Sum(x => x.Vpip) / (double)n, session.Sum(x => x.Pfr) / (double)n, histV, histP, session.Sum(x => x.Net),
            (t - rows[breakEnd].T).TotalMinutes, (t - start).TotalMinutes, tablesWindow);
        return (inputs, start);
    }

    /// <summary>Puntuación de tilt de cada sesión ya terminada (para la gráfica de Sesiones), en el mismo orden que las sesiones.</summary>
    public static List<(DateTime Start, int Score)> ScoreHistory(HudDatabase db, StatEngine engine, string hero)
    {
        SessionService.EnsureUpToDate(db);
        var result = new List<(DateTime, int)>();
        var hv = engine.Get(hero, "vpip");
        var hp = engine.Get(hero, "pfr");
        double allV = hv.Value ?? 0.25, allP = hp.Value ?? 0.15;
        foreach (var s in SessionService.GetSessions(db, Period.All))
        {
            var (vpip, pfr) = db.Query(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(AVG(pf_vpip),0), COALESCE(AVG(pf_pfr),0) FROM player_hand_facts WHERE is_hero = 1 AND played_at >= $a AND played_at <= $b";
                cmd.Parameters.AddWithValue("$a", s.Start.ToString("s", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$b", s.End.ToString("s", CultureInfo.InvariantCulture));
                using var r = cmd.ExecuteReader();
                r.Read();
                return (r.GetDouble(0), r.GetDouble(1));
            });
            var score = Tilt.Compute(new TiltInputs(s.Hands, vpip, pfr, allV, allP, s.NetBb, s.Duration.TotalMinutes, s.Duration.TotalMinutes, s.TablesMax));
            result.Add((s.Start, score.Score));
        }
        return result;
    }
}
