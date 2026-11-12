namespace VergilHud.Core;

/// <summary>
/// ICM de Malmuth-Harville: valor en premio de un stack. Solo tiene sentido en MTT y SNG con premios repartidos;
/// en Spin &amp; Go (el ganador se lo lleva todo) coincide con el EV de fichas (DESIGN.md §18.1).
/// </summary>
public static class Icm
{
    /// <summary>$ esperado de cada jugador. <paramref name="payouts"/> va de 1.º en adelante (los que no cobran no se listan).</summary>
    public static double[] Equities(IReadOnlyList<double> stacks, IReadOnlyList<double> payouts)
    {
        int n = stacks.Count;
        if (n > 12) throw new ArgumentException("El ICM exacto admite hasta 12 jugadores");
        var eq = new double[n];
        double total = stacks.Sum();
        if (n == 0 || total <= 0) return eq;
        int places = Math.Min(payouts.Count, n);

        void Rec(double prob, int used, int place, double remaining)
        {
            for (int i = 0; i < n; i++)
            {
                if ((used & (1 << i)) != 0 || stacks[i] <= 0) continue;
                double p = prob * stacks[i] / remaining;
                eq[i] += p * payouts[place];
                if (place + 1 < places) Rec(p, used | (1 << i), place + 1, remaining - stacks[i]);
            }
        }
        Rec(1, 0, 0, total);
        return eq;
    }

    /// <summary>Premios por defecto (fracción del bote) según el número de jugadores de un SNG. Null si no hay estructura estándar.</summary>
    public static double[]? DefaultPayouts(string format, int players) => format switch
    {
        "sng" when players >= 9 => new[] { 0.5, 0.3, 0.2 },
        "sng" when players >= 6 => new[] { 0.65, 0.35 },
        "sng" when players >= 2 => new[] { 1.0 },
        _ => null,
    };
}

/// <param name="ChipEv">Fichas ganadas de media por la decisión (equity·fichas − pérdida esperada), en bb.</param>
/// <param name="DollarEv">Valor esperado de la decisión en ICM, en fracción del bote de premios.</param>
/// <param name="Before">Valor ICM del héroe antes del all-in (fracción del bote de premios).</param>
public sealed record IcmSpot(string HandId, string HeroCards, string Villain, double Equity, double EffectiveBb, double ChipEv, double Before,
    double IfWin, double IfLose, double DollarEv)
{
    /// <summary>La decisión gana fichas pero pierde valor en premio.</summary>
    public bool IcmTax => ChipEv > 0 && DollarEv < 0;
}

public static class IcmAnalysis
{
    /// <summary>Analiza los all-in del héroe en MTT/SNG (los más recientes primero). Los Spin se excluyen: ahí el ICM es el EV de fichas.</summary>
    public static List<IcmSpot> ScanHero(HudDatabase db, IReadOnlyList<double> payouts, int limit = 300)
    {
        var ids = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hand_id FROM player_hand_facts WHERE is_hero = 1 AND went_allin = 1 AND format IN ('mtt','sng') " +
                              "ORDER BY played_at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var l = new List<string>();
            while (r.Read()) l.Add(r.GetString(0));
            return l;
        });
        var result = new List<IcmSpot>();
        foreach (var id in ids)
            if (db.GetHand(id) is { } h && Analyze(h, payouts) is { } s) result.Add(s);
        return result;
    }

    /// <summary>
    /// Analiza el all-in del héroe contra un único rival cuando se ven las cartas de ambos. Se toman los stacks de la mesa al empezar
    /// la mano como el campo completo (SNG o mesa final) e ignora ciegas y antes, así que es una aproximación para estudiar.
    /// </summary>
    public static IcmSpot? Analyze(Hand h, IReadOnlyList<double> payouts)
    {
        if (h.Hero == null || h.HeroCards == null || !h.IsTournament || h.Format == "spin") return null;
        var allIn = h.Actions.Where(a => a.AllIn).Select(a => a.Player).Distinct().ToList();
        if (allIn.Count == 0) return null;

        var replay = HandReplay.Build(h);
        var step = replay.Steps.FirstOrDefault(s => s.Equity is { Count: 2 } && s.Equity.ContainsKey(h.Hero));
        if (step?.Equity == null) return null;
        var villain = step.Equity.Keys.First(k => k != h.Hero);
        if (!allIn.Contains(h.Hero) && !allIn.Contains(villain)) return null;

        var seats = h.Seats.OrderBy(s => s.Number).ToList();
        var stacks = seats.Select(s => (double)s.Stack).ToList();
        int ih = seats.FindIndex(s => s.Player == h.Hero), iv = seats.FindIndex(s => s.Player == villain);
        if (ih < 0 || iv < 0) return null;
        double eff = Math.Min(stacks[ih], stacks[iv]);
        double e = step.Equity[h.Hero];

        var before = Icm.Equities(stacks, payouts)[ih];
        var win = stacks.ToList(); win[ih] += eff; win[iv] -= eff;
        var lose = stacks.ToList(); lose[ih] -= eff; lose[iv] += eff;
        double ifWin = Icm.Equities(win, payouts)[ih], ifLose = Icm.Equities(lose, payouts)[ih];
        double dollar = e * ifWin + (1 - e) * ifLose - before;
        double bb = h.BigBlind > 0 ? (double)h.BigBlind : 1;
        double chip = (2 * e - 1) * eff / bb;
        return new IcmSpot(h.Id, h.HeroCards, villain, e, eff / bb, chip, before, ifWin, ifLose, dollar);
    }
}
