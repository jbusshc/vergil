namespace VergilHud.Core;

public enum Street { Preflop, Flop, Turn, River }

public enum ActionKind { PostSmallBlind, PostBigBlind, PostAnte, PostDead, Fold, Check, Call, Bet, Raise }

/// <summary>Una acción en la mano. Para Raise, Amount es el incremento y ToAmount el total de la calle.</summary>
public sealed record HandAction(string Player, Street Street, ActionKind Kind, decimal Amount, decimal ToAmount, bool AllIn)
{
    public bool IsPost => Kind is ActionKind.PostSmallBlind or ActionKind.PostBigBlind or ActionKind.PostAnte or ActionKind.PostDead;
    public bool IsAggressive => Kind is ActionKind.Bet or ActionKind.Raise;
}

public sealed class Seat
{
    public int Number { get; set; }
    public string Player { get; set; } = "";
    public decimal Stack { get; set; }
    public bool SittingOut { get; set; }
}

/// <summary>Resultado de un torneo anunciado en la mano de la eliminación o del final ("finished in 2nd place").</summary>
public sealed record TournamentResult(string Player, int Place, decimal? Prize);

public sealed class Hand
{
    /// <summary>Partes de la inscripción tal como aparecen en la cabecera: buy-in, [bounty], cuota.</summary>
    public List<decimal> BuyinParts { get; } = new();
    public List<TournamentResult> Results { get; } = new();
    public string Id { get; set; } = "";
    public string Site { get; set; } = "PokerStars";
    public string TableName { get; set; } = "";
    public string Game { get; set; } = "";
    public bool IsTournament { get; set; }
    /// <summary>cash | mtt | spin | zoom. SNG y MTT no se distinguen solo con la mano: se afina con el summary (Fase 4).</summary>
    public string Format { get; set; } = "cash";
    public string? TournamentId { get; set; }
    /// <summary>Mano de dinero ficticio (fichas de práctica): la cabecera no lleva moneda. Sus estadísticas se guardan aparte.</summary>
    public bool IsPlayMoney { get; set; }
    public decimal? Buyin { get; set; }
    public int Level { get; set; }
    public decimal Ante { get; set; }
    public decimal Pot { get; set; }
    public decimal Rake { get; set; }
    public DateTime Time { get; set; }
    public decimal SmallBlind { get; set; }
    public decimal BigBlind { get; set; }
    public int MaxSeats { get; set; }
    public int ButtonSeat { get; set; }
    public string? Hero { get; set; }
    public string? HeroCards { get; set; }
    public bool HasShowdown { get; set; }
    public List<string> Board { get; } = new();
    public List<Seat> Seats { get; } = new();
    public List<HandAction> Actions { get; } = new();
    public Dictionary<string, decimal> Collected { get; } = new();
    public Dictionary<string, decimal> Returned { get; } = new();
    public Dictionary<string, string> ShownCards { get; } = new();
    public string Raw { get; set; } = "";
}

/// <summary>Contadores de un jugador en una sola mano (oportunidades y realizaciones).</summary>
public sealed class PlayerHandStats
{
    public string Player { get; set; } = "";
    public int Seat { get; set; }
    public string Position { get; set; } = "";

    public bool Vpip { get; set; }
    public bool Pfr { get; set; }
    public bool ThreeBetOpp { get; set; }
    public bool ThreeBet { get; set; }
    public bool FoldTo3BetOpp { get; set; }
    public bool FoldTo3Bet { get; set; }
    public bool StealOpp { get; set; }
    public bool Steal { get; set; }
    public bool FoldToStealOpp { get; set; }
    public bool FoldToSteal { get; set; }
    public bool SawFlop { get; set; }
    public bool CbetOpp { get; set; }
    public bool Cbet { get; set; }
    public bool FoldToCbetOpp { get; set; }
    public bool FoldToCbet { get; set; }
    public bool Wtsd { get; set; }
    public bool Wsd { get; set; }

    public int PostflopBets { get; set; }
    public int PostflopCalls { get; set; }
    public int PostflopFolds { get; set; }

    public decimal Net { get; set; }
    public decimal NetBb { get; set; }

    /// <summary>Orden idéntico a HudDatabase.FlagColumns.</summary>
    public bool[] Flags() => new[]
    {
        Vpip, Pfr, ThreeBetOpp, ThreeBet, FoldTo3BetOpp, FoldTo3Bet, StealOpp, Steal,
        FoldToStealOpp, FoldToSteal, SawFlop, CbetOpp, Cbet, FoldToCbetOpp, FoldToCbet, Wtsd, Wsd,
    };
}
