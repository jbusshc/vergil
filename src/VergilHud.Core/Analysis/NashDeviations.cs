using System.Globalization;

namespace VergilHud.Core;

/// <param name="Acted">El héroe empujó (o pagó el all-in) en este spot.</param>
/// <param name="NashFreq">Frecuencia con la que Nash realiza esa acción con esa mano.</param>
/// <param name="CostBb">Lo que cuesta, en bb de fichas, haber elegido la acción contraria a Nash.</param>
public sealed record NashDeviation(string HandId, DateTime Time, string Cards, string HandClass, string Position, double StackBb, string Spot,
    bool Acted, double NashFreq, double CostBb)
{
    public string Description => Acted
        ? $"Empujaste/pagaste {HandClass} con {StackBb:0.#} bb en {Spot}; Nash lo hace solo el {NashFreq * 100:0}% de las veces."
        : $"Te retiraste con {HandClass} y {StackBb:0.#} bb en {Spot}; Nash empuja/paga el {NashFreq * 100:0}% de las veces.";
}

public sealed record NashHandSummary(string HandClass, int Spots, int Deviations, double CostBb);

public sealed record NashReport(int Spots, IReadOnlyList<NashDeviation> Deviations, IReadOnlyList<NashHandSummary> ByHand)
{
    public double TotalCostBb => Deviations.Sum(d => d.CostBb);
}

/// <summary>
/// Compara tus decisiones de push/fold en Spin &amp; Go (3-handed y heads-up) con el equilibrio de Nash (DESIGN.md §19.5).
/// Es material de estudio: el stack del rival se toma igual al efectivo y solo se juzgan los casos claros (Nash ≥ 80 % o ≤ 20 %).
/// </summary>
public static class NashDeviations
{
    public const double Clear = 0.8;
    public const double MaxStackBb = 25;

    public static NashReport Analyze(HudDatabase db, NashLibrary lib, int limit = 5000)
    {
        var rows = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT hand_id, played_at, position, players_dealt, eff_stack_bb, hole_cards, pf_shove_opp, pf_shove, pf_call_shove_opp, pf_call_shove " +
                "FROM player_hand_facts WHERE is_hero = 1 AND format IN ('spin','mtt','sng') AND players_dealt <= 3 AND hole_cards IS NOT NULL " +
                "AND eff_stack_bb >= 1 AND eff_stack_bb <= $m AND (pf_shove_opp = 1 OR pf_call_shove_opp = 1) ORDER BY played_at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$m", MaxStackBb);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var l = new List<(string Id, DateTime T, string Pos, int Players, double Stack, string Cards, bool Open, bool Shove, bool CallOpp, bool Call)>();
            while (r.Read())
                l.Add((r.GetString(0), DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.IsDBNull(2) ? "" : r.GetString(2), r.GetInt32(3),
                    r.GetDouble(4), r.GetString(5), r.GetInt32(6) == 1, r.GetInt32(7) == 1, r.GetInt32(8) == 1, r.GetInt32(9) == 1));
            return l;
        });

        var deviations = new List<NashDeviation>();
        var summary = new Dictionary<string, (int Spots, int Dev, double Cost)>();
        int spots = 0;
        foreach (var row in rows)
        {
            int cls = HandClasses.IndexOfCards(row.Cards);
            if (cls < 0) continue;
            string? role; string mode; bool acted; string label;
            if (row.Open)
            {
                acted = row.Shove;
                if (row.Players == 2) { mode = "hu"; role = "push"; label = "heads-up como SB"; }
                else if (row.Pos == "BTN") { mode = "3h"; role = "btn_push"; label = "3-handed en el botón"; }
                else if (row.Pos == "SB") { mode = "3h"; role = "sb_push"; label = "3-handed en el SB (botón retirado)"; }
                else continue;
            }
            else
            {
                acted = row.Call;
                if (row.Players == 2) { mode = "hu"; role = "call"; label = "heads-up como BB"; }
                else if (row.Pos == "SB") { mode = "3h"; role = "sb_call"; label = "3-handed en el SB ante empuje del botón"; }
                else if (row.Pos == "BB")
                {
                    mode = "3h";
                    var shover = ShoverPosition(db, row.Id);
                    if (shover == null) continue;
                    role = shover == "BTN" ? "bb_call_btn" : "bb_call_sb";
                    label = shover == "BTN" ? "3-handed en el BB ante empuje del botón" : "3-handed en el BB ante empuje del SB";
                }
                else continue;
            }

            var chart = lib.Get(mode, row.Stack);
            double f = chart.Freq[role][cls], adv = chart.Advantage[role][cls];
            spots++;
            var name = HandClasses.Names[cls];
            bool mistake = acted ? f <= 1 - Clear : f >= Clear;
            var (n, d, c) = summary.GetValueOrDefault(name);
            double cost = Math.Abs(adv);
            summary[name] = (n + 1, d + (mistake ? 1 : 0), c + (mistake ? cost : 0));
            if (mistake) deviations.Add(new NashDeviation(row.Id, row.T, row.Cards, name, row.Pos, row.Stack, label, acted, acted ? f : f, cost));
        }
        var byHand = summary.Where(kv => kv.Value.Dev > 0)
            .Select(kv => new NashHandSummary(kv.Key, kv.Value.Spots, kv.Value.Dev, kv.Value.Cost))
            .OrderByDescending(x => x.CostBb).ToList();
        return new NashReport(spots, deviations.OrderByDescending(d => d.CostBb).ToList(), byHand);
    }

    /// <summary>Posición de quien hizo el primer empuje de la mano (para saber a quién se enfrenta el BB).</summary>
    static string? ShoverPosition(HudDatabase db, string handId)
    {
        var h = db.GetHand(handId);
        if (h == null) return null;
        var active = h.Seats.Where(s => h.Actions.Any(a => a.Player == s.Player)).OrderBy(s => s.Number).ToList();
        var pos = Positions.Assign(h, active);
        var a = h.Actions.FirstOrDefault(x => x.AllIn && x.Kind == ActionKind.Raise);
        return a == null ? null : pos.GetValueOrDefault(a.Player);
    }
}
