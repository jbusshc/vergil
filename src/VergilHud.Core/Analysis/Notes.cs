using System.Globalization;

namespace VergilHud.Core;

public sealed record PlayerNote(string Player, string? Note, string? ColorLabel)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Note) && string.IsNullOrEmpty(ColorLabel);
}

/// <summary>Notas libres y etiqueta de color de cada jugador, y etiquetas de mano (DESIGN.md §5 y §7.4).</summary>
public sealed class NotesService
{
    /// <summary>Etiquetas de color permitidas (la interfaz les da nombre y tono).</summary>
    public static readonly string[] Colors = { "red", "orange", "yellow", "green", "blue", "purple", "grey" };

    /// <summary>Etiquetas de mano de uso habitual; se pueden escribir otras libres.</summary>
    public static readonly string[] CommonTags = { "estudiar", "error", "bluff", "valor", "bien jugada" };

    const int MaxNote = 4000, MaxTag = 40;
    readonly HudDatabase _db;

    public NotesService(HudDatabase db) => _db = db;

    // ---------- jugadores ----------
    public PlayerNote Get(string player) => GetMany(new[] { player }).GetValueOrDefault(player) ?? new PlayerNote(player, null, null);

    public Dictionary<string, PlayerNote> GetMany(IEnumerable<string> players)
    {
        var list = players.Distinct().ToList();
        var result = new Dictionary<string, PlayerNote>();
        if (list.Count == 0) return result;
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            var ps = new List<string>();
            for (int i = 0; i < list.Count; i++) { ps.Add("$p" + i); cmd.Parameters.AddWithValue("$p" + i, list[i]); }
            cmd.CommandText = $"SELECT player, note, color_label FROM players WHERE player IN ({string.Join(",", ps)}) AND (note IS NOT NULL OR color_label IS NOT NULL)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[r.GetString(0)] = new PlayerNote(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            return result;
        });
    }

    /// <summary>Guarda la nota y el color. Con los dos vacíos se borra la ficha. Lanza ArgumentException si el color no es válido.</summary>
    public void Set(string player, string? note, string? color)
    {
        if (string.IsNullOrWhiteSpace(player)) throw new ArgumentException("Falta el jugador");
        if (!string.IsNullOrEmpty(color) && !Colors.Contains(color)) throw new ArgumentException($"Color desconocido '{color}'");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > MaxNote }) note = note[..MaxNote];
        color = string.IsNullOrEmpty(color) ? null : color;

        _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            if (note == null && color == null)
            {
                cmd.CommandText = "UPDATE players SET note = NULL, color_label = NULL WHERE player = $p";
                cmd.Parameters.AddWithValue("$p", player);
            }
            else
            {
                cmd.CommandText = "INSERT INTO players(player, first_seen, last_seen, hands, color_label, note) VALUES($p,$t,$t,0,$c,$n) " +
                                  "ON CONFLICT(player) DO UPDATE SET color_label = excluded.color_label, note = excluded.note";
                cmd.Parameters.AddWithValue("$p", player);
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("s"));
                cmd.Parameters.AddWithValue("$c", (object?)color ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
            }
            return cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Jugadores con nota o color (para la lista de "Jugadores").</summary>
    public List<PlayerNote> ListAnnotated() => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT player, note, color_label FROM players WHERE note IS NOT NULL OR color_label IS NOT NULL ORDER BY player";
        using var r = cmd.ExecuteReader();
        var l = new List<PlayerNote>();
        while (r.Read()) l.Add(new PlayerNote(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return l;
    });

    // ---------- etiquetas de mano ----------
    static string NormalizeTag(string tag)
    {
        var t = (tag ?? "").Trim();
        if (t.Length == 0) throw new ArgumentException("La etiqueta está vacía");
        if (t.Length > MaxTag) throw new ArgumentException($"La etiqueta es demasiado larga (máx. {MaxTag})");
        return t.ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>Añade una etiqueta a una mano. Devuelve false si ya la tenía.</summary>
    public bool AddTag(string handId, string tag)
    {
        var t = NormalizeTag(tag);
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO hand_tags(hand_id, tag, created_at) VALUES($h,$t,$c)";
            cmd.Parameters.AddWithValue("$h", handId);
            cmd.Parameters.AddWithValue("$t", t);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("s"));
            return cmd.ExecuteNonQuery() > 0;
        });
    }

    public bool RemoveTag(string handId, string tag)
    {
        var t = NormalizeTag(tag);
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM hand_tags WHERE hand_id = $h AND tag = $t";
            cmd.Parameters.AddWithValue("$h", handId);
            cmd.Parameters.AddWithValue("$t", t);
            return cmd.ExecuteNonQuery() > 0;
        });
    }

    public List<string> GetTags(string handId) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag FROM hand_tags WHERE hand_id = $h ORDER BY tag";
        cmd.Parameters.AddWithValue("$h", handId);
        using var r = cmd.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add(r.GetString(0));
        return l;
    });

    /// <summary>Todas las etiquetas en uso con su número de manos.</summary>
    public List<(string Tag, int Hands)> AllTags() => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag, COUNT(*) FROM hand_tags GROUP BY tag ORDER BY COUNT(*) DESC, tag";
        using var r = cmd.ExecuteReader();
        var l = new List<(string, int)>();
        while (r.Read()) l.Add((r.GetString(0), r.GetInt32(1)));
        return l;
    });

    public List<string> HandsWithTag(string tag)
    {
        var t = NormalizeTag(tag);
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hand_id FROM hand_tags WHERE tag = $t ORDER BY created_at DESC";
            cmd.Parameters.AddWithValue("$t", t);
            using var r = cmd.ExecuteReader();
            var l = new List<string>();
            while (r.Read()) l.Add(r.GetString(0));
            return l;
        });
    }
}
