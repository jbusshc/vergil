namespace VergilHud.Core;

public enum FactKind { Int, Real, Text }

public sealed record FactColumn(string Name, FactKind Kind);

/// <summary>
/// Lista blanca de los hechos atómicos por jugador y mano. Es la única fuente de verdad: las columnas de la tabla
/// player_hand_facts, el extractor y el lenguaje de estadísticas salen de aquí.
/// </summary>
public static class FactCatalog
{
    public static IReadOnlyList<FactColumn> Columns { get; }
    static readonly Dictionary<string, int> Index;

    static FactCatalog()
    {
        var l = new List<FactColumn>();
        void I(params string[] n) => l.AddRange(n.Select(x => new FactColumn(x, FactKind.Int)));
        void R(params string[] n) => l.AddRange(n.Select(x => new FactColumn(x, FactKind.Real)));
        void T(params string[] n) => l.AddRange(n.Select(x => new FactColumn(x, FactKind.Text)));

        // Contexto (mano y jugador)
        T("position", "format", "tournament_id", "played_at", "stack_bucket", "hole_cards", "hero_made_hand");
        I("players_dealt", "is_hero");
        R("big_blind", "stack_bb", "eff_stack_bb", "m_ratio");

        // Preflop
        I("pf_vpip", "pf_pfr", "pf_limp", "pf_limp_call", "pf_limp_raise",
          "pf_open_opp", "pf_open", "pf_cold_call_opp", "pf_cold_call",
          "pf_3bet_opp", "pf_3bet", "pf_3bet_vs_steal_opp", "pf_3bet_vs_steal", "pf_resteal",
          "pf_squeeze_opp", "pf_squeeze", "pf_f3b_opp", "pf_f3b", "pf_4bet_opp", "pf_4bet",
          "pf_f4b_opp", "pf_f4b", "pf_5bet", "pf_steal_opp", "pf_steal", "pf_fts_opp", "pf_fts",
          "pf_bb_defend_opp", "pf_bb_defend", "pf_sb_complete", "pf_allin",
          "pf_raised_pot", "pf_3bet_pot", "pf_4bet_pot", "pf_limped_pot",
          // Torneo / stacks cortos
          "pf_shove_opp", "pf_shove", "pf_call_shove_opp", "pf_call_shove", "pf_reshove_opp", "pf_reshove", "is_bubble");
        R("pf_open_size_bb");

        // Flop / turn / river
        foreach (var x in new[] { "f", "t", "r" })
        {
            I(x + "_saw", x + "_multiway", x + "_ip",
              x + "_cbet_opp", x + "_cbet", x + "_cbet_ip", x + "_cbet_oop",
              x + "_fcb_opp", x + "_fcb", x + "_raise_cb_opp", x + "_raise_cb",
              x + "_donk_opp", x + "_donk", x + "_float_bet_opp", x + "_float_bet",
              x + "_check_raise_opp", x + "_check_raise",
              x + "_bets", x + "_raises", x + "_calls", x + "_checks", x + "_folds");
            if (x != "f") I(x + "_probe_opp", x + "_probe");
            T(x + "_bet_size_bucket", x + "_faced_size_bucket");
            R(x + "_bet_size_pct");
        }
        R("f_cbet_size_pct");

        // Resultado
        I("wtsd", "wsd", "wwsf", "went_allin");
        R("net", "net_bb", "ev_net_bb", "rake_paid");

        Columns = l;
        Index = l.Select((c, i) => (c.Name, i)).ToDictionary(t => t.Name, t => t.i);
    }

    public static int Count => Columns.Count;
    public static bool Contains(string name) => Index.ContainsKey(name);

    public static int IndexOf(string name) =>
        Index.TryGetValue(name, out var i) ? i : throw new ArgumentException($"Hecho desconocido '{name}'");

    public static FactColumn Get(string name) => Columns[IndexOf(name)];
}
