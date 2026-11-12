using System.Globalization;

namespace VergilHud.Core;

public sealed record DailyPick(string HandId, double Score, string Reason);

/// <summary>
/// Cada día propone 5 manos para el Estudio (DESIGN.md §19.6), puntuadas por tamaño del resultado, rareza respecto a tu forma habitual
/// de jugar esa posición, desviación de Nash en push/fold y pertenencia a un leak activo. La selección se guarda para que no cambie en el día.
/// </summary>
public static class DailyPicks
{
    public const int PerDay = 5;

    public static List<DailyPick> Get(HudDatabase db, StudyService study, IReadOnlyCollection<Leak> leaks, LeakDetector detector,
        NashReport? nash, DateTime? today = null, int count = PerDay)
    {
        var day = (today ?? DateTime.Now).Date;
        var key = "daily_picks_" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var saved = Load(db, key);
        if (saved != null) return saved;

        var picks = Compute(db, study, leaks, detector, nash, count);
        foreach (var p in picks) study.Add(p.HandId, "diaria", "diaria", day);
        Save(db, key, picks);
        return picks;
    }

    public static List<DailyPick> Compute(HudDatabase db, StudyService study, IReadOnlyCollection<Leak> leaks, LeakDetector detector,
        NashReport? nash, int count = PerDay)
    {
        var inQueue = study.GetAll(100000).Select(c => c.HandId).ToHashSet();

        var rows = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hand_id, position, pf_vpip, net_bb, wtsd FROM player_hand_facts WHERE is_hero = 1 ORDER BY played_at DESC LIMIT 4000";
            using var r = cmd.ExecuteReader();
            var l = new List<(string Id, string Pos, bool Vpip, double Net, bool Sd)>();
            while (r.Read())
                l.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetInt32(2) == 1, r.IsDBNull(3) ? 0 : r.GetDouble(3), r.GetInt32(4) == 1));
            return l;
        });
        if (rows.Count == 0) return new();

        // Rareza: con qué frecuencia entras al bote desde cada posición
        var vpipByPos = rows.GroupBy(r => r.Pos).ToDictionary(g => g.Key, g => (Rate: g.Count(x => x.Vpip) / (double)g.Count(), N: g.Count()));
        double maxNet = Math.Max(1, rows.Max(r => Math.Abs(r.Net)));

        // Manos que caen en un leak activo (límite por leak para no recorrer miles de filas)
        var leakHands = new Dictionary<string, string>();
        var analytics = new Analytics(db);
        foreach (var l in leaks)
            foreach (var h in analytics.ListHands(detector.SpotFilter(l) with { Limit = 400 }))
                leakHands.TryAdd(h.HandId, l.Baseline.Label);

        var nashCost = nash?.Deviations.GroupBy(d => d.HandId).ToDictionary(g => g.Key, g => g.Sum(d => d.CostBb)) ?? new();
        double maxCost = Math.Max(1, nashCost.Values.DefaultIfEmpty(0).Max());

        var scored = new List<DailyPick>();
        foreach (var r in rows)
        {
            if (inQueue.Contains(r.Id)) continue;
            double size = Math.Min(1, Math.Abs(r.Net) / maxNet);
            double rarity = 0;
            if (vpipByPos.TryGetValue(r.Pos, out var v) && v.N >= 30)
                rarity = r.Vpip ? 1 - v.Rate : v.Rate;
            double leak = leakHands.ContainsKey(r.Id) ? 1 : 0;
            double nashScore = nashCost.TryGetValue(r.Id, out var c) ? Math.Min(1, c / maxCost) : 0;
            double score = 0.35 * size + 0.15 * rarity + 0.25 * leak + 0.25 * nashScore;
            if (score <= 0) continue;

            var reasons = new List<string>();
            if (size >= 0.5) reasons.Add($"bote grande ({r.Net:+0;-0} bb)");
            if (leak > 0) reasons.Add("leak: " + leakHands[r.Id]);
            if (nashScore > 0) reasons.Add("se aleja de Nash");
            if (rarity >= 0.8 && vpipByPos[r.Pos].N >= 30) reasons.Add($"poco habitual desde {r.Pos}");
            scored.Add(new DailyPick(r.Id, score, reasons.Count == 0 ? "resultado relevante" : string.Join("; ", reasons)));
        }
        return scored.OrderByDescending(p => p.Score).ThenBy(p => p.HandId, StringComparer.Ordinal).Take(count).ToList();
    }

    static List<DailyPick>? Load(HudDatabase db, string key)
    {
        var json = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        });
        if (json == null) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<DailyPick>>(json); }
        catch { return null; }
    }

    static void Save(HudDatabase db, string key, List<DailyPick> picks) => db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", System.Text.Json.JsonSerializer.Serialize(picks));
        return cmd.ExecuteNonQuery();
    });
}
