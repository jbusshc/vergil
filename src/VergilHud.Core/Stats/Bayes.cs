namespace VergilHud.Core;

/// <summary>Resultado bayesiano de una proporción (DESIGN.md §6.3).</summary>
/// <param name="PriorMean">Media de la población usada como prior.</param>
/// <param name="Posterior">Media posterior: lo que se muestra en modo Honesto.</param>
/// <param name="Raw">x/n crudo (null si n = 0).</param>
/// <param name="Lo">Extremo inferior del intervalo creíble del 80 % (con prior).</param>
/// <param name="Hi">Extremo superior del intervalo creíble del 80 % (con prior).</param>
/// <param name="Reliability">0 = sin datos, 1 = muestra muy fiable. Se calcula sin el prior (ver Bayes.Posterior).</param>
public readonly record struct BayesResult(double PriorMean, double Posterior, double? Raw, double Lo, double Hi, double Reliability, double N);

public static class Bayes
{
    /// <summary>z de un intervalo bilateral del 80 %.</summary>
    public const double Z80 = 1.2815515655446004;

    /// <summary>
    /// Posterior de una proporción: prior Beta(k·μ, k·(1−μ)) + x aciertos en n intentos.
    /// El valor mostrado y el intervalo usan el prior; la <b>fiabilidad</b> usa un intervalo con prior uniforme, es decir, solo
    /// los datos del jugador: así un prior fuerte no hace parecer fiable a un jugador del que no sabemos nada.
    /// Fiabilidad = 1 − clamp(ancho / 0,5).
    /// </summary>
    public static BayesResult Posterior(double x, double n, double priorMean, double strength = 10)
    {
        if (n < 0 || x < 0 || x > n) throw new ArgumentOutOfRangeException(nameof(x), "Se esperaba 0 ≤ x ≤ n");
        double mu = Math.Clamp(priorMean, 0.001, 0.999);
        double k = Math.Max(strength, 0.1);

        double a = k * mu + x, b = k * (1 - mu) + (n - x);
        var (mean, lo, hi) = Interval(a, b);

        var (_, dlo, dhi) = Interval(1 + x, 1 + n - x);   // solo datos: Beta(1+x, 1+n-x)
        double reliability = 1 - Math.Clamp((dhi - dlo) / 0.5, 0, 1);

        return new BayesResult(mu, mean, n > 0 ? x / n : null, lo, hi, reliability, n);
    }

    /// <summary>
    /// Intervalo creíble usando solo los datos (prior uniforme), sin encoger hacia la población. Sirve para comparar el
    /// comportamiento reciente con el histórico sin que el prior invente diferencias. <paramref name="z"/> = 2 ≈ 95 %.
    /// </summary>
    public static (double Lo, double Hi) DataInterval(double x, double n, double z = 2.0)
    {
        double a = 1 + x, b = 1 + n - x, s = a + b;
        double mean = a / s, sd = Math.Sqrt(a * b / (s * s * (s + 1)));
        return (Math.Max(0, mean - z * sd), Math.Min(1, mean + z * sd));
    }

    /// <summary>Media e intervalo del 80 % de una Beta(a, b) con aproximación normal (suficiente para este uso).</summary>
    static (double Mean, double Lo, double Hi) Interval(double a, double b)
    {
        double s = a + b;
        double mean = a / s;
        double sd = Math.Sqrt(a * b / (s * s * (s + 1)));
        return (mean, Math.Max(0, mean - Z80 * sd), Math.Min(1, mean + Z80 * sd));
    }
}

/// <summary>
/// Media de la población (todos los rivales de tu base de datos, sin el héroe) por estadística y formato, usada como prior.
/// Se mezcla con una media de partida de la definición para que una base de datos casi vacía no dé priors ruidosos, y se
/// recalcula cada 1.000 manos importadas.
/// </summary>
public sealed class PopulationPriors
{
    readonly StatEngine _engine;
    readonly HudDatabase _db;
    readonly Dictionary<string, (double Mean, long HandsAt)> _cache = new();
    readonly object _lock = new();
    long _handCount = -1;
    DateTime _handCountAt = DateTime.MinValue;

    /// <summary>Cuántas manos nuevas hacen falta para recalcular.</summary>
    public int RefreshEvery { get; init; } = 1000;
    /// <summary>Peso (en observaciones) de la media de partida frente a los datos de la población.</summary>
    public double DefaultWeight { get; init; } = 200;
    public const double FallbackMean = 0.30;
    /// <summary>Cada cuánto se vuelve a contar las manos de la base de datos para decidir si toca recalcular.</summary>
    public TimeSpan HandCountTtl { get; init; } = TimeSpan.FromSeconds(5);

    public PopulationPriors(HudDatabase db, StatEngine engine)
    {
        _db = db;
        _engine = engine;
    }

    long HandCount()
    {
        // Evita una consulta por cada celda del HUD: el recuento solo se refresca cada pocos segundos.
        if (_handCount < 0 || (DateTime.UtcNow - _handCountAt) >= HandCountTtl)
        {
            _handCount = _db.GetHandCount();
            _handCountAt = DateTime.UtcNow;
        }
        return _handCount;
    }

    public void Invalidate()
    {
        lock (_lock) { _cache.Clear(); _handCount = -1; }
    }

    public double Mean(StatDefinition def, IReadOnlyList<string>? formats)
    {
        var key = def.Id + "|" + (formats is null ? "" : string.Join(",", formats));
        long hands;
        lock (_lock)
        {
            hands = HandCount();
            if (_cache.TryGetValue(key, out var e) && hands - e.HandsAt < RefreshEvery) return e.Mean;
        }
        var pop = _engine.GetPopulation(def.Id, formats);
        double dflt = def.Prior?.Mean ?? FallbackMean;
        double mean = (pop.Numerator + DefaultWeight * dflt) / (pop.Denominator + DefaultWeight);
        lock (_lock) _cache[key] = (mean, hands);
        return mean;
    }
}
