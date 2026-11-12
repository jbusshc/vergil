using System.Collections.Concurrent;
using System.Globalization;

namespace VergilHud.Core;

/// <summary>Un asiento en un instante de la mano. Las cifras van en fichas; la interfaz las convierte a bb si se quiere.</summary>
/// <param name="Cards">Cartas visibles: las del héroe, y las de los demás al llegar al showdown o al mostrar el equity.</param>
public sealed record ReplaySeat(int Seat, string Player, string Position, decimal Stack, decimal Bet, bool Folded, bool AllIn,
    bool IsHero, bool IsButton, string? Cards, decimal Won);

/// <summary>Aritmética de la decisión del héroe en una mano ya terminada (no es consejo: son números del bote en ese momento).</summary>
/// <param name="PotOddsPct">Lo que hay que pagar / (bote + lo que hay que pagar), en %.</param>
/// <param name="Spr">Stack efectivo / bote antes de actuar.</param>
public sealed record DecisionInfo(decimal PotBefore, decimal ToCall, double PotOddsPct, double Spr);

public sealed record ReplayStep(int Index, Street Street, string Description, string? Actor, decimal Pot, IReadOnlyList<string> Board,
    IReadOnlyList<ReplaySeat> Seats, DecisionInfo? Decision, IReadOnlyDictionary<string, double>? Equity, bool IsDeal, bool IsFinal);

/// <summary>
/// Reconstruye una mano paso a paso: cada ciega y cada acción, el reparto de calles, la devolución de apuestas sin igualar,
/// el showdown y el resultado. Si acaba con all-in y se ven las cartas, añade el equity de cada jugador en cada calle.
/// </summary>
public sealed class HandReplay
{
    public Hand Hand { get; }
    public decimal BigBlind { get; }
    public IReadOnlyList<ReplayStep> Steps { get; }

    HandReplay(Hand h, decimal bb, IReadOnlyList<ReplayStep> steps)
    {
        Hand = h;
        BigBlind = bb;
        Steps = steps;
    }

    /// <summary>Índice del primer paso de una calle (el reparto de las cartas, o la primera acción si es preflop).</summary>
    public int FirstStepOf(Street street)
    {
        if (street == Street.Preflop) return 0;
        for (int i = 0; i < Steps.Count; i++)
            if (Steps[i].Street == street && Steps[i].IsDeal) return i;
        return -1;
    }

    sealed class Mut
    {
        public int Seat;
        public string Player = "";
        public string Position = "";
        public decimal Stack, Bet, Won;
        public bool Folded, AllIn;
        public string? Cards;
    }

    static readonly ConcurrentDictionary<string, double[]> EquityCache = new();

    static string StreetName(Street s) => s switch { Street.Flop => "Flop", Street.Turn => "Turn", Street.River => "River", _ => "Preflop" };

    public static HandReplay Build(Hand h, Func<decimal, string>? money = null)
    {
        money ??= v => v.ToString("0.##", CultureInfo.InvariantCulture);
        var participants = new HashSet<string>(h.Actions.Select(a => a.Player));
        if (h.Hero != null) participants.Add(h.Hero);
        var active = h.Seats.Where(s => participants.Contains(s.Player)).OrderBy(s => s.Number).ToList();
        var pos = Positions.Assign(h, active);
        decimal bb = h.BigBlind > 0 ? h.BigBlind : 1;

        var st = active.ToDictionary(s => s.Player, s => new Mut
        {
            Seat = s.Number,
            Player = s.Player,
            Position = pos.GetValueOrDefault(s.Player, ""),
            Stack = s.Stack,
            Cards = s.Player == h.Hero ? h.HeroCards : null,
        });

        var steps = new List<ReplayStep>();
        var board = new List<string>();
        decimal potBase = 0;
        var cur = Street.Preflop;
        int lastAllInStep = -1;

        void Snap(string desc, string? actor, DecisionInfo? dec = null, bool deal = false, bool final = false)
        {
            decimal pot = potBase + st.Values.Sum(x => x.Bet);
            var seats = st.Values.OrderBy(m => m.Seat).Select(m => new ReplaySeat(m.Seat, m.Player, m.Position, m.Stack, m.Bet, m.Folded,
                m.AllIn, m.Player == h.Hero, m.Seat == h.ButtonSeat, m.Cards, m.Won)).ToList();
            steps.Add(new ReplayStep(steps.Count, cur, desc, actor, pot, board.ToList(), seats, dec, null, deal, final));
        }

        void CloseStreet(Street next)
        {
            potBase += st.Values.Sum(x => x.Bet);
            foreach (var m in st.Values) m.Bet = 0;
            cur = next;
            int need = next switch { Street.Flop => 3, Street.Turn => 4, Street.River => 5, _ => 0 };
            board.Clear();
            board.AddRange(h.Board.Take(need));
            Snap($"{StreetName(next)} [{string.Join(" ", board)}]", null, deal: true);
        }

        Snap("Inicio de la mano", null);

        foreach (var a in h.Actions)
        {
            if (!st.TryGetValue(a.Player, out var m)) continue;
            if (a.Street != cur) CloseStreet(a.Street);

            decimal maxBet = st.Values.Max(x => x.Bet);
            decimal toCall = Math.Max(0, maxBet - m.Bet);
            decimal potBefore = potBase + st.Values.Sum(x => x.Bet);

            DecisionInfo? dec = null;
            if (a.Player == h.Hero && !a.IsPost)
            {
                decimal oppMax = st.Values.Where(x => x != m && !x.Folded).Select(x => x.Stack).DefaultIfEmpty(0).Max();
                decimal effective = Math.Min(m.Stack, oppMax);
                dec = new DecisionInfo(potBefore, toCall, toCall > 0 ? (double)(toCall / (potBefore + toCall) * 100) : 0,
                    potBefore > 0 ? (double)(effective / potBefore) : 0);
            }

            string desc;
            switch (a.Kind)
            {
                case ActionKind.PostAnte:
                    m.Stack -= a.Amount; potBase += a.Amount;
                    desc = $"{a.Player} pone el ante {money(a.Amount)}";
                    break;
                case ActionKind.PostSmallBlind:
                    m.Stack -= a.Amount; m.Bet += a.Amount;
                    desc = $"{a.Player} pone la ciega pequeña {money(a.Amount)}";
                    break;
                case ActionKind.PostBigBlind:
                    m.Stack -= a.Amount; m.Bet += a.Amount;
                    desc = $"{a.Player} pone la ciega grande {money(a.Amount)}";
                    break;
                case ActionKind.PostDead:
                {
                    var live = Math.Min(a.Amount, bb);
                    m.Stack -= a.Amount; m.Bet += live; potBase += a.Amount - live;
                    desc = $"{a.Player} pone ciegas {money(a.Amount)}";
                    break;
                }
                case ActionKind.Call:
                    m.Stack -= a.Amount; m.Bet += a.Amount;
                    desc = $"{a.Player} iguala {money(a.Amount)}";
                    break;
                case ActionKind.Bet:
                    m.Stack -= a.Amount; m.Bet += a.Amount;
                    desc = potBefore > 0
                        ? $"{a.Player} apuesta {money(a.Amount)} ({Math.Round(a.Amount / potBefore * 100, MidpointRounding.AwayFromZero):0}% del bote)"
                        : $"{a.Player} apuesta {money(a.Amount)}";
                    break;
                case ActionKind.Raise:
                    m.Stack -= a.ToAmount - m.Bet; m.Bet = a.ToAmount;
                    desc = $"{a.Player} sube a {money(a.ToAmount)}";
                    break;
                case ActionKind.Fold:
                    m.Folded = true;
                    desc = $"{a.Player} se retira";
                    break;
                default:
                    desc = $"{a.Player} pasa";
                    break;
            }
            if (m.Stack < 0) m.Stack = 0;
            if (a.AllIn)
            {
                m.AllIn = true;
                desc += " (all-in)";
                lastAllInStep = steps.Count;
            }
            Snap(desc, a.Player, dec);
        }

        // Apuestas sin igualar: vuelven a su dueño
        foreach (var (p, amount) in h.Returned)
            if (st.TryGetValue(p, out var m) && amount > 0)
            {
                m.Stack += amount;
                m.Bet = Math.Max(0, m.Bet - amount);
                Snap($"Se devuelven {money(amount)} a {p} (apuesta sin igualar)", p);
            }

        // Si hubo showdown o all-in, las calles que faltan se reparten sin acción
        bool runout = h.HasShowdown || lastAllInStep >= 0;
        if (runout)
            while (board.Count < h.Board.Count)
            {
                var next = board.Count < 3 ? Street.Flop : board.Count == 3 ? Street.Turn : Street.River;
                CloseStreet(next);
            }

        if (h.HasShowdown)
        {
            foreach (var m in st.Values.Where(x => !x.Folded))
                if (h.ShownCards.TryGetValue(m.Player, out var c)) m.Cards = c;
            Snap("Showdown", null);
        }

        potBase += st.Values.Sum(x => x.Bet);
        foreach (var m in st.Values) m.Bet = 0;
        foreach (var (p, amount) in h.Collected)
            if (st.TryGetValue(p, out var m))
            {
                m.Stack += amount;
                m.Won += amount;
            }
        var pot0 = potBase;
        potBase = 0;
        var winners = st.Values.Where(x => x.Won > 0).OrderByDescending(x => x.Won).Select(x => $"{x.Player} gana {money(x.Won)}").ToList();
        cur = board.Count switch { 0 => Street.Preflop, 3 => Street.Flop, 4 => Street.Turn, _ => Street.River };
        Snap(winners.Count > 0 ? "Resultado: " + string.Join("; ", winners) : "Fin de la mano", null, final: true);

        var withEquity = AddEquity(h, steps, lastAllInStep);
        return new HandReplay(h, bb, withEquity);
    }

    /// <summary>Equity de los jugadores que siguen vivos desde el último all-in, calle a calle (si se ven las cartas de todos).</summary>
    static List<ReplayStep> AddEquity(Hand h, List<ReplayStep> steps, int fromStep)
    {
        if (fromStep < 0) return steps;
        var result = new List<ReplayStep>(steps);
        for (int i = fromStep; i < steps.Count; i++)
        {
            var s = steps[i];
            if (s.Board.Count >= 5 || s.IsFinal) continue;
            var live = s.Seats.Where(x => !x.Folded).ToList();
            if (live.Count < 2) continue;
            var cards = new List<int[]>();
            var ok = true;
            foreach (var x in live)
            {
                var text = x.IsHero ? h.HeroCards : h.ShownCards.GetValueOrDefault(x.Player);
                int[] c;
                try { c = Cards.ParseMany(text); }
                catch (FormatException) { ok = false; break; }
                if (c.Length != 2) { ok = false; break; }
                cards.Add(c);
            }
            if (!ok) continue;

            int[] board;
            try { board = s.Board.Select(Cards.Parse).ToArray(); }
            catch (FormatException) { continue; }

            var key = string.Join("|", cards.Select(c => string.Join(",", c.OrderBy(v => v)))) + "#" + string.Join(",", board);
            // Las manos se ordenan para que la clave de la caché no dependa del orden de los asientos
            var order = Enumerable.Range(0, live.Count).OrderBy(k => string.Join(",", cards[k].OrderBy(v => v))).ToList();
            var sortedKey = string.Join("|", order.Select(k => string.Join(",", cards[k].OrderBy(v => v)))) + "#" + string.Join(",", board);
            double[] eq;
            try { eq = EquityCache.GetOrAdd(sortedKey, _ => EquityCalculator.Equity(order.Select(k => cards[k]).ToList(), board)); }
            catch (ArgumentException) { continue; }   // cartas repetidas en un historial dañado: se omite el equity, el replayer sigue
            var dict = new Dictionary<string, double>();
            for (int k = 0; k < order.Count; k++) dict[live[order[k]].Player] = eq[k];

            var seats = s.Seats.Select(x => !x.Folded && x.Cards == null && h.ShownCards.TryGetValue(x.Player, out var c) ? x with { Cards = c } : x).ToList();
            result[i] = s with { Equity = dict, Seats = seats };
        }
        return result;
    }
}
