namespace VergilHud.Core;

/// <summary>Estadísticas acumuladas de un jugador a lo largo de todas sus manos.</summary>
public sealed class PlayerStats
{
    public string Player { get; set; } = "";
    public int Hands { get; set; }

    public int Vpip { get; set; }
    public int Pfr { get; set; }
    public int ThreeBetOpp { get; set; }
    public int ThreeBet { get; set; }
    public int FoldTo3BetOpp { get; set; }
    public int FoldTo3Bet { get; set; }
    public int StealOpp { get; set; }
    public int Steal { get; set; }
    public int FoldToStealOpp { get; set; }
    public int FoldToSteal { get; set; }
    public int SawFlop { get; set; }
    public int CbetOpp { get; set; }
    public int Cbet { get; set; }
    public int FoldToCbetOpp { get; set; }
    public int FoldToCbet { get; set; }
    public int Wtsd { get; set; }
    public int Wsd { get; set; }
    public int PostflopBets { get; set; }
    public int PostflopCalls { get; set; }
    public int PostflopFolds { get; set; }
    public double Net { get; set; }
    public double NetBb { get; set; }

    public double? VpipPct => Pct(Vpip, Hands);
    public double? PfrPct => Pct(Pfr, Hands);
    public double? ThreeBetPct => Pct(ThreeBet, ThreeBetOpp);
    public double? FoldTo3BetPct => Pct(FoldTo3Bet, FoldTo3BetOpp);
    public double? StealPct => Pct(Steal, StealOpp);
    public double? FoldToStealPct => Pct(FoldToSteal, FoldToStealOpp);
    public double? CbetPct => Pct(Cbet, CbetOpp);
    public double? FoldToCbetPct => Pct(FoldToCbet, FoldToCbetOpp);
    public double? WtsdPct => Pct(Wtsd, SawFlop);
    public double? WsdPct => Pct(Wsd, Wtsd);

    /// <summary>Factor de agresión postflop: (bets + raises) / calls.</summary>
    public double? Af => PostflopCalls == 0 ? (PostflopBets > 0 ? PostflopBets : null) : (double)PostflopBets / PostflopCalls;

    /// <summary>Frecuencia de agresión postflop en %.</summary>
    public double? Afq => Pct(PostflopBets, PostflopBets + PostflopCalls + PostflopFolds);

    public double? BbPer100 => Hands == 0 ? null : NetBb * 100.0 / Hands;

    /// <summary>Clasificación orientativa del jugador según su muestra.</summary>
    public string Profile
    {
        get
        {
            if (Hands < 30) return "Pocos datos";
            double v = VpipPct ?? 0, p = PfrPct ?? 0, af = Af ?? 0;
            if (v >= 45 && p < 12) return "Whale / calling station";
            if (v >= 35 && af < 1.5) return "Fish pasivo";
            if (v >= 35) return "Maníaco";
            if (v <= 14) return "Nit";
            if (v <= 26 && v - p <= 6) return "TAG / regular";
            if (v > 26 && p >= 18) return "LAG";
            return "Loose-pasivo";
        }
    }

    static double? Pct(int x, int n) => n == 0 ? null : x * 100.0 / n;

    public static string Fmt(double? v, string format = "0") => v is null ? "-" : v.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
}
