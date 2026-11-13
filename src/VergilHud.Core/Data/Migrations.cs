using Microsoft.Data.Sqlite;

namespace VergilHud.Core;

public interface IMigration
{
    int Version { get; }
    void Up(SqliteConnection c, SqliteTransaction tx);
}

public static class Migrator
{
    public static readonly IReadOnlyList<IMigration> All = new IMigration[] { new M001Initial(), new M002FactsSchema(), new M003Analysis(), new M004Notes(), new M005Study(), new M006Wellbeing(), new M007PlayMoney() };
    public static int Latest => All[^1].Version;

    public static int GetVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public static void Run(SqliteConnection c)
    {
        Exec(c, null, "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL)");
        bool hasRow;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM schema_version";
            hasRow = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        if (!hasRow)
        {
            // Una base de datos de la Fase 1 ya tiene la tabla hands pero no versión: equivale a v1.
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='hands'";
            int legacy = Convert.ToInt32(cmd.ExecuteScalar()) > 0 ? 1 : 0;
            Exec(c, null, $"INSERT INTO schema_version(version) VALUES({legacy})");
        }

        int current = GetVersion(c);
        foreach (var m in All.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var tx = c.BeginTransaction();
            m.Up(c, tx);
            Exec(c, tx, $"UPDATE schema_version SET version = {m.Version}");
            tx.Commit();
        }
    }

    internal static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Esquema de la Fase 1 (se conserva para poder migrar bases de datos antiguas).</summary>
sealed class M001Initial : IMigration
{
    public int Version => 1;

    public void Up(SqliteConnection c, SqliteTransaction tx) => Migrator.Exec(c, tx, @"
CREATE TABLE hands(
  hand_id TEXT PRIMARY KEY, site TEXT, table_name TEXT, game TEXT, is_tournament INTEGER,
  played_at TEXT, small_blind REAL, big_blind REAL, max_seats INTEGER,
  hero TEXT, hero_cards TEXT, board TEXT, raw TEXT);
CREATE TABLE player_hands(hand_id TEXT NOT NULL, player TEXT NOT NULL, seat INTEGER, position TEXT, played_at TEXT, PRIMARY KEY(hand_id, player));
CREATE INDEX ix_ph_player ON player_hands(player);
CREATE INDEX ix_hands_time ON hands(played_at);
CREATE TABLE files(path TEXT PRIMARY KEY, offset INTEGER NOT NULL);");
}

/// <summary>
/// Esquema v4 (notas): una autonota y una etiqueta de mano no se repiten, y una tabla clave/valor para recordar
/// con qué versión de las reglas se generaron las autonotas.
/// </summary>
sealed class M004Notes : IMigration
{
    public int Version => 4;

    public void Up(SqliteConnection c, SqliteTransaction tx)
    {
        // Por si una versión anterior dejó duplicados, se limpian antes de crear los índices únicos
        Migrator.Exec(c, tx, "DELETE FROM auto_notes WHERE id NOT IN (SELECT MIN(id) FROM auto_notes GROUP BY rule_id, hand_id, player)");
        Migrator.Exec(c, tx, "DELETE FROM hand_tags WHERE rowid NOT IN (SELECT MIN(rowid) FROM hand_tags GROUP BY hand_id, tag)");
        Migrator.Exec(c, tx, "CREATE UNIQUE INDEX IF NOT EXISTS ux_auto_notes ON auto_notes(rule_id, hand_id, player)");
        Migrator.Exec(c, tx, "CREATE INDEX IF NOT EXISTS ix_auto_notes_player ON auto_notes(player)");
        Migrator.Exec(c, tx, "CREATE UNIQUE INDEX IF NOT EXISTS ux_hand_tags ON hand_tags(hand_id, tag)");
        Migrator.Exec(c, tx, "CREATE INDEX IF NOT EXISTS ix_hand_tags_tag ON hand_tags(tag)");
        Migrator.Exec(c, tx, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)");
    }
}

/// <summary>
/// Esquema v3 (análisis): tabla de torneos, estado del cálculo de EV, índices para el héroe y valor por defecto de ev_net_bb.
/// </summary>
sealed class M003Analysis : IMigration
{
    public int Version => 3;

    public void Up(SqliteConnection c, SqliteTransaction tx)
    {
        Migrator.Exec(c, tx, "ALTER TABLE hands ADD COLUMN ev_state INTEGER NOT NULL DEFAULT 0");
        Migrator.Exec(c, tx, HandStore.TournamentsTableSql);
        foreach (var (_, sql) in HandStore.Indexes) Migrator.Exec(c, tx, sql);
        // Hasta que se calcule el EV de los all-in, el EV de cada mano es su resultado real.
        Migrator.Exec(c, tx, "UPDATE player_hand_facts SET ev_net_bb = net_bb WHERE ev_net_bb IS NULL");

        // Reconstruye los torneos a partir de las manos ya guardadas.
        var raws = new List<string>();
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT raw FROM hands WHERE tournament_id IS NOT NULL ORDER BY played_at, hand_id";
            using var r = read.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) raws.Add(r.GetString(0));
        }
        foreach (var raw in raws)
            if (PokerStarsParser.Parse(raw) is { } h) HandStore.UpsertTournament(c, tx, h);
    }
}

/// <summary>
/// Esquema v2: hechos atómicos por jugador y mano. Vuelve a parsear TODAS las manos desde hands.raw,
/// de modo que cada mejora del extractor se aplica subiendo la versión (DESIGN.md §5.1).
/// </summary>
sealed class M002FactsSchema : IMigration
{
    public int Version => 2;

    public void Up(SqliteConnection c, SqliteTransaction tx)
    {
        Migrator.Exec(c, tx, "ALTER TABLE hands RENAME TO hands_v1");
        Migrator.Exec(c, tx, "DROP TABLE IF EXISTS player_hands");
        Migrator.Exec(c, tx, "DROP INDEX IF EXISTS ix_hands_time");
        Migrator.Exec(c, tx, HandStore.OtherTablesSql);
        Migrator.Exec(c, tx, HandStore.FactsTableSql());
        Migrator.Exec(c, tx, HandStore.FactsTableSql(HandStore.PlayTable));

        using var read = c.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT raw FROM hands_v1 ORDER BY rowid";
        using var r = read.ExecuteReader();
        var batch = new List<(Hand, List<PlayerHandFacts>)>();
        while (r.Read())
        {
            if (r.IsDBNull(0) || PokerStarsParser.Parse(r.GetString(0)) is not { } hand) continue;
            batch.Add((hand, FactExtractor.Extract(hand)));
            if (batch.Count >= 500) { HandStore.Insert(c, tx, batch, upsertTournaments: false); batch.Clear(); }
        }
        if (batch.Count > 0) HandStore.Insert(c, tx, batch, upsertTournaments: false);
        r.Close();

        Migrator.Exec(c, tx, "DROP TABLE hands_v1");
    }
}

/// <summary>Esquema v5: cola de estudio con repaso espaciado (SM-2) y registro de repasos.</summary>
sealed class M005Study : IMigration
{
    public int Version => 5;

    public void Up(SqliteConnection c, SqliteTransaction tx)
    {
        // La tabla reservada en la v2 nunca se usó (otro formato de columnas): se sustituye.
        Migrator.Exec(c, tx, "DROP TABLE IF EXISTS review_cards");
        Migrator.Exec(c, tx, @"CREATE TABLE review_cards(
            hand_id TEXT PRIMARY KEY, source TEXT NOT NULL, category TEXT NOT NULL DEFAULT '', added_at TEXT NOT NULL,
            due TEXT NOT NULL, ease REAL NOT NULL DEFAULT 2.5, interval_days INTEGER NOT NULL DEFAULT 0, reps INTEGER NOT NULL DEFAULT 0,
            last_grade INTEGER, retired INTEGER NOT NULL DEFAULT 0)");
        Migrator.Exec(c, tx, "CREATE INDEX IF NOT EXISTS ix_review_due ON review_cards(retired, due)");
        Migrator.Exec(c, tx, @"CREATE TABLE IF NOT EXISTS study_log(
            id INTEGER PRIMARY KEY AUTOINCREMENT, hand_id TEXT NOT NULL, reviewed_at TEXT NOT NULL, grade INTEGER NOT NULL,
            category TEXT NOT NULL DEFAULT '', guess TEXT)");
    }
}

/// <summary>Esquema v6: movimientos de bankroll y diario de sesión.</summary>
sealed class M006Wellbeing : IMigration
{
    public int Version => 6;

    public void Up(SqliteConnection c, SqliteTransaction tx)
    {
        Migrator.Exec(c, tx, @"CREATE TABLE IF NOT EXISTS bankroll_tx(
            id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, kind TEXT NOT NULL, amount REAL NOT NULL, note TEXT NOT NULL DEFAULT '')");
        Migrator.Exec(c, tx, @"CREATE TABLE IF NOT EXISTS journal(
            id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, phase TEXT NOT NULL, mood INTEGER, fatigue INTEGER, note TEXT NOT NULL DEFAULT '')");
    }
}

/// <summary>
/// Esquema v7: el dinero ficticio se guarda aparte (tabla play_hand_facts) para no mezclarlo con el dinero real en ninguna estadística.
/// Mueve las manos de práctica que ya estaban mezcladas.
/// </summary>
sealed class M007PlayMoney : IMigration
{
    public int Version => 7;

    static bool Exists(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public void Up(SqliteConnection c, SqliteTransaction tx)
    {
        if (!Exists(c, tx, "SELECT COUNT(*) FROM pragma_table_info('hands') WHERE name = 'play_money'"))
            Migrator.Exec(c, tx, "ALTER TABLE hands ADD COLUMN play_money INTEGER NOT NULL DEFAULT 0");
        if (!Exists(c, tx, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{HandStore.PlayTable}'"))
            Migrator.Exec(c, tx, HandStore.FactsTableSql(HandStore.PlayTable));

        // Solo hace falta mirar la primera línea (la cabecera) de cada mano
        var play = new List<(string Id, string? Tournament)>();
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT hand_id, tournament_id, substr(raw, 1, instr(raw || char(10), char(10)) - 1) FROM hands WHERE play_money = 0";
            using var r = read.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(2) && PokerStarsParser.IsPlayMoneyHeader(r.GetString(2)))
                    play.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }
        if (play.Count == 0) return;

        var cols = "hand_id,player,seat," + string.Join(",", FactCatalog.Columns.Select(x => x.Name));
        foreach (var chunk in play.Chunk(400))
        {
            var ids = string.Join(",", chunk.Select(x => "'" + x.Id.Replace("'", "''") + "'"));
            Migrator.Exec(c, tx, $"INSERT OR IGNORE INTO {HandStore.PlayTable}({cols}) SELECT {cols} FROM player_hand_facts WHERE hand_id IN ({ids})");
            Migrator.Exec(c, tx, $"DELETE FROM player_hand_facts WHERE hand_id IN ({ids})");
            Migrator.Exec(c, tx, $"UPDATE hands SET play_money = 1 WHERE hand_id IN ({ids})");
        }
        foreach (var t in play.Where(x => x.Tournament != null).Select(x => x.Tournament!).Distinct().Chunk(400))
            Migrator.Exec(c, tx, $"DELETE FROM tournaments WHERE id IN ({string.Join(",", t.Select(x => "'" + x.Replace("'", "''") + "'"))})");
    }
}