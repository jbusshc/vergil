using Microsoft.Data.Sqlite;

namespace VergilHud.Core;

/// <param name="Equity">Cuota esperada del bote total (0..1).</param>
/// <param name="EvNet">Resultado esperado de la mano (en las unidades de la mano, no en bb).</param>
/// <param name="Net">Resultado real.</param>
public sealed record AllInResult(string Player, string Street, double Equity, double EvNet, double Net);

/// <summary>
/// EV de los all-in (DESIGN.md §6.5): cuando la mano acaba con todos all-in antes del river y se ven las cartas de todos los
/// que siguen, se calcula el equity en el momento del último all-in y se reparte cada capa del bote (side pots incluidos).
/// La diferencia con el resultado real es la suerte.
/// </summary>
public static class AllInEv
{
    public static Dictionary<string, AllInResult>? Compute(Hand h, IReadOnlyList<PlayerHandFacts> facts)
    {
        if (!h.HasShowdown || facts.Count < 2) return null;

        // Última acción all-in (la que cierra la apuesta)
        int lastIdx = -1;
        for (int i = 0; i < h.Actions.Count; i++)
            if (h.Actions[i].AllIn && !h.Actions[i].IsPost) lastIdx = i;
        if (lastIdx < 0) return null;
        var street = h.Actions[lastIdx].Street;
        if (street == Street.River) return null;   // en el river no hay nada por salir

        // Tras el último all-in solo pueden quedar respuestas (call/fold) en la misma calle: sin más apuestas en calles posteriores.
        for (int i = lastIdx + 1; i < h.Actions.Count; i++)
        {
            var a = h.Actions[i];
            if (a.IsPost) continue;
            if (a.Street != street || a.Kind is not (ActionKind.Call or ActionKind.Fold)) return null;
        }

        var players = facts.Select(f => f.Player).ToHashSet();
        var folded = h.Actions.Where(a => a.Kind == ActionKind.Fold).Select(a => a.Player).ToHashSet();
        var live = players.Where(p => !folded.Contains(p)).ToList();
        if (live.Count < 2) return null;

        int boardCount = street switch { Street.Preflop => 0, Street.Flop => 3, _ => 4 };
        if (h.Board.Count < boardCount) return null;
        int[] board;
        var holes = new Dictionary<string, int[]>();
        try
        {
            board = h.Board.Take(boardCount).Select(Cards.Parse).ToArray();
            foreach (var p in live)
            {
                var text = p == h.Hero ? h.HeroCards : h.ShownCards.GetValueOrDefault(p);
                var cards = Cards.ParseMany(text);
                if (cards.Length != 2) return null;   // cartas desconocidas (o no es Hold'em): no hay EV
                holes[p] = cards;
            }
        }
        catch (FormatException) { return null; }

        // Aportación de cada jugador al bote (sin la parte no igualada, que se le devolvió): cobrado − neto.
        var factOf = facts.ToDictionary(f => f.Player);
        var contrib = players.ToDictionary(p => p, p => (double)h.Collected.GetValueOrDefault(p) - (factOf[p].Real("net") ?? 0));
        double total = contrib.Values.Sum();
        double collectedTotal = (double)h.Collected.Values.Sum();
        if (total <= 0) return null;
        double rakeFactor = Math.Clamp(collectedTotal / total, 0, 1);

        var levels = contrib.Values.Where(v => v > 1e-9).Select(v => Math.Round(v, 6)).Distinct().OrderBy(v => v).ToList();
        var evCollected = live.ToDictionary(p => p, _ => 0.0);
        var equityCache = new Dictionary<string, double[]>();
        double prev = 0;
        foreach (var level in levels)
        {
            double layerAmount = (level - prev) * contrib.Count(kv => kv.Value >= level - 1e-9);
            prev = level;
            var eligible = live.Where(p => contrib[p] >= level - 1e-9).OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (eligible.Count == 0) continue;   // nadie vivo en esta capa (no debería pasar)
            double[] eq;
            if (eligible.Count == 1) eq = new[] { 1.0 };
            else
            {
                var key = string.Join("|", eligible);
                if (!equityCache.TryGetValue(key, out eq!))
                    equityCache[key] = eq = EquityCalculator.Equity(eligible.Select(p => holes[p]).ToList(), board);
            }
            for (int i = 0; i < eligible.Count; i++) evCollected[eligible[i]] += eq[i] * layerAmount * rakeFactor;
        }

        var result = new Dictionary<string, AllInResult>();
        foreach (var p in live)
        {
            double net = factOf[p].Real("net") ?? 0;
            result[p] = new AllInResult(p, street.ToString().ToLowerInvariant(),
                collectedTotal > 0 ? evCollected[p] / collectedTotal : 0, evCollected[p] - contrib[p], net);
        }
        return result;
    }
}

/// <summary>
/// Calcula en segundo plano el EV de las manos pendientes y lo guarda (ev_net_bb y tabla allin_ev).
/// Nunca va en el camino crítico del HUD.
/// </summary>
public static class AllInEvService
{
    /// <summary>Procesa hasta <paramref name="batch"/> manos pendientes. Devuelve cuántas ha examinado (0 = no queda nada).</summary>
    public static int ProcessPending(HudDatabase db, int batch = 200)
    {
        // Sin all-in con showdown no hay nada que calcular: se marcan resueltas de golpe.
        db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE hands SET ev_state = 1 WHERE ev_state = 0 AND NOT (" +
                              "EXISTS(SELECT 1 FROM player_hand_facts f WHERE f.hand_id = hands.hand_id AND f.went_allin = 1) AND " +
                              "EXISTS(SELECT 1 FROM player_hand_facts f WHERE f.hand_id = hands.hand_id AND f.wtsd = 1))";
            return cmd.ExecuteNonQuery();
        });

        var rows = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hand_id, raw FROM hands WHERE ev_state = 0 LIMIT $n";
            cmd.Parameters.AddWithValue("$n", batch);
            using var r = cmd.ExecuteReader();
            var l = new List<(string, string)>();
            while (r.Read()) l.Add((r.GetString(0), r.GetString(1)));
            return l;
        });
        if (rows.Count == 0) return 0;

        // El equity (lo caro) se calcula fuera del bloqueo de la base de datos.
        var computed = new List<(string HandId, decimal Bb, Dictionary<string, AllInResult>? Result)>();
        foreach (var (id, raw) in rows)
        {
            var hand = PokerStarsParser.Parse(raw);
            if (hand == null) { computed.Add((id, 1, null)); continue; }
            computed.Add((id, hand.BigBlind > 0 ? hand.BigBlind : 1, AllInEv.Compute(hand, FactExtractor.Extract(hand))));
        }

        db.Query(conn =>
        {
            using var tx = conn.BeginTransaction();
            foreach (var (id, bb, res) in computed)
            {
                if (res != null)
                {
                    foreach (var r in res.Values)
                    {
                        Exec(conn, tx, "UPDATE player_hand_facts SET ev_net_bb = $v WHERE hand_id = $h AND player = $p",
                            ("$v", Math.Round(r.EvNet / (double)bb, 4)), ("$h", id), ("$p", r.Player));
                        Exec(conn, tx, "DELETE FROM allin_ev WHERE hand_id = $h AND player = $p", ("$h", id), ("$p", r.Player));
                        Exec(conn, tx, "INSERT INTO allin_ev(hand_id, player, street, equity, ev_net) VALUES($h,$p,$s,$e,$n)",
                            ("$h", id), ("$p", r.Player), ("$s", r.Street), ("$e", r.Equity), ("$n", r.EvNet));
                    }
                }
                Exec(conn, tx, "UPDATE hands SET ev_state = 1 WHERE hand_id = $h", ("$h", id));
            }
            tx.Commit();
            return 0;
        });
        return rows.Count;
    }

    /// <summary>Procesa todo lo pendiente. Devuelve cuántas manos tenían EV calculable.</summary>
    public static int ProcessAll(HudDatabase db, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && ProcessPending(db) > 0) { }
        return db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(DISTINCT hand_id) FROM allin_ev";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
    }

    static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }
}
