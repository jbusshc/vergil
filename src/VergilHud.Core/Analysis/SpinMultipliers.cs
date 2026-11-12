namespace VergilHud.Core;

/// <param name="Expected">Cuántas veces se esperaría ese multiplicador entre tus victorias.</param>
/// <param name="Lo">Extremo bajo del intervalo del 95 % de lo que es normal ver (aproximación normal/Poisson), y <paramref name="Hi"/> el alto.</param>
public sealed record MultiplierComparison(double Multiplier, double Probability, int Observed, double Expected, double Lo, double Hi)
{
    public bool Outside => Observed < Lo || Observed > Hi;
}

/// <summary>
/// Multiplicadores y probabilidades de los Spin &amp; Go de 3 jugadores según la tabla publicada por PokerStars
/// (pokerstars.com/es-419/poker/spin-and-go-max/payouts, consultada el 2026-11-12). Las probabilidades están en «X de 10.000.000».
/// Los premios mayores no cambian con la inscripción; solo varían el reparto entre ×3 y ×2.
/// </summary>
public static class SpinMultipliers
{
    const double Total = 10_000_000;
    public static readonly double[] BuyIns = { 1, 2, 5, 10, 25, 50 };

    static readonly (double M, double N)[] Fixed =
    {
        (12000, 10), (100, 500), (50, 1000), (25, 7500), (10, 100_000), (5, 250_000), (4, 900_000),
    };

    /// <summary>Cuántos «de 10.000.000» salen ×3 para cada inscripción (el resto, hasta completar, sale ×2).</summary>
    static readonly Dictionary<double, double> Threes = new()
    {
        [1] = 3_560_520, [2] = 3_560_520, [5] = 3_860_520, [10] = 4_160_520, [25] = 4_460_520, [50] = 4_460_520,
    };

    /// <summary>Inscripción de la tabla más cercana (1, 2, 5, 10, 25, 50).</summary>
    public static double Nearest(double buyin) => BuyIns.OrderBy(b => Math.Abs(b - buyin)).First();

    public static IReadOnlyList<(double Multiplier, double Probability)> Distribution(double buyin)
    {
        double b = Nearest(buyin);
        double fixedN = Fixed.Sum(f => f.N), threes = Threes[b];
        var l = Fixed.Select(f => (f.M, f.N / Total)).ToList();
        l.Add((3, threes / Total));
        l.Add((2, (Total - fixedN - threes) / Total));
        return l.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>Multiplicador medio. Un jugador que gana 1 de cada 3 Spins devuelve E[×]/3 de lo que paga.</summary>
    public static double ExpectedMultiplier(double buyin) => Distribution(buyin).Sum(d => d.Multiplier * d.Probability);

    /// <summary>ROI de un jugador sin ventaja (gana 1 de cada 3): E[×]/3 − 1.</summary>
    public static double BreakEvenRoi(double buyin) => ExpectedMultiplier(buyin) / 3 - 1;

    /// <summary>
    /// Compara los multiplicadores de tus victorias con lo esperado. La ruleta se decide antes de jugar y no depende de si ganas, así que
    /// entre tus victorias el reparto debe parecerse al teórico. Los extremos son del 95 % (aproximación normal con corrección de continuidad).
    /// </summary>
    public static List<MultiplierComparison> Compare(double buyin, IEnumerable<(double Multiplier, int Count)> wins)
    {
        var observed = wins.GroupBy(w => Math.Round(w.Multiplier, 1)).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        int n = observed.Values.Sum();
        var dist = Distribution(buyin);
        var result = new List<MultiplierComparison>();
        foreach (var (m, p) in dist)
        {
            double e = n * p, sd = Math.Sqrt(n * p * (1 - p));
            result.Add(new MultiplierComparison(m, p, observed.GetValueOrDefault(Math.Round(m, 1)), e, Math.Max(0, e - 1.96 * sd - 0.5), e + 1.96 * sd + 0.5));
        }
        return result;
    }
}
