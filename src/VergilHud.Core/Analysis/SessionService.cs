namespace VergilHud.Core;

public sealed record SessionRow(long Id, DateTime Start, DateTime End, int Hands, double NetBb, double EvBb, int TablesMax)
{
    public TimeSpan Duration => End - Start;
    public double BbPer100 => Hands == 0 ? 0 : NetBb * 100.0 / Hands;
    public double HandsPerHour => Duration.TotalHours < 1.0 / 60 ? Hands : Hands / Duration.TotalHours;
}

/// <summary>Detecta las sesiones del héroe: una pausa de más de 30 minutos entre manos abre una sesión nueva (DESIGN.md §8.5).</summary>
public static class SessionService
{
    public const int GapMinutes = 30;
    const int TableWindowMinutes = 5;

    /// <summary>Número máximo de mesas distintas con una mano en cualquier ventana de 5 minutos.</summary>
    static int MaxTables(List<(DateTime T, string Table)> hands, int from, int to)
    {
        var window = new Queue<(DateTime T, string Table)>();
        var counts = new Dictionary<string, int>();
        int max = 0;
        for (int i = from; i < to; i++)
        {
            var h = hands[i];
            window.Enqueue(h);
            counts[h.Table] = counts.GetValueOrDefault(h.Table) + 1;
            while ((h.T - window.Peek().T).TotalMinutes > TableWindowMinutes)
            {
                var old = window.Dequeue();
                if (--counts[old.Table] == 0) counts.Remove(old.Table);
            }
            max = Math.Max(max, counts.Count);
        }
        return max;
    }

    /// <summary>Recalcula todas las sesiones a partir de las manos del héroe. Devuelve cuántas hay.</summary>
    public static int Rebuild(HudDatabase db, int gapMinutes = GapMinutes)
    {
        var rows = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT f.played_at, h.table_name, f.net_bb, f.ev_net_bb FROM player_hand_facts f " +
                              "JOIN hands h ON h.hand_id = f.hand_id WHERE f.is_hero = 1 ORDER BY f.played_at, f.hand_id";
            using var r = cmd.ExecuteReader();
            var l = new List<(DateTime, string, double, double)>();
            while (r.Read())
            {
                if (!DateTime.TryParse(r.GetString(0), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var t)) continue;
                l.Add((t, r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? 0 : r.GetDouble(2), r.IsDBNull(3) ? 0 : r.GetDouble(3)));
            }
            return l;
        });

        var times = rows.Select(x => (x.Item1, x.Item2)).ToList();
        var sessions = new List<(DateTime Start, DateTime End, int Hands, double Net, double Ev, int Tables)>();
        int s = 0;
        for (int i = 1; i <= rows.Count; i++)
        {
            bool split = i == rows.Count || (rows[i].Item1 - rows[i - 1].Item1).TotalMinutes > gapMinutes;
            if (!split) continue;
            double net = 0, ev = 0;
            for (int k = s; k < i; k++) { net += rows[k].Item3; ev += rows[k].Item4; }
            sessions.Add((rows[s].Item1, rows[i - 1].Item1, i - s, Math.Round(net, 4), Math.Round(ev, 4), MaxTables(times, s, i)));
            s = i;
        }

        db.Query(conn =>
        {
            using var tx = conn.BeginTransaction();
            Exec(conn, tx, "DELETE FROM sessions");
            Exec(conn, tx, "UPDATE hands SET session_id = NULL");
            foreach (var x in sessions)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO sessions(started_at, ended_at, hands, net, net_bb, ev_net_bb, tables_max, tilt_score) " +
                                  "VALUES($s,$e,$h,$n,$n,$v,$t,NULL); SELECT last_insert_rowid()";
                ins.Parameters.AddWithValue("$s", x.Start.ToString("s"));
                ins.Parameters.AddWithValue("$e", x.End.ToString("s"));
                ins.Parameters.AddWithValue("$h", x.Hands);
                ins.Parameters.AddWithValue("$n", x.Net);
                ins.Parameters.AddWithValue("$v", x.Ev);
                ins.Parameters.AddWithValue("$t", x.Tables);
                var id = (long)ins.ExecuteScalar()!;
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE hands SET session_id = $id WHERE hero IS NOT NULL AND played_at >= $s AND played_at <= $e";
                upd.Parameters.AddWithValue("$id", id);
                upd.Parameters.AddWithValue("$s", x.Start.ToString("s"));
                upd.Parameters.AddWithValue("$e", x.End.ToString("s"));
                upd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        });
        return sessions.Count;
    }

    /// <summary>Reconstruye solo si hay manos del héroe más recientes que la última sesión guardada.</summary>
    public static bool EnsureUpToDate(HudDatabase db)
    {
        var (lastHand, lastSession) = db.Query(conn =>
        {
            using var a = conn.CreateCommand();
            a.CommandText = "SELECT MAX(played_at) FROM player_hand_facts WHERE is_hero = 1";
            using var b = conn.CreateCommand();
            b.CommandText = "SELECT MAX(ended_at) FROM sessions";
            return (a.ExecuteScalar() as string, b.ExecuteScalar() as string);
        });
        if (lastHand == null || lastHand == lastSession) return false;
        Rebuild(db);
        return true;
    }

    public static List<SessionRow> GetSessions(HudDatabase db, Period period)
    {
        return db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            var where = new List<string>();
            if (period.From is { } f) { where.Add("started_at >= $f"); cmd.Parameters.AddWithValue("$f", f.ToString("s")); }
            if (period.To is { } t) { where.Add("started_at <= $t"); cmd.Parameters.AddWithValue("$t", t.ToString("s")); }
            cmd.CommandText = "SELECT id, started_at, ended_at, hands, net_bb, ev_net_bb, tables_max FROM sessions" +
                              (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY started_at";
            using var r = cmd.ExecuteReader();
            var l = new List<SessionRow>();
            while (r.Read())
                l.Add(new SessionRow(r.GetInt64(0), DateTime.Parse(r.GetString(1)), DateTime.Parse(r.GetString(2)), r.GetInt32(3),
                    r.IsDBNull(4) ? 0 : r.GetDouble(4), r.IsDBNull(5) ? 0 : r.GetDouble(5), r.IsDBNull(6) ? 1 : r.GetInt32(6)));
            return l;
        });
    }

    static void Exec(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
