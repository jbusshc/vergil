using Microsoft.Data.Sqlite;

namespace VergilHud.Core;

/// <summary>Base de datos SQLite local. Thread-safe mediante un lock; las migraciones se aplican al abrir.</summary>
public sealed class HudDatabase : IDisposable
{
    readonly SqliteConnection _conn;
    readonly object _lock = new();
    readonly FactInsertCache _factCache = new();

    /// <summary>Se dispara tras insertar manos nuevas, con los jugadores afectados (para invalidar cachés).</summary>
    public event Action<IReadOnlyCollection<string>>? PlayersChanged;

    /// <summary>Manos nuevas (con sus hechos) ya guardadas: de aquí salen las autonotas.</summary>
    public event Action<IReadOnlyList<(Hand Hand, List<PlayerHandFacts> Facts)>>? HandsInserted;

    public HudDatabase(string path)
    {
        if (path != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Migrator.Run(_conn);
        CreateSecondaryIndexes(); // por si una importación masiva se interrumpió con los índices quitados
    }

    /// <summary>Quita los índices secundarios (importación masiva). Hay que llamar a CreateSecondaryIndexes después.</summary>
    public void DropSecondaryIndexes()
    {
        lock (_lock)
            foreach (var (name, _) in HandStore.Indexes) Exec($"DROP INDEX IF EXISTS {name}");
    }

    public void CreateSecondaryIndexes()
    {
        lock (_lock)
            foreach (var (_, sql) in HandStore.Indexes) Exec(sql);
    }

    void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Ejecuta una consulta con acceso exclusivo a la conexión (lo usa StatEngine).</summary>
    public T Query<T>(Func<SqliteConnection, T> fn)
    {
        lock (_lock) return fn(_conn);
    }

    public int SchemaVersion => Query(Migrator.GetVersion);

    /// <summary>Inserta las manos (ignorando duplicadas). Devuelve las realmente nuevas.</summary>
    public List<Hand> InsertHands(IEnumerable<(Hand Hand, List<PlayerHandFacts> Facts)> items)
    {
        List<Hand> inserted;
        var players = new HashSet<string>();
        var list = items.ToList();
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            inserted = HandStore.Insert(_conn, tx, list, cache: _factCache);
            var ids = inserted.Select(h => h.Id).ToHashSet();
            foreach (var (h, f) in list)
                if (ids.Contains(h.Id)) foreach (var x in f) players.Add(x.Player);
            tx.Commit();
        }
        if (players.Count > 0) PlayersChanged?.Invoke(players);
        if (inserted.Count > 0 && HandsInserted != null)
        {
            var ids = inserted.Select(h => h.Id).ToHashSet();
            HandsInserted(list.Where(x => ids.Contains(x.Hand.Id)).ToList());
        }
        return inserted;
    }

    // ---------- estadísticas heredadas (la ventana principal y el HUD de la Fase 1) ----------
    static readonly string LegacySelect =
        "SELECT player, COUNT(*), SUM(pf_vpip), SUM(pf_pfr), SUM(pf_3bet_opp), SUM(pf_3bet), SUM(pf_f3b_opp), SUM(pf_f3b), " +
        "SUM(pf_steal_opp), SUM(pf_steal), SUM(pf_fts_opp), SUM(pf_fts), SUM(f_saw), SUM(f_cbet_opp), SUM(f_cbet), " +
        "SUM(f_fcb_opp), SUM(f_fcb), SUM(wtsd), SUM(wsd), " +
        "SUM(f_bets+f_raises+t_bets+t_raises+r_bets+r_raises), SUM(f_calls+t_calls+r_calls), SUM(f_folds+t_folds+r_folds), " +
        "COALESCE(SUM(net),0), COALESCE(SUM(net_bb),0) FROM player_hand_facts";

    static PlayerStats ReadLegacy(SqliteDataReader r)
    {
        int i = 0;
        int N() => r.GetInt32(i++);
        var s = new PlayerStats { Player = r.GetString(i++), Hands = N() };
        s.Vpip = N(); s.Pfr = N(); s.ThreeBetOpp = N(); s.ThreeBet = N(); s.FoldTo3BetOpp = N(); s.FoldTo3Bet = N();
        s.StealOpp = N(); s.Steal = N(); s.FoldToStealOpp = N(); s.FoldToSteal = N(); s.SawFlop = N();
        s.CbetOpp = N(); s.Cbet = N(); s.FoldToCbetOpp = N(); s.FoldToCbet = N(); s.Wtsd = N(); s.Wsd = N();
        s.PostflopBets = N(); s.PostflopCalls = N(); s.PostflopFolds = N();
        s.Net = r.GetDouble(i++); s.NetBb = r.GetDouble(i++);
        return s;
    }

    public PlayerStats GetPlayerStats(string player)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = LegacySelect + " WHERE player = $p GROUP BY player";
            cmd.Parameters.AddWithValue("$p", player);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadLegacy(r) : new PlayerStats { Player = player };
        }
    }

    public List<PlayerStats> GetAllPlayerStats(string? filter = null, int minHands = 1, int limit = 2000, IReadOnlyList<string>? formats = null, bool playMoney = false)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            string fmt = "";
            if (formats is { Count: > 0 })
            {
                for (int i = 0; i < formats.Count; i++) cmd.Parameters.AddWithValue("$fm" + i, formats[i]);
                fmt = $" AND format IN ({string.Join(",", formats.Select((_, i) => "$fm" + i))})";
            }
            // El dinero ficticio vive en otra tabla: nunca se mezcla con el real
            cmd.CommandText = LegacySelect.Replace("FROM player_hand_facts", playMoney ? "FROM play_hand_facts" : "FROM player_hand_facts") +
                              " WHERE player LIKE $f" + fmt + " GROUP BY player HAVING COUNT(*) >= $m ORDER BY COUNT(*) DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$f", "%" + (filter ?? "") + "%");
            cmd.Parameters.AddWithValue("$m", minHands);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<PlayerStats>();
            while (r.Read()) list.Add(ReadLegacy(r));
            return list;
        }
    }

    public long GetHandCount()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM hands";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>El héroe es el nombre que más veces aparece en "Dealt to".</summary>
    public string? GetHeroName()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT hero FROM hands WHERE hero IS NOT NULL GROUP BY hero ORDER BY COUNT(*) DESC LIMIT 1";
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>Una mano por su número, o null si no está guardada.</summary>
    public Hand? GetHand(string handId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT raw FROM hands WHERE hand_id = $id";
            cmd.Parameters.AddWithValue("$id", handId);
            return cmd.ExecuteScalar() is string raw ? PokerStarsParser.Parse(raw) : null;
        }
    }

    /// <summary>La mano más reciente (por fecha) guardada en la base de datos, o null si está vacía.</summary>
    public Hand? GetLatestHand()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT raw FROM hands ORDER BY played_at DESC, hand_id DESC LIMIT 1";
            return cmd.ExecuteScalar() is string raw ? PokerStarsParser.Parse(raw) : null;
        }
    }

    /// <summary>Todos los nombres con los que se ha jugado: los que aparecen en "Dealt to" (solo el dueño del historial).</summary>
    public List<string> GetHeroNames()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT hero FROM hands WHERE hero IS NOT NULL";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>
    /// Inicio de la sesión en curso: se recorre hacia atrás desde la última mano del héroe mientras no haya una pausa
    /// de más de <paramref name="gapMinutes"/> minutos. Null si no hay manos.
    /// </summary>
    public DateTime? GetCurrentSessionStart(int gapMinutes = 30)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT played_at FROM hands WHERE hero IS NOT NULL ORDER BY played_at DESC LIMIT 5000";
            using var r = cmd.ExecuteReader();
            DateTime? start = null;
            while (r.Read())
            {
                if (!DateTime.TryParse(r.GetString(0), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var t)) continue;
                if (start != null && (start.Value - t).TotalMinutes > gapMinutes) break;
                start = t;
            }
            return start;
        }
    }

    // ---------- importación ----------
    public Dictionary<string, long> GetFileOffsets()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT path, offset FROM files";
            using var r = cmd.ExecuteReader();
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
            return d;
        }
    }

    public void SetFileOffset(string path, long offset)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO files(path, offset) VALUES($p,$o) ON CONFLICT(path) DO UPDATE SET offset = excluded.offset";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$o", offset);
            cmd.ExecuteNonQuery();
        }
    }

    public void LogImportError(string path, long offset, string message, string? raw)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO import_errors(path, offset, message, raw, at) VALUES($p,$o,$m,$r,$t)";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$o", offset);
            cmd.Parameters.AddWithValue("$m", message);
            cmd.Parameters.AddWithValue("$r", raw is null ? DBNull.Value : raw.Length > 4000 ? raw[..4000] : raw);
            cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("s"));
            cmd.ExecuteNonQuery();
        }
    }

    public long GetImportErrorCount()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM import_errors";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _factCache.Clear();
            _conn.Dispose();
        }
    }
}
