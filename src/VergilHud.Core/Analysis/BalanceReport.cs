using System.Globalization;

namespace VergilHud.Core;

public enum Strength { Value, Medium, Marginal, Draw, Air }

/// <summary>Clasifica la fuerza de unas cartas contra un tablero (flop, turn o river) para el diagnóstico de equilibrio.</summary>
public static class HandStrength
{
    public static string Name(Strength s) => s switch
    {
        Strength.Value => "Fuerte (set o mejor)",
        Strength.Medium => "Valor medio (par alto o sobrepar)",
        Strength.Marginal => "Marginal (par bajo)",
        Strength.Draw => "Proyecto",
        _ => "Aire",
    };

    public static Strength Classify(int[] hole, int[] board)
    {
        if (hole.Length != 2 || board.Length < 3) return Strength.Air;
        var all = hole.Concat(board).ToArray();
        int score = HandEvaluator.Evaluate(all), cat = HandEvaluator.Category(score);
        int boardCat = HandEvaluator.Category(HandEvaluator.Evaluate(board));

        if (cat >= HandEvaluator.Straight) return Strength.Value;
        if (cat >= HandEvaluator.Trips && cat > boardCat) return Strength.Value;
        if (cat == HandEvaluator.TwoPair && boardCat < HandEvaluator.TwoPair) return Strength.Value;

        int boardMax = board.Max(c => c >> 2);
        int h1 = hole[0] >> 2, h2 = hole[1] >> 2;
        if (cat == HandEvaluator.Pair && boardCat < HandEvaluator.Pair)
        {
            // El par usa una carta de la mano: ¿es sobrepar o par con la carta más alta del tablero?
            if (h1 == h2) return h1 > boardMax ? Strength.Medium : Strength.Marginal;
            int paired = board.Select(c => c >> 2).Contains(h1) ? h1 : h2;
            return paired == boardMax ? Strength.Medium : Strength.Marginal;
        }
        if (board.Length < 5 && HasDraw(hole, board)) return Strength.Draw;
        return Strength.Air;
    }

    static bool HasDraw(int[] hole, int[] board)
    {
        var all = hole.Concat(board).ToArray();
        // Proyecto de color: cuatro cartas del mismo palo, alguna de la mano
        foreach (var g in all.GroupBy(c => c & 3))
            if (g.Count() >= 4 && g.Any(c => hole.Contains(c))) return true;
        // Proyecto de escalera: al menos 4 outs que completan una escalera usando la mano
        int cur = HandEvaluator.Category(HandEvaluator.Evaluate(all));
        int outs = 0;
        var used = new HashSet<int>(all);
        for (int c = 0; c < 52; c++)
        {
            if (used.Contains(c)) continue;
            int cat = HandEvaluator.Category(HandEvaluator.Evaluate(all.Append(c).ToArray()));
            if (cat >= HandEvaluator.Straight && cat > cur && cat != HandEvaluator.Flush) outs++;
        }
        return outs >= 4;
    }
}

/// <param name="Bucket">Tamaño de la apuesta (small/medium/large/over) o «check-raise».</param>
public sealed record BalanceRow(string Street, string Bucket, int N, int Value, int Medium, int Marginal, int Draw, int Air, double NetBb)
{
    public double Share(Strength s) => N == 0 ? 0 : (s switch { Strength.Value => Value, Strength.Medium => Medium, Strength.Marginal => Marginal, Strength.Draw => Draw, _ => Air }) / (double)N;
    public double AvgNetBb => N == 0 ? 0 : NetBb / N;
}

public sealed record BalanceReport(IReadOnlyList<BalanceRow> Rows, IReadOnlyList<string> Findings, int Hands)
{
    public const int MinForFinding = 20;
    static readonly string[] Streets = { "f", "t", "r" };
    static string StreetName(string s) => s switch { "f" => "flop", "t" => "turn", _ => "river" };
    static string BucketName(string b) => b switch { "small" => "pequeña", "medium" => "mediana", "large" => "grande", "over" => "overbet", _ => b };

    /// <summary>
    /// ¿Cómo construyes tus apuestas? Para cada calle y tamaño de tu primera apuesta, qué reparto de fuerza de mano llevas y cuánto ganas.
    /// Solo se miran las manos en las que viste el tablero y tus cartas son conocidas.
    /// </summary>
    public static BalanceReport Build(HudDatabase db, int limit = 3000)
    {
        var rows = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT hand_id, hole_cards, net_bb, f_bets, f_bet_size_bucket, t_bets, t_bet_size_bucket, r_bets, r_bet_size_bucket, f_check_raise " +
                "FROM player_hand_facts WHERE is_hero = 1 AND hole_cards IS NOT NULL AND (f_bets > 0 OR t_bets > 0 OR r_bets > 0 OR f_check_raise = 1) " +
                "ORDER BY played_at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var l = new List<(string Id, string Cards, double Net, int[] Bets, string?[] Buckets, bool Cr)>();
            while (r.Read())
                l.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? 0 : r.GetDouble(2),
                    new[] { r.GetInt32(3), r.GetInt32(5), r.GetInt32(7) },
                    new[] { r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(8) ? null : r.GetString(8) },
                    r.GetInt32(9) == 1));
            return l;
        });

        var acc = new Dictionary<(string, string), int[]>();
        var net = new Dictionary<(string, string), double>();
        void Add(string street, string bucket, Strength s, double n)
        {
            var key = (street, bucket);
            if (!acc.TryGetValue(key, out var a)) acc[key] = a = new int[6];
            a[0]++;
            a[1 + (int)s]++;
            net[key] = net.GetValueOrDefault(key) + n;
        }

        int hands = 0;
        foreach (var row in rows)
        {
            var hand = db.GetHand(row.Id);
            if (hand == null) continue;
            int[] hole;
            try { hole = Cards.ParseMany(row.Cards); } catch (FormatException) { continue; }
            if (hole.Length != 2) continue;
            hands++;
            for (int i = 0; i < 3; i++)
            {
                int need = 3 + i;
                if (hand.Board.Count < need) continue;
                int[] board;
                try { board = hand.Board.Take(need).Select(Cards.Parse).ToArray(); } catch (FormatException) { continue; }
                if (hole.Intersect(board).Any()) continue;
                var s = HandStrength.Classify(hole, board);
                if (row.Bets[i] > 0 && row.Buckets[i] != null) Add(Streets[i], row.Buckets[i]!, s, row.Net);
                if (i == 0 && row.Cr) Add("f", "check-raise", s, row.Net);
            }
        }

        var table = acc.Select(kv => new BalanceRow(kv.Key.Item1, kv.Key.Item2, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3], kv.Value[4], kv.Value[5],
                net[kv.Key]))
            .OrderBy(r => Array.IndexOf(Streets, r.Street)).ThenBy(r => r.Bucket).ToList();
        return new BalanceReport(table, BuildFindings(table), hands);
    }

    public static List<string> BuildFindings(IEnumerable<BalanceRow> rows)
    {
        var l = new List<string>();
        foreach (var r in rows.Where(r => r.N >= MinForFinding))
        {
            foreach (var s in new[] { Strength.Value, Strength.Medium, Strength.Marginal, Strength.Draw, Strength.Air })
            {
                double share = r.Share(s);
                if (share < 0.85) continue;
                string what = r.Bucket == "check-raise"
                    ? $"Tu check-raise en flop es {share * 100:0}% «{HandStrength.Name(s).ToLowerInvariant()}» ({r.N} manos)."
                    : $"Tus apuestas {BucketName(r.Bucket)} en {StreetName(r.Street)} son {share * 100:0}% «{HandStrength.Name(s).ToLowerInvariant()}» ({r.N} manos).";
                l.Add(what + " Un rival atento puede explotarlo.");
            }
        }
        return l;
    }
}
