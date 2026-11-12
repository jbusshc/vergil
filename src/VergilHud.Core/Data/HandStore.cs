using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VergilHud.Core;

/// <summary>Esquema v2 y escritura de manos + hechos. Compartido por HudDatabase y las migraciones.</summary>
internal static class HandStore
{
    public static string FactsTableSql()
    {
        var cols = string.Join(", ", FactCatalog.Columns.Select(c => c.Kind switch
        {
            FactKind.Int => $"{c.Name} INTEGER NOT NULL DEFAULT 0",
            FactKind.Real => $"{c.Name} REAL",
            _ => $"{c.Name} TEXT",
        }));
        return $"CREATE TABLE player_hand_facts(hand_id TEXT NOT NULL, player TEXT NOT NULL, seat INTEGER NOT NULL, {cols}, PRIMARY KEY(hand_id, player));" +
               "CREATE INDEX ix_facts_player_time ON player_hand_facts(player, played_at);" +
               "CREATE INDEX ix_facts_player_pos ON player_hand_facts(player, position);";
    }

    public const string OtherTablesSql = @"
CREATE TABLE hands(
  hand_id TEXT PRIMARY KEY, site TEXT, table_name TEXT, game TEXT, format TEXT, tournament_id TEXT NULL,
  buyin REAL NULL, level INTEGER, played_at TEXT, small_blind REAL, big_blind REAL, ante REAL,
  max_seats INTEGER, players_dealt INTEGER, hero TEXT NULL, hero_cards TEXT NULL, board TEXT,
  pot REAL, rake REAL, session_id INTEGER NULL, raw TEXT);
CREATE INDEX ix_hands_played_at ON hands(played_at);
CREATE INDEX ix_hands_tournament ON hands(tournament_id);
CREATE TABLE players(player TEXT PRIMARY KEY, first_seen TEXT, last_seen TEXT, hands INTEGER, color_label TEXT NULL, note TEXT NULL);
CREATE TABLE auto_notes(id INTEGER PRIMARY KEY AUTOINCREMENT, player TEXT, created_at TEXT, rule_id TEXT, text TEXT, hand_id TEXT);
CREATE TABLE sessions(id INTEGER PRIMARY KEY AUTOINCREMENT, started_at TEXT, ended_at TEXT, hands INTEGER, net REAL, net_bb REAL, ev_net_bb REAL, tables_max INTEGER, tilt_score REAL);
CREATE TABLE hand_tags(hand_id TEXT, tag TEXT, created_at TEXT);
CREATE TABLE review_cards(hand_id TEXT PRIMARY KEY, ease REAL, interval_days INTEGER, due_at TEXT, reps INTEGER, last_result TEXT);
CREATE TABLE allin_ev(hand_id TEXT, player TEXT, street TEXT, equity REAL, ev_net REAL);
CREATE TABLE custom_stats(id TEXT PRIMARY KEY, json TEXT);
CREATE TABLE import_errors(id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT, offset INTEGER, message TEXT, raw TEXT, at TEXT);";

    /// <summary>Índices secundarios. Se pueden quitar durante una importación masiva y recrear después.</summary>
    public static readonly (string Name, string Sql)[] Indexes =
    {
        ("ix_facts_player_time", "CREATE INDEX IF NOT EXISTS ix_facts_player_time ON player_hand_facts(player, played_at)"),
        ("ix_facts_player_pos", "CREATE INDEX IF NOT EXISTS ix_facts_player_pos ON player_hand_facts(player, position)"),
        // Solo filas del héroe y con las columnas que leen el Dashboard, los informes y la matriz: las consultas se resuelven sin
        // tocar la tabla (cada fila tiene más de 130 columnas). (played_at, hand_id) deja el orden ya hecho.
        ("ix_facts_hero_time", "CREATE INDEX IF NOT EXISTS ix_facts_hero_time ON player_hand_facts(played_at, hand_id, format, net_bb, ev_net_bb, " +
                               "wtsd, rake_paid, big_blind, position, pf_vpip, pf_pfr, stack_bucket, players_dealt, hole_cards, tournament_id) WHERE is_hero = 1"),
        ("ix_facts_tournament", "CREATE INDEX IF NOT EXISTS ix_facts_tournament ON player_hand_facts(tournament_id) WHERE tournament_id IS NOT NULL"),
        ("ix_hands_played_at", "CREATE INDEX IF NOT EXISTS ix_hands_played_at ON hands(played_at)"),
        ("ix_hands_tournament", "CREATE INDEX IF NOT EXISTS ix_hands_tournament ON hands(tournament_id)"),
    };

    public const string TournamentsTableSql = @"
CREATE TABLE IF NOT EXISTS tournaments(
  id TEXT PRIMARY KEY, format TEXT, buyin REAL, rake REAL, bounty REAL, entrants INTEGER NULL,
  started_at TEXT, finished_at TEXT, hands INTEGER NOT NULL, hero TEXT NULL,
  finish_position INTEGER NULL, prize REAL NULL, max_seats INTEGER);";

    const string TournamentUpsertSql = @"
INSERT INTO tournaments(id,format,buyin,rake,bounty,entrants,started_at,finished_at,hands,hero,finish_position,prize,max_seats)
VALUES($id,$fmt,$buyin,$rake,$bounty,$entr,$at,$at,1,$hero,$pos,$prize,$max)
ON CONFLICT(id) DO UPDATE SET
  started_at = MIN(started_at, excluded.started_at), finished_at = MAX(finished_at, excluded.finished_at),
  hands = hands + 1, hero = COALESCE(hero, excluded.hero),
  finish_position = COALESCE(excluded.finish_position, finish_position), prize = COALESCE(excluded.prize, prize)";

    /// <summary>Suma esta mano a su torneo (lo crea si hace falta). El puesto y el premio salen de las líneas de fin de torneo.</summary>
    public static void UpsertTournament(SqliteConnection c, SqliteTransaction tx, Hand h)
    {
        if (!h.IsTournament || h.TournamentId == null) return;
        var parts = h.BuyinParts;
        var res = h.Hero == null ? null : h.Results.FirstOrDefault(r => r.Player == h.Hero);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = TournamentUpsertSql;
        cmd.Parameters.AddWithValue("$id", h.TournamentId);
        cmd.Parameters.AddWithValue("$fmt", h.Format);
        cmd.Parameters.AddWithValue("$buyin", parts.Count > 0 ? (double)parts.Sum() : 0.0);
        cmd.Parameters.AddWithValue("$rake", parts.Count > 0 ? (double)parts[^1] : 0.0);
        cmd.Parameters.AddWithValue("$bounty", parts.Count == 3 ? (double)parts[1] : 0.0);
        cmd.Parameters.AddWithValue("$entr", h.Format == "spin" ? h.MaxSeats : DBNull.Value);
        cmd.Parameters.AddWithValue("$at", h.Time.ToString("s"));
        cmd.Parameters.AddWithValue("$hero", (object?)h.Hero ?? DBNull.Value);
        // "finished in Nth place" sin "and received" = fuera de premios (0); si no hay línea, el torneo sigue en curso (null).
        cmd.Parameters.AddWithValue("$pos", res != null ? res.Place : DBNull.Value);
        cmd.Parameters.AddWithValue("$prize", res != null ? (double)(res.Prize ?? 0m) : DBNull.Value);
        cmd.Parameters.AddWithValue("$max", h.MaxSeats);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserta manos y hechos ignorando duplicados. Devuelve solo las manos realmente nuevas.</summary>
    public static List<Hand> Insert(SqliteConnection c, SqliteTransaction tx, IEnumerable<(Hand Hand, List<PlayerHandFacts> Facts)> items,
        bool upsertTournaments = true, FactInsertCache? cache = null)
    {
        var inserted = new List<Hand>();
        var ownCache = cache == null;
        cache ??= new FactInsertCache();
        try
        {
            InsertCore(c, tx, items, upsertTournaments, cache, inserted);
        }
        finally
        {
            if (ownCache) cache.Clear();
        }
        return inserted;
    }

    static void InsertCore(SqliteConnection c, SqliteTransaction tx, IEnumerable<(Hand Hand, List<PlayerHandFacts> Facts)> items,
        bool upsertTournaments, FactInsertCache cache, List<Hand> inserted)
    {
        using var hc = c.CreateCommand();
        hc.Transaction = tx;
        hc.CommandText = @"INSERT OR IGNORE INTO hands(hand_id,site,table_name,game,format,tournament_id,buyin,level,played_at,small_blind,big_blind,ante,max_seats,players_dealt,hero,hero_cards,board,pot,rake,raw)
VALUES($id,$site,$tbl,$game,$fmt,$tid,$buyin,$level,$at,$sb,$bb,$ante,$max,$pd,$hero,$cards,$board,$pot,$rake,$raw)";
        var hp = new[] { "$id", "$site", "$tbl", "$game", "$fmt", "$tid", "$buyin", "$level", "$at", "$sb", "$bb", "$ante", "$max", "$pd", "$hero", "$cards", "$board", "$pot", "$rake", "$raw" }
            .Select(n => hc.Parameters.AddWithValue(n, DBNull.Value)).ToArray();
        hc.Prepare();

        foreach (var (h, facts) in items)
        {
            object?[] hv =
            {
                h.Id, h.Site, h.TableName, h.Game, h.Format, h.TournamentId, h.Buyin is { } b ? (double)b : null, h.Level,
                h.Time.ToString("s"), (double)h.SmallBlind, (double)h.BigBlind, (double)h.Ante, h.MaxSeats, facts.Count,
                h.Hero, h.HeroCards, string.Join(" ", h.Board), (double)h.Pot, (double)h.Rake, h.Raw,
            };
            for (int i = 0; i < hp.Length; i++) hp[i].Value = hv[i] ?? DBNull.Value;
            if (hc.ExecuteNonQuery() == 0) continue;
            if (upsertTournaments) UpsertTournament(c, tx, h);

            foreach (var f in facts) cache.Insert(c, tx, h.Id, f);
            inserted.Add(h);
        }
    }

}

/// <summary>
/// Inserta filas de hechos reutilizando sentencias ya preparadas. Una fila solo envía las columnas con valor (la mayoría valen 0 o
/// NULL, y esas columnas ya tienen DEFAULT 0 / NULL), así que lleva ~25-40 parámetros en lugar de 146: SQLite enlaza los
/// parámetros por nombre buscándolos uno a uno, un coste cuadrático que dominaba toda la importación. Las filas con las mismas
/// columnas comparten sentencia, que se prepara una sola vez.
/// </summary>
internal sealed class FactInsertCache
{
    sealed class Entry
    {
        public required SqliteCommand Cmd;
        public required SqliteParameter Hid, Player, Seat;
        public required SqliteParameter[] Vals;
    }

    const int MaxEntries = 4000;
    readonly Dictionary<string, Entry> _entries = new();
    readonly int[] _idx = new int[FactCatalog.Count];

    public void Insert(SqliteConnection c, SqliteTransaction tx, string handId, PlayerHandFacts f)
    {
        var cols = FactCatalog.Columns;
        int n = 0;
        for (int i = 0; i < cols.Count; i++)
        {
            var v = f.Values[i];
            bool has = cols[i].Kind switch
            {
                FactKind.Int => v is int k && k != 0,
                FactKind.Real => v != null && double.IsFinite(Convert.ToDouble(v)),
                _ => v is string,
            };
            if (has) _idx[n++] = i;
        }

        var key = new StringBuilder(n * 3);
        for (int k = 0; k < n; k++) key.Append(_idx[k]).Append(',');
        var ks = key.ToString();

        if (!_entries.TryGetValue(ks, out var e))
        {
            if (_entries.Count >= MaxEntries) Clear();
            var names = new StringBuilder("INSERT OR IGNORE INTO player_hand_facts(hand_id,player,seat", 400);
            var values = new StringBuilder("$h,$p,$s", 200);
            for (int k = 0; k < n; k++) { names.Append(',').Append(cols[_idx[k]].Name); values.Append(",$v").Append(k); }
            var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = names.Append(") VALUES(").Append(values).Append(')').ToString();
            e = new Entry
            {
                Cmd = cmd,
                Hid = cmd.Parameters.AddWithValue("$h", DBNull.Value),
                Player = cmd.Parameters.AddWithValue("$p", DBNull.Value),
                Seat = cmd.Parameters.AddWithValue("$s", DBNull.Value),
                Vals = Enumerable.Range(0, n).Select(k => cmd.Parameters.AddWithValue("$v" + k, DBNull.Value)).ToArray(),
            };
            cmd.Prepare();
            _entries[ks] = e;
        }

        e.Cmd.Transaction = tx;
        e.Hid.Value = handId;
        e.Player.Value = f.Player;
        e.Seat.Value = f.Seat;
        for (int k = 0; k < n; k++)
        {
            var v = f.Values[_idx[k]];
            e.Vals[k].Value = cols[_idx[k]].Kind == FactKind.Real ? Convert.ToDouble(v) : v;
        }
        e.Cmd.ExecuteNonQuery();
    }

    public void Clear()
    {
        foreach (var e in _entries.Values) e.Cmd.Dispose();
        _entries.Clear();
    }
}
