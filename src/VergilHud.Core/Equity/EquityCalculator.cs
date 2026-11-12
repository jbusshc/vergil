namespace VergilHud.Core;

/// <summary>
/// Equity de manos contra manos con el tablero conocido hasta ahora. Enumera todos los tableros posibles cuando son
/// pocos (hasta 2 millones, p. ej. heads-up preflop) y, si no, simula con Monte Carlo (semilla fija: resultado reproducible).
/// El equity de cada jugador es su cuota del bote: gana = 1, empate entre k = 1/k.
/// </summary>
public static class EquityCalculator
{
    /// <summary>
    /// Hasta este número de tableros se enumeran todos (exacto). Cubre todo lo postflop; el preflop se simula, con un error típico
    /// de ±0,15 % con 100.000 simulaciones: irrelevante para un EV y unas 30 veces más rápido que enumerar 1,7 millones de tableros.
    /// </summary>
    public const int ExactLimit = 100_000;

    public static double[] Equity(IReadOnlyList<int[]> holes, ReadOnlySpan<int> board, int monteCarloRuns = 100_000, int seed = 12345,
        int exactLimit = ExactLimit)
    {
        if (holes.Count < 2) throw new ArgumentException("Hacen falta al menos 2 manos");
        if (board.Length > 5) throw new ArgumentException("El tablero tiene como mucho 5 cartas");

        var used = new bool[52];
        foreach (var h in holes)
            foreach (var c in h)
            {
                if (used[c]) throw new ArgumentException($"Carta repetida: {Cards.ToString(c)}");
                used[c] = true;
            }
        foreach (var c in board)
        {
            if (used[c]) throw new ArgumentException($"Carta repetida: {Cards.ToString(c)}");
            used[c] = true;
        }

        var deck = new List<int>(52);
        for (int c = 0; c < 52; c++) if (!used[c]) deck.Add(c);
        int need = 5 - board.Length;
        var wins = new double[holes.Count];

        var full = new int[7];
        board.CopyTo(full);
        int boardLen = board.Length;
        long combos = Combinations(deck.Count, need);

        var scores = new int[holes.Count];
        long runs;
        if (combos <= exactLimit)
        {
            runs = combos;
            var idx = new int[Math.Max(need, 1)];
            Enumerate(deck, need, 0, 0, idx, () => Score(holes, boardLen, idx, deck, full, scores, wins));
        }
        else
        {
            runs = monteCarloRuns;
            var rng = new Random(seed);
            var picked = new int[need];
            for (long i = 0; i < runs; i++)
            {
                // Muestreo sin reposición con un barajado parcial.
                for (int k = 0; k < need; k++)
                {
                    int j = k + rng.Next(deck.Count - k);
                    (deck[k], deck[j]) = (deck[j], deck[k]);
                    picked[k] = k;
                }
                Score(holes, boardLen, picked, deck, full, scores, wins);
            }
        }
        for (int i = 0; i < wins.Length; i++) wins[i] /= runs;
        return wins;
    }

    static void Score(IReadOnlyList<int[]> holes, int boardLen, int[] idx, List<int> deck, int[] full, int[] scores, double[] wins)
    {
        int need = 5 - boardLen;
        for (int k = 0; k < need; k++) full[boardLen + k] = deck[idx[k]];
        int best = -1, bestCount = 0;
        Span<int> seven = stackalloc int[7];
        for (int p = 0; p < holes.Count; p++)
        {
            var h = holes[p];
            for (int i = 0; i < 5; i++) seven[i] = full[i];
            seven[5] = h[0];
            seven[6] = h[1];
            int s = HandEvaluator.Evaluate(seven);
            scores[p] = s;
            if (s > best) { best = s; bestCount = 1; }
            else if (s == best) bestCount++;
        }
        double share = 1.0 / bestCount;
        for (int p = 0; p < holes.Count; p++)
            if (scores[p] == best) wins[p] += share;
    }

    static void Enumerate(List<int> deck, int need, int start, int depth, int[] idx, Action visit)
    {
        if (depth == need) { visit(); return; }
        for (int i = start; i <= deck.Count - (need - depth); i++)
        {
            idx[depth] = i;
            Enumerate(deck, need, i + 1, depth + 1, idx, visit);
        }
    }

    static long Combinations(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        long r = 1;
        for (int i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }
}
