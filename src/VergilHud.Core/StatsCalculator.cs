namespace VergilHud.Core;

/// <summary>Calcula las estadísticas por jugador de una mano ya parseada.</summary>
public static class StatsCalculator
{
    static readonly string[] StealPositions = { "CO", "BTN", "SB" };
    static readonly string[] LabelsFromButton = { "BTN", "CO", "HJ", "MP", "UTG+2", "UTG+1" };

    public static List<PlayerHandStats> Compute(Hand h)
    {
        var participants = new HashSet<string>(h.Actions.Select(a => a.Player));
        if (h.Hero != null) participants.Add(h.Hero);
        // Un jugador "sitting out" que pone ciegas (desconectado en torneo) sí juega la mano; uno sin acciones no.
        var active = h.Seats.Where(s => participants.Contains(s.Player)).OrderBy(s => s.Number).ToList();
        var stats = active.ToDictionary(s => s.Player, s => new PlayerHandStats { Player = s.Player, Seat = s.Number });
        if (stats.Count == 0) return new();

        AssignPositions(h, active, stats);
        var folded = new HashSet<string>();
        var pfAggressor = Preflop(h, stats, folded);
        Postflop(h, stats, folded, pfAggressor);
        Money(h, stats);

        foreach (var s in stats.Values)
        {
            s.Wtsd = s.SawFlop && h.HasShowdown && !folded.Contains(s.Player);
            s.Wsd = s.Wtsd && h.Collected.GetValueOrDefault(s.Player) > 0;
        }
        return stats.Values.ToList();
    }

    static void AssignPositions(Hand h, List<Seat> active, Dictionary<string, PlayerHandStats> stats)
    {
        int n = Math.Max(h.MaxSeats, active.Max(s => s.Number));
        var ordered = active.OrderBy(s => ((s.Number - h.ButtonSeat - 1) % n + n) % n).Select(s => s.Player).ToList();
        var sb = h.Actions.FirstOrDefault(a => a.Kind == ActionKind.PostSmallBlind)?.Player;
        var bb = h.Actions.FirstOrDefault(a => a.Kind == ActionKind.PostBigBlind)?.Player;
        if (sb != null && stats.ContainsKey(sb)) stats[sb].Position = "SB";
        if (bb != null && stats.ContainsKey(bb)) stats[bb].Position = "BB";

        var others = ordered.Where(p => p != sb && p != bb).ToList();
        int k = others.Count;
        for (int j = 0; j < k; j++)
        {
            int fromEnd = k - 1 - j;
            stats[others[j]].Position = j == 0 && k >= 4 ? "UTG"
                : fromEnd < LabelsFromButton.Length ? LabelsFromButton[fromEnd] : "UTG";
        }
    }

    /// <summary>Procesa la calle preflop. Devuelve el último agresor (para C-Bet).</summary>
    static string? Preflop(Hand h, Dictionary<string, PlayerHandStats> stats, HashSet<string> folded)
    {
        int raises = 0, limpers = 0, callersSinceRaise = 0;
        string? opener = null, lastAggressor = null;
        bool stealAttempt = false;
        var acted = new HashSet<string>();

        foreach (var a in h.Actions.Where(a => a.Street == Street.Preflop && !a.IsPost))
        {
            if (!stats.TryGetValue(a.Player, out var s)) continue;
            bool first = acted.Add(a.Player);

            if (raises == 1 && a.Player != opener && !s.ThreeBetOpp)
            {
                s.ThreeBetOpp = true;
                s.ThreeBet = a.Kind == ActionKind.Raise;
            }
            if (raises == 2 && a.Player == opener && !s.FoldTo3BetOpp)
            {
                s.FoldTo3BetOpp = true;
                s.FoldTo3Bet = a.Kind == ActionKind.Fold;
            }
            if (first && raises == 0 && limpers == 0 && StealPositions.Contains(s.Position))
            {
                s.StealOpp = true;
                s.Steal = a.Kind == ActionKind.Raise;
            }
            if (first && raises == 1 && stealAttempt && callersSinceRaise == 0 && s.Position is "SB" or "BB")
            {
                s.FoldToStealOpp = true;
                s.FoldToSteal = a.Kind == ActionKind.Fold;
            }

            switch (a.Kind)
            {
                case ActionKind.Fold:
                    folded.Add(a.Player);
                    break;
                case ActionKind.Call:
                    s.Vpip = true;
                    if (raises == 0) limpers++; else callersSinceRaise++;
                    break;
                case ActionKind.Raise:
                case ActionKind.Bet:
                    s.Vpip = true;
                    s.Pfr = true;
                    raises++;
                    if (raises == 1) { opener = a.Player; stealAttempt = s.Steal; }
                    callersSinceRaise = 0;
                    lastAggressor = a.Player;
                    break;
            }
        }
        return lastAggressor;
    }

    static void Postflop(Hand h, Dictionary<string, PlayerHandStats> stats, HashSet<string> folded, string? pfAggressor)
    {
        if (h.Board.Count >= 3)
            foreach (var s in stats.Values)
                if (!folded.Contains(s.Player)) s.SawFlop = true;

        bool betMade = false, cbetMade = false, raisedAfterCbet = false;
        foreach (var a in h.Actions.Where(a => a.Street != Street.Preflop && !a.IsPost))
        {
            if (!stats.TryGetValue(a.Player, out var s)) continue;

            if (a.Street == Street.Flop)
            {
                if (a.Player == pfAggressor && !betMade && !s.CbetOpp)
                {
                    s.CbetOpp = true;
                    if (a.Kind == ActionKind.Bet) { s.Cbet = true; cbetMade = true; }
                }
                else if (cbetMade && !raisedAfterCbet && a.Player != pfAggressor && !s.FoldToCbetOpp)
                {
                    s.FoldToCbetOpp = true;
                    s.FoldToCbet = a.Kind == ActionKind.Fold;
                }
                if (a.IsAggressive)
                {
                    betMade = true;
                    if (a.Kind == ActionKind.Raise && cbetMade) raisedAfterCbet = true;
                }
            }

            switch (a.Kind)
            {
                case ActionKind.Bet:
                case ActionKind.Raise: s.PostflopBets++; break;
                case ActionKind.Call: s.PostflopCalls++; break;
                case ActionKind.Fold: s.PostflopFolds++; folded.Add(a.Player); break;
            }
        }
    }

    static void Money(Hand h, Dictionary<string, PlayerHandStats> stats)
    {
        var invested = new Dictionary<string, decimal>();
        var street = new Dictionary<string, decimal>();
        var current = Street.Preflop;

        void Flush()
        {
            foreach (var (p, v) in street) invested[p] = invested.GetValueOrDefault(p) + v;
            street.Clear();
        }

        foreach (var a in h.Actions)
        {
            if (a.Street != current) { Flush(); current = a.Street; }
            switch (a.Kind)
            {
                case ActionKind.PostAnte:
                    invested[a.Player] = invested.GetValueOrDefault(a.Player) + a.Amount;
                    break;
                case ActionKind.PostDead:
                    // SB muerta + BB viva
                    invested[a.Player] = invested.GetValueOrDefault(a.Player) + Math.Max(0, a.Amount - h.BigBlind);
                    street[a.Player] = street.GetValueOrDefault(a.Player) + Math.Min(a.Amount, h.BigBlind);
                    break;
                case ActionKind.PostSmallBlind:
                case ActionKind.PostBigBlind:
                case ActionKind.Call:
                case ActionKind.Bet:
                    street[a.Player] = street.GetValueOrDefault(a.Player) + a.Amount;
                    break;
                case ActionKind.Raise:
                    street[a.Player] = a.ToAmount;
                    break;
            }
        }
        Flush();

        var bb = h.BigBlind == 0 ? 1 : h.BigBlind;
        foreach (var s in stats.Values)
        {
            s.Net = h.Collected.GetValueOrDefault(s.Player) + h.Returned.GetValueOrDefault(s.Player) - invested.GetValueOrDefault(s.Player);
            s.NetBb = Math.Round(s.Net / bb, 2);
        }
    }
}
