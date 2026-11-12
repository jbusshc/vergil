namespace VergilHud.Core;

/// <summary>
/// Convierte una mano parseada en una fila de hechos por jugador (ver FactCatalog).
/// Las definiciones de cada hecho están documentadas en DESIGN.md §6.1 y docs/FACTS.md.
/// </summary>
public static class FactExtractor
{
    public static List<PlayerHandFacts> Extract(Hand hand) => new Run(hand).Execute();

    /// <summary>Ratio apuesta/bote → "small" (&lt;40%), "medium" (40-80%), "large" (80-120%), "over" (&gt;120%).</summary>
    public static string SizeBucket(double ratio) =>
        ratio < 0.40 ? "small" : ratio < 0.80 ? "medium" : ratio <= 1.20 ? "large" : "over";

    public static string StackBucket(double stackBb) =>
        stackBb < 10 ? "<10" : stackBb < 20 ? "10-20" : stackBb < 40 ? "20-40" : "40+";

    sealed class Run
    {
        readonly Hand _h;
        readonly List<Seat> _active;
        readonly Dictionary<string, PlayerHandFacts> _f;
        readonly Dictionary<string, string> _pos;
        readonly IReadOnlyList<string> _order;
        readonly decimal _bb;
        readonly HashSet<string> _inHand;
        readonly Dictionary<string, decimal> _invested = new();
        readonly Dictionary<string, decimal> _streetInv = new();
        readonly HashSet<(string, string)> _once = new();
        /// <summary>Jugadores sin fichas: ya no pueden actuar, así que no cuentan como "agresor que aún no ha actuado".</summary>
        readonly HashSet<string> _allIn = new();
        decimal _potBase;

        public Run(Hand h)
        {
            _h = h;
            var participants = new HashSet<string>(h.Actions.Select(a => a.Player));
            if (h.Hero != null) participants.Add(h.Hero);
            // Un jugador "sitting out" que pone ciegas (desconectado en torneo) sí juega la mano.
            _active = h.Seats.Where(s => participants.Contains(s.Player)).OrderBy(s => s.Number).ToList();
            _f = _active.ToDictionary(s => s.Player, s => new PlayerHandFacts(s.Player, s.Number));
            _pos = Positions.Assign(h, _active);
            _order = Positions.PostflopOrder(h, _active);
            _bb = h.BigBlind > 0 ? h.BigBlind : 1;
            _inHand = new HashSet<string>(_f.Keys);
        }

        // ---------- utilidades ----------
        void Flag(string p, string fact) => _f[p].SetFlag(fact);
        void Inc(string p, string fact) => _f[p].Increment(fact);
        void Set(string p, string fact, object? v) => _f[p][fact] = v is decimal d ? (double)d : v;
        static void Add(Dictionary<string, decimal> d, string k, decimal v) => d[k] = d.GetValueOrDefault(k) + v;

        /// <summary>Marca "oportunidad" y, si procede, "acción", una sola vez por jugador y estadística.</summary>
        void Opp(string p, string stat, bool hit)
        {
            if (!_once.Add((p, stat))) return;
            Flag(p, stat + "_opp");
            if (hit) Flag(p, stat);
        }

        bool Once(string p, string key) => _once.Add((p, key));

        /// <summary>Aplica la acción al estado de dinero. Devuelve el bote y lo que había que igualar ANTES de actuar.</summary>
        void Apply(HandAction a, out decimal potBefore, out decimal toCall)
        {
            var p = a.Player;
            _streetInv.TryGetValue(p, out var mine);
            var cur = _streetInv.Count == 0 ? 0m : _streetInv.Values.Max();
            toCall = Math.Max(0m, cur - mine);
            potBefore = _potBase + _streetInv.Values.Sum();
            switch (a.Kind)
            {
                case ActionKind.PostAnte:
                    _potBase += a.Amount;
                    Add(_invested, p, a.Amount);
                    break;
                case ActionKind.PostSmallBlind:
                case ActionKind.PostBigBlind:
                case ActionKind.Call:
                case ActionKind.Bet:
                    _streetInv[p] = mine + a.Amount;
                    Add(_invested, p, a.Amount);
                    break;
                case ActionKind.PostDead:
                {
                    var live = Math.Min(a.Amount, _bb); // SB muerta + BB viva
                    _streetInv[p] = mine + live;
                    _potBase += a.Amount - live;
                    Add(_invested, p, a.Amount);
                    break;
                }
                case ActionKind.Raise:
                    Add(_invested, p, a.ToAmount - mine);
                    _streetInv[p] = a.ToAmount;
                    break;
            }
        }

        // ---------- ejecución ----------
        public List<PlayerHandFacts> Execute()
        {
            if (_f.Count == 0) return new();
            Context();
            var byStreet = _h.Actions.ToLookup(a => a.Street);
            var pfAggr = Preflop(byStreet[Street.Preflop].ToList());
            var prev = pfAggr;
            foreach (var st in new[] { Street.Flop, Street.Turn, Street.River })
                prev = Postflop(st, byStreet[st].ToList(), prev, pfAggr);
            Outcome();
            return _active.Select(s => _f[s.Player]).ToList();
        }

        void Context()
        {
            foreach (var s in _active)
            {
                var p = s.Player;
                Set(p, "position", _pos.GetValueOrDefault(p));
                Set(p, "format", _h.Format);
                Set(p, "tournament_id", _h.TournamentId);
                Set(p, "played_at", _h.Time.ToString("s"));
                Set(p, "players_dealt", _active.Count);
                Set(p, "is_hero", p == _h.Hero ? 1 : 0);
                Set(p, "big_blind", _bb);
                var stackBb = (double)(s.Stack / _bb);
                Set(p, "stack_bb", stackBb);
                Set(p, "stack_bucket", StackBucket(stackBb));
                var maxOther = _active.Where(o => o.Player != p).Select(o => o.Stack).DefaultIfEmpty(s.Stack).Max();
                Set(p, "eff_stack_bb", (double)(Math.Min(s.Stack, maxOther) / _bb));
                var denom = _h.SmallBlind + _h.BigBlind + _h.Ante * _active.Count;
                if (denom > 0) Set(p, "m_ratio", (double)(s.Stack / denom));
                if (p == _h.Hero) Set(p, "hole_cards", _h.HeroCards);
                else if (_h.ShownCards.TryGetValue(p, out var cards)) Set(p, "hole_cards", cards);
            }
        }

        // ---------- preflop ----------
        string? Preflop(List<HandAction> acts)
        {
            int raises = 0, limpers = 0, callers = 0;
            var raisers = new List<string>();
            string? opener = null;
            bool openWasSteal = false, lastRaiseAllIn = false;
            var acted = new HashSet<string>();
            var limped = new HashSet<string>();
            var postedBig = acts.Where(a => a.Kind is ActionKind.PostBigBlind or ActionKind.PostDead).Select(a => a.Player).ToHashSet();
            var blinds = acts.Where(a => a.Kind is ActionKind.PostSmallBlind or ActionKind.PostBigBlind or ActionKind.PostDead)
                             .Select(a => a.Player).ToHashSet();

            foreach (var a in acts)
            {
                var p = a.Player;
                if (!_f.ContainsKey(p)) continue;
                Apply(a, out _, out _);
                if (a.IsPost) continue;

                var ps = _pos.GetValueOrDefault(p, "");
                bool first = acted.Add(p);
                bool raise = a.Kind == ActionKind.Raise, call = a.Kind == ActionKind.Call, fold = a.Kind == ActionKind.Fold;

                // Primer en entrar al bote (open / steal / shove)
                if (first && raises == 0 && limpers == 0 && !postedBig.Contains(p))
                {
                    Flag(p, "pf_open_opp");
                    if (raise) Flag(p, "pf_open");
                    Flag(p, "pf_shove_opp");
                    if (raise && a.AllIn) Flag(p, "pf_shove");
                    if (ps is "CO" or "BTN" or "SB")
                    {
                        Flag(p, "pf_steal_opp");
                        if (raise) Flag(p, "pf_steal");
                    }
                }

                // Cold call (sin dinero previo salvo ciegas)
                if (first && raises >= 1 && !blinds.Contains(p))
                {
                    Flag(p, "pf_cold_call_opp");
                    if (call) Flag(p, "pf_cold_call");
                }

                // 3-bet / reshove / resteal / squeeze (primera vez que se enfrenta a una única subida)
                if (raises == 1 && p != opener && Once(p, "pf_3bet"))
                {
                    Flag(p, "pf_3bet_opp");
                    if (raise) Flag(p, "pf_3bet");
                    Flag(p, "pf_reshove_opp");
                    if (raise && a.AllIn) Flag(p, "pf_reshove");
                    if (openWasSteal)
                    {
                        if (raise) Flag(p, "pf_resteal");
                        if (ps is "SB" or "BB")
                        {
                            Flag(p, "pf_3bet_vs_steal_opp");
                            if (raise) Flag(p, "pf_3bet_vs_steal");
                        }
                    }
                    if (callers >= 1)
                    {
                        Flag(p, "pf_squeeze_opp");
                        if (raise) Flag(p, "pf_squeeze");
                    }
                }

                // El que abrió se enfrenta a un 3-bet: fold / 4-bet
                if (raises == 2 && p == opener && Once(p, "pf_f3b"))
                {
                    Flag(p, "pf_f3b_opp");
                    if (fold) Flag(p, "pf_f3b");
                    Flag(p, "pf_4bet_opp");
                    if (raise) Flag(p, "pf_4bet");
                }
                // El 3-beteador se enfrenta a un 4-bet
                if (raises == 3 && raisers.Count >= 2 && p == raisers[1] && Once(p, "pf_f4b"))
                {
                    Flag(p, "pf_f4b_opp");
                    if (fold) Flag(p, "pf_f4b");
                }
                if (raise && raises >= 3) Flag(p, "pf_5bet");

                // Fold a robo de ciegas (nadie ha pagado todavía)
                if (first && raises == 1 && openWasSteal && callers == 0 && ps is "SB" or "BB")
                {
                    Flag(p, "pf_fts_opp");
                    if (fold) Flag(p, "pf_fts");
                }

                // Defensa de BB, SB completa, limps
                if (ps == "BB" && raises == 1 && Once(p, "pf_bb_defend"))
                {
                    Flag(p, "pf_bb_defend_opp");
                    if (call || raise) Flag(p, "pf_bb_defend");
                }
                if (ps == "SB" && call && raises == 0) Flag(p, "pf_sb_complete");
                if (call && raises == 0) { Flag(p, "pf_limp"); limped.Add(p); }
                if (limped.Contains(p) && raises >= 1)
                {
                    if (call) Flag(p, "pf_limp_call");
                    if (raise) Flag(p, "pf_limp_raise");
                }

                // Pagar un all-in
                if (raises >= 1 && lastRaiseAllIn && p != raisers[^1] && Once(p, "pf_call_shove"))
                {
                    Flag(p, "pf_call_shove_opp");
                    if (call) Flag(p, "pf_call_shove");
                }

                if (call || raise) Flag(p, "pf_vpip");
                if (raise) Flag(p, "pf_pfr");
                if (a.AllIn) { Flag(p, "pf_allin"); Flag(p, "went_allin"); _allIn.Add(p); }
                if (fold) _inHand.Remove(p);

                if (call)
                {
                    if (raises == 0) limpers++; else callers++;
                }
                if (raise)
                {
                    raises++;
                    raisers.Add(p);
                    callers = 0;
                    lastRaiseAllIn = a.AllIn;
                    if (raises == 1)
                    {
                        opener = p;
                        openWasSteal = _f[p].Flag("pf_steal");
                        Set(p, "pf_open_size_bb", a.ToAmount / _bb);
                    }
                }
            }

            foreach (var f in _f.Values)
            {
                if (raises >= 1) f.SetFlag("pf_raised_pot");
                if (raises >= 2) f.SetFlag("pf_3bet_pot");
                if (raises >= 3) f.SetFlag("pf_4bet_pot");
                if (raises == 0 && limpers > 0) f.SetFlag("pf_limped_pot");
            }
            return raisers.LastOrDefault();
        }

        // ---------- flop / turn / river ----------
        /// <summary>Procesa una calle. Devuelve el último que apostó o subió (agresor para la calle siguiente).</summary>
        string? Postflop(Street st, List<HandAction> acts, string? prevAggr, string? pfAggr)
        {
            string px = st == Street.Flop ? "f" : st == Street.Turn ? "t" : "r";
            int need = st == Street.Flop ? 3 : st == Street.Turn ? 4 : 5;
            _potBase += _streetInv.Values.Sum();
            _streetInv.Clear();
            if (_h.Board.Count < need) return null;

            var activeStart = _order.Where(_inHand.Contains).ToList();
            foreach (var p in activeStart)
            {
                Flag(p, px + "_saw");
                if (activeStart.Count >= 3) Flag(p, px + "_multiway");
            }
            if (activeStart.Count > 0) Flag(activeStart[^1], px + "_ip");

            bool betSeen = false, aggrActed = false, aggrChecked = false, cbetMade = false, raiseAfterCbet = false, pfAggrActed = false;
            string? bettor = null;
            var checkedSet = new HashSet<string>();
            var firstBet = new HashSet<string>();
            var faced = new HashSet<string>();

            foreach (var a in acts)
            {
                var p = a.Player;
                if (!_f.ContainsKey(p)) continue;
                Apply(a, out var potBefore, out var toCall);
                bool isAggr = p == prevAggr;
                bool bet = a.Kind == ActionKind.Bet, raise = a.Kind == ActionKind.Raise, fold = a.Kind == ActionKind.Fold;

                if (!betSeen)
                {
                    if (isAggr) Opp(p, px + "_cbet", bet);
                    else if (prevAggr != null && _inHand.Contains(prevAggr) && !_allIn.Contains(prevAggr))
                    {
                        if (!aggrActed) Opp(p, px + "_donk", bet);
                        else if (aggrChecked) Opp(p, px + "_float_bet", bet);
                    }
                    if (st != Street.Flop && prevAggr == null && pfAggr != null && p != pfAggr && _inHand.Contains(pfAggr)
                        && !_allIn.Contains(pfAggr) && !pfAggrActed)
                        Opp(p, px + "_probe", bet);
                }
                else
                {
                    if (cbetMade && !raiseAfterCbet && !isAggr)
                    {
                        Opp(p, px + "_fcb", fold);
                        Opp(p, px + "_raise_cb", raise);
                    }
                    if (checkedSet.Contains(p)) Opp(p, px + "_check_raise", raise);
                    if (toCall > 0 && potBefore - toCall > 0 && faced.Add(p))
                        Set(p, px + "_faced_size_bucket", SizeBucket((double)(toCall / (potBefore - toCall))));
                }

                switch (a.Kind)
                {
                    case ActionKind.Bet:
                        Inc(p, px + "_bets");
                        if (firstBet.Add(p) && potBefore > 0)
                        {
                            var ratio = (double)(a.Amount / potBefore);
                            Set(p, px + "_bet_size_pct", ratio * 100);
                            Set(p, px + "_bet_size_bucket", SizeBucket(ratio));
                            if (st == Street.Flop && isAggr && !betSeen) Set(p, "f_cbet_size_pct", ratio * 100);
                        }
                        break;
                    case ActionKind.Raise: Inc(p, px + "_raises"); break;
                    case ActionKind.Call: Inc(p, px + "_calls"); break;
                    case ActionKind.Check: Inc(p, px + "_checks"); checkedSet.Add(p); break;
                    case ActionKind.Fold: Inc(p, px + "_folds"); _inHand.Remove(p); break;
                }
                if (a.AllIn) { Flag(p, "went_allin"); _allIn.Add(p); }

                if (isAggr)
                {
                    aggrActed = true;
                    if (a.Kind == ActionKind.Check) aggrChecked = true;
                }
                if (pfAggr != null && p == pfAggr) pfAggrActed = true;
                if (bet || raise)
                {
                    if (bet && isAggr && !betSeen) cbetMade = true;
                    else if (raise && cbetMade) raiseAfterCbet = true;
                    betSeen = true;
                    bettor = p;
                }
            }

            foreach (var p in activeStart)
                if (_f[p].Flag(px + "_cbet_opp"))
                    Flag(p, _f[p].Flag(px + "_ip") ? px + "_cbet_ip" : px + "_cbet_oop");
            return bettor;
        }

        // ---------- resultado ----------
        void Outcome()
        {
            var totalCollected = _h.Collected.Values.Sum();
            foreach (var p in _f.Keys)
            {
                var collected = _h.Collected.GetValueOrDefault(p);
                var net = collected + _h.Returned.GetValueOrDefault(p) - _invested.GetValueOrDefault(p);
                Set(p, "net", net);
                Set(p, "net_bb", Math.Round((double)(net / _bb), 4));
                // Por defecto el EV es el resultado real; el servicio de EV de all-in lo sustituye cuando puede calcularlo.
                Set(p, "ev_net_bb", Math.Round((double)(net / _bb), 4));
                if (_h.Rake > 0 && totalCollected > 0 && collected > 0)
                    Set(p, "rake_paid", _h.Rake * collected / totalCollected);

                bool sawFlop = _f[p].Flag("f_saw");
                if (sawFlop && collected > 0) Flag(p, "wwsf");
                if (sawFlop && _h.HasShowdown && _inHand.Contains(p))
                {
                    Flag(p, "wtsd");
                    if (collected > 0) Flag(p, "wsd");
                }
            }
        }
    }
}
