using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class EvaluatorTests
{
    static int E(string cards) => HandEvaluator.Evaluate(Cards.ParseMany(cards));

    [Theory]
    [InlineData("Ah Kh Qh Jh Th 2c 3d", HandEvaluator.StraightFlush)]
    [InlineData("9c 9d 9h 9s Ad 2c 3d", HandEvaluator.Quads)]
    [InlineData("9c 9d 9h Ks Kd 2c 3d", HandEvaluator.FullHouse)]
    [InlineData("9c 9d 9h Ks Kd Kc 3d", HandEvaluator.FullHouse)]      // dos tríos: el menor hace de pareja
    [InlineData("2h 7h 9h Jh Kh 3c 4d", HandEvaluator.Flush)]
    [InlineData("5c 6d 7h 8s 9d Kc Ad", HandEvaluator.Straight)]
    [InlineData("Ac 2d 3h 4s 5d Kc Qd", HandEvaluator.Straight)]       // rueda
    [InlineData("9c 9d 9h Ks 2d 4c 7d", HandEvaluator.Trips)]
    [InlineData("9c 9d Kh Ks 2d 4c 7d", HandEvaluator.TwoPair)]
    [InlineData("9c 9d Kh 5s 2d 4c 7d", HandEvaluator.Pair)]
    [InlineData("Ac 9d Kh 5s 2d 4c 7d", HandEvaluator.HighCard)]
    public void Detects_the_category(string cards, int category)
    {
        Assert.Equal(category, HandEvaluator.Category(E(cards)));
    }

    [Fact]
    public void Orders_hands_correctly_within_and_across_categories()
    {
        var ascending = new[]
        {
            "2c 3d 4h 5s 7c 9d Jc",   // carta alta J
            "Ac Kd Qh Jc 9s 4d 2c",   // carta alta A-K
            "2c 2d 5h 7s 9c Jd Kc",   // pareja de doses
            "Ac Ad 5h 7s 9c Jd Kc",   // pareja de ases
            "3c 3d 4h 4s 9c Jd Kc",
            "Kc Kd 4h 4s 9c Jd 2c",
            "2c 2d 2h 5s 9c Jd Kc",
            "As Ad Ah 5s 9c Jd Kc",
            "Ac 2d 3h 4s 5d 9c Jd",   // rueda (la escalera más baja)
            "6c 2d 3h 4s 5d 9c Jd",   // 6-high
            "Tc Jd Qh Ks Ad 2c 3d",   // broadway
            "2h 7h 9h Jh Kh 3c 4d",
            "2h 7h 9h Jh Ah 3c 4d",
            "2c 2d 2h 5s 5c Jd Kc",
            "3c 3d 3h 2s 2c Jd Kc",
            "2c 2d 2h 2s 9c Jd Kc",
            "2c 2d 2h 2s Ac Jd Kc",
            "5h 6h 7h 8h 9h 2c 3d",
            "Ah Kh Qh Jh Th 2c 3d",
        };
        var scores = ascending.Select(E).ToArray();
        for (int i = 1; i < scores.Length; i++)
            Assert.True(scores[i] > scores[i - 1], $"'{ascending[i]}' debería ganar a '{ascending[i - 1]}'");
    }

    [Fact]
    public void Ties_are_equal_and_kickers_decide()
    {
        Assert.Equal(E("Ac Kd Qh Jc 9s 4d 2c"), E("Ah Ks Qd Jd 9h 4c 2d"));     // distintos palos, misma mano
        Assert.True(E("Ac Ad Kh 7s 3c 2d 4h") > E("Ac Ad Qh 7s 3c 2d 4h"));      // mejor kicker
        Assert.Equal(E("Ac Ad Kh 7s 3c 2d 4h"), E("Ac Ad Kh 7s 3c 2d 9h") == 0 ? 0 : E("Ah Ad Kc 7d 3s 2h 4s")); // trivial: mismo kicker
        Assert.Equal(E("Ac Ad Kh Qs Jc 2d 4h"), E("Ah Ad Kc Qd Js 3c 4s"));      // 5.º kicker irrelevante
    }

    /// <summary>La mejor mano de 7 cartas = la mejor de sus 21 subconjuntos de 5.</summary>
    [Fact]
    public void Seven_card_score_equals_best_of_21_five_card_hands()
    {
        var rng = new Random(7);
        var deck = Enumerable.Range(0, 52).ToArray();
        for (int n = 0; n < 20_000; n++)
        {
            for (int i = 0; i < 7; i++) { int j = i + rng.Next(52 - i); (deck[i], deck[j]) = (deck[j], deck[i]); }
            var seven = deck.Take(7).ToArray();
            int best = 0;
            for (int a = 0; a < 7; a++)
                for (int b = a + 1; b < 7; b++)
                {
                    var five = seven.Where((_, i) => i != a && i != b).ToArray();
                    best = Math.Max(best, HandEvaluator.Evaluate(five));
                }
            Assert.Equal(best, HandEvaluator.Evaluate(seven));
        }
    }

    /// <summary>Contra un evaluador de referencia lento y obvio, escrito aparte con reglas explícitas.</summary>
    [Fact]
    public void Five_card_ranking_agrees_with_a_slow_reference_implementation()
    {
        var rng = new Random(11);
        var deck = Enumerable.Range(0, 52).ToArray();
        int[] Draw()
        {
            for (int i = 0; i < 5; i++) { int j = i + rng.Next(52 - i); (deck[i], deck[j]) = (deck[j], deck[i]); }
            return deck.Take(5).ToArray();
        }
        for (int n = 0; n < 60_000; n++)
        {
            var a = Draw();
            var b = Draw();
            int expected = Math.Sign(string.CompareOrdinal(Reference(a), Reference(b)));
            int actual = Math.Sign(HandEvaluator.Evaluate(a).CompareTo(HandEvaluator.Evaluate(b)));
            Assert.True(expected == actual, $"{string.Join(" ", a.Select(Cards.ToString))} vs {string.Join(" ", b.Select(Cards.ToString))}");
        }
    }

    // Clave ordenable: categoría + rangos de desempate, construida a mano con las reglas del póker.
    static string Reference(int[] hand)
    {
        var ranks = hand.Select(c => c >> 2).OrderByDescending(r => r).ToList();
        bool flush = hand.Select(c => c & 3).Distinct().Count() == 1;
        var groups = ranks.GroupBy(r => r).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToList();
        bool straight = false;
        int topStraight = ranks[0];
        if (groups.Count == 5)
        {
            if (ranks[0] - ranks[4] == 4) straight = true;
            else if (ranks.SequenceEqual(new[] { 12, 3, 2, 1, 0 })) { straight = true; topStraight = 3; }
        }
        string K(IEnumerable<int> rs) => string.Concat(rs.Select(r => (char)('a' + r)));
        int cat;
        string tie;
        if (straight && flush) { cat = 8; tie = K(new[] { topStraight }); }
        else if (groups[0].Count() == 4) { cat = 7; tie = K(groups.Select(g => g.Key)); }
        else if (groups[0].Count() == 3 && groups[1].Count() == 2) { cat = 6; tie = K(groups.Select(g => g.Key)); }
        else if (flush) { cat = 5; tie = K(ranks); }
        else if (straight) { cat = 4; tie = K(new[] { topStraight }); }
        else if (groups[0].Count() == 3) { cat = 3; tie = K(groups.Select(g => g.Key)); }
        else if (groups[0].Count() == 2 && groups[1].Count() == 2) { cat = 2; tie = K(groups.Select(g => g.Key)); }
        else if (groups[0].Count() == 2) { cat = 1; tie = K(groups.Select(g => g.Key)); }
        else { cat = 0; tie = K(ranks); }
        return cat + tie;
    }

    [Fact]
    public void Card_parsing_and_hand_names()
    {
        Assert.Equal("Ah", Cards.ToString(Cards.Parse("Ah")));
        Assert.Equal("AKs", Cards.HandName("Ah Kh"));
        Assert.Equal("AKo", Cards.HandName("Kd Ah"));
        Assert.Equal("77", Cards.HandName("7c 7d"));
        Assert.Equal("", Cards.HandName("Ah Kh Qh Jh"));
        Assert.Throws<FormatException>(() => Cards.Parse("1x"));
        Assert.Throws<FormatException>(() => Cards.Parse("Ahh"));
        Assert.Empty(Cards.ParseMany(null));
    }

    [Fact]
    public void Evaluator_is_fast_enough_for_equity_work()
    {
        var rng = new Random(3);
        var deck = Enumerable.Range(0, 52).ToArray();
        var hands = new int[10_000][];
        for (int n = 0; n < hands.Length; n++)
        {
            for (int i = 0; i < 7; i++) { int j = i + rng.Next(52 - i); (deck[i], deck[j]) = (deck[j], deck[i]); }
            hands[n] = deck.Take(7).ToArray();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long acc = 0;
        for (int rep = 0; rep < 200; rep++)
            foreach (var h in hands) acc += HandEvaluator.Evaluate(h);
        sw.Stop();
        double perSec = 2_000_000 / sw.Elapsed.TotalSeconds;
        Assert.True(acc != 0);
#if DEBUG
        Assert.True(perSec > 300_000, $"{perSec:N0} evaluaciones/s (Debug)");
#else
        Assert.True(perSec > 3_000_000, $"{perSec:N0} evaluaciones/s");
#endif
    }
}

public class EquityTests
{
    static double[] Eq(string a, string b, string board = "") =>
        EquityCalculator.Equity(new[] { Cards.ParseMany(a), Cards.ParseMany(b) }, Cards.ParseMany(board));

    [Fact]
    public void Aces_versus_kings_preflop_is_about_82_percent()
    {
        var e = Eq("Ah Ad", "Kh Kd");
        Assert.InRange(e[0], 0.815, 0.830);
        Assert.Equal(1.0, e.Sum(), 9);
    }

    [Theory]
    [InlineData("Ah Kh", "Qc Qd", 0.425, 0.475)]   // AKs contra QQ: ~46 %
    [InlineData("Ah Kd", "2c 2d", 0.465, 0.495)]   // AKo contra 22: ~48 %
    [InlineData("Ah Ad", "7c 2d", 0.860, 0.895)]   // AA contra 72o: ~88 %
    [InlineData("Ac Ad", "Ah As", 0.49, 0.51)]     // mismas manos: ~50 %
    public void Known_preflop_matchups(string a, string b, double lo, double hi)
    {
        Assert.InRange(Eq(a, b)[0], lo, hi);
    }

    [Fact]
    public void Complete_board_gives_a_deterministic_result()
    {
        var win = Eq("Ah Ad", "Kh Kd", "2c 7d Jh 3s 9c");
        Assert.Equal(new[] { 1.0, 0.0 }, win);
        var chop = Eq("Ah Kd", "As Kc", "2c 7d Jh 3s 9c");
        Assert.Equal(new[] { 0.5, 0.5 }, chop);
    }

    [Fact]
    public void Flop_and_turn_equities_make_sense()
    {
        // Proyecto de color + proyecto de escalera frente a una pareja
        var e = Eq("Ah Kh", "Qc Qd", "Jh Th 2c");
        Assert.InRange(e[0], 0.55, 0.65);
        // Con una carta por salir: 9 outs de color + 3 ases... contra un set ya hecho
        var t = Eq("Ah 5h", "Kc Kd", "Kh 8h 2c 9d");
        Assert.Equal(1.0, t.Sum(), 9);
        Assert.InRange(t[0], 0.0, 0.35);
    }

    [Fact]
    public void Three_way_monte_carlo_is_reproducible_and_sums_to_one()
    {
        var holes = new[] { Cards.ParseMany("Ah Ad"), Cards.ParseMany("Kh Kd"), Cards.ParseMany("Qc Qs") };
        var a = EquityCalculator.Equity(holes, ReadOnlySpan<int>.Empty, 100_000, seed: 5);
        var b = EquityCalculator.Equity(holes, ReadOnlySpan<int>.Empty, 100_000, seed: 5);
        Assert.Equal(a, b);
        Assert.Equal(1.0, a.Sum(), 9);
        Assert.True(a[0] > a[1] + 0.3 && a[0] > a[2] + 0.3);   // AA domina
        Assert.InRange(Math.Abs(a[1] - a[2]), 0.0, 0.03);        // KK y QQ van casi igualados
        Assert.InRange(a[0], 0.60, 0.75);
    }

    [Fact]
    public void Monte_carlo_agrees_with_exact_enumeration()
    {
        // Un tablero de 3 cartas contra dos manos se enumera exacto; se compara con una simulación forzada.
        var holes = new[] { Cards.ParseMany("Ah Kh"), Cards.ParseMany("9c 9d") };
        var exact = EquityCalculator.Equity(holes, Cards.ParseMany("2c 7d Jh"));
        // Mismo caso con tres jugadores (más combinaciones que el límite exacto no se alcanza en flop, así que se valida el total)
        Assert.Equal(1.0, exact.Sum(), 9);
        var preflop = EquityCalculator.Equity(holes, ReadOnlySpan<int>.Empty, exactLimit: 2_000_000);   // exacto (1,7 M)
        var mc = EquityCalculator.Equity(holes, ReadOnlySpan<int>.Empty, 300_000, seed: 9);
        Assert.Equal(preflop[0], mc[0], 2);
    }

    [Fact]
    public void Invalid_input_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Eq("Ah Ad", "Ah Kd"));                         // carta repetida
        Assert.Throws<ArgumentException>(() => Eq("Ah Ad", "Kh Kd", "Ah 2c 3d"));              // repetida con el tablero
        Assert.Throws<ArgumentException>(() => EquityCalculator.Equity(new[] { Cards.ParseMany("Ah Ad") }, ReadOnlySpan<int>.Empty));
    }
}
