namespace VergilHud.Core;

/// <summary>Posiciones de la mesa (UTG, UTG1, UTG2, MP, HJ, CO, BTN, SB, BB) y orden de acción postflop.</summary>
public static class Positions
{
    static readonly string[] Back = { "HJ", "CO", "BTN" };
    static readonly string[] Front = { "UTG", "UTG1", "UTG2" };

    static int Distance(Hand h, Seat s, int n) => ((s.Number - h.ButtonSeat - 1) % n + n) % n;

    static int TableSize(Hand h, IReadOnlyList<Seat> active) => Math.Max(h.MaxSeats, active.Max(s => s.Number));

    /// <summary>Jugadores en el orden en que actúan postflop (el primero a la izquierda del botón; el botón, último).</summary>
    public static IReadOnlyList<string> PostflopOrder(Hand h, IReadOnlyList<Seat> active)
    {
        if (active.Count == 0) return Array.Empty<string>();
        int n = TableSize(h, active);
        return active.OrderBy(s => Distance(h, s, n)).Select(s => s.Player).ToList();
    }

    public static Dictionary<string, string> Assign(Hand h, IReadOnlyList<Seat> active)
    {
        var result = new Dictionary<string, string>();
        if (active.Count == 0) return result;
        var ordered = PostflopOrder(h, active);
        var sb = h.Actions.FirstOrDefault(a => a.Kind == ActionKind.PostSmallBlind)?.Player;
        var bb = h.Actions.FirstOrDefault(a => a.Kind == ActionKind.PostBigBlind)?.Player;
        if (sb != null) result[sb] = "SB";
        if (bb != null) result[bb] = "BB";

        var others = ordered.Where(p => p != sb && p != bb).ToList();
        int k = others.Count, nb = Math.Min(3, k), nf = k - nb;
        for (int j = 0; j < k; j++)
        {
            if (j >= nf) result[others[j]] = Back[3 - nb + (j - nf)];
            else result[others[j]] = nf == 1 ? "UTG" : j == nf - 1 ? "MP" : Front[j];
        }
        return result;
    }
}
