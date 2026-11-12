using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VergilHud.Core;

/// <summary>
/// Regla de autonota (DESIGN.md §7.4): una condición sobre los hechos de un jugador en una mano y una plantilla de texto.
/// La plantilla admite {hecho}, p. ej. "Limp-raise desde {position} con {hole_cards}".
/// </summary>
public sealed class AutoNoteRule
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string When { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Por defecto las reglas describen a los rivales; con true también al héroe.</summary>
    public bool IncludeHero { get; set; }
}

public sealed record AutoNote(long Id, string Player, DateTime Created, string RuleId, string Text, string HandId);

public static class AutoNoteRules
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly Regex Placeholder = new(@"\{([a-z0-9_]+)\}", RegexOptions.Compiled);

    public static string LoadRawJson()
    {
        using var s = typeof(AutoNoteRules).Assembly.GetManifestResourceStream("autonotes.json")
                      ?? throw new InvalidOperationException("Falta el recurso embebido autonotes.json");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static List<AutoNoteRule> Parse(string json) => JsonSerializer.Deserialize<List<AutoNoteRule>>(json, Json) ?? new();

    public static List<AutoNoteRule> Load() => Parse(LoadRawJson());

    /// <summary>Comprueba que la condición compila y que todos los {hechos} de la plantilla existen. Lanza si no.</summary>
    public static void Validate(AutoNoteRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Id)) throw new ArgumentException("La regla necesita un id");
        if (string.IsNullOrWhiteSpace(rule.Text)) throw new ArgumentException($"{rule.Id}: falta el texto");
        StatDsl.CompilePredicate(rule.When);
        foreach (Match m in Placeholder.Matches(rule.Text))
            if (!FactCatalog.Contains(m.Groups[1].Value))
                throw new ArgumentException($"{rule.Id}: la plantilla usa {{{m.Groups[1].Value}}}, que no es un hecho conocido");
    }

    public static string Render(string template, PlayerHandFacts f) => Placeholder.Replace(template, m =>
    {
        var name = m.Groups[1].Value;
        var v = f[name];
        return v switch
        {
            null => "?",
            double d => d.ToString("0.#", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "?",
        };
    });

    /// <summary>Huella de las reglas: si cambian, hay que regenerar las autonotas de las manos ya importadas.</summary>
    public static string Fingerprint(string json) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(json)))[..12];
}

/// <summary>Genera y consulta las autonotas. Se alimenta de las manos recién importadas.</summary>
public sealed class AutoNoteService
{
    readonly HudDatabase _db;
    readonly List<(AutoNoteRule Rule, Func<PlayerHandFacts, bool> Test)> _rules = new();
    readonly string _fingerprint;

    public IReadOnlyList<AutoNoteRule> Rules => _rules.Select(r => r.Rule).ToList();

    public AutoNoteService(HudDatabase db, string? rulesJson = null)
    {
        _db = db;
        var json = rulesJson ?? AutoNoteRules.LoadRawJson();
        _fingerprint = AutoNoteRules.Fingerprint(json);
        foreach (var rule in AutoNoteRules.Parse(json))
        {
            AutoNoteRules.Validate(rule);
            _rules.Add((rule, StatDsl.CompilePredicate(rule.When)));
        }
    }

    /// <summary>Evalúa las reglas sobre las manos dadas y guarda las notas nuevas (sin duplicar). Devuelve cuántas se añadieron.</summary>
    public int Process(IEnumerable<(Hand Hand, List<PlayerHandFacts> Facts)> items)
    {
        var found = new List<(string Player, string At, string Rule, string Text, string HandId)>();
        foreach (var (h, facts) in items)
            foreach (var f in facts)
            {
                bool isHero = f.Flag("is_hero");
                foreach (var (rule, test) in _rules)
                {
                    if (isHero && !rule.IncludeHero) continue;
                    if (!test(f)) continue;
                    found.Add((f.Player, h.Time.ToString("s"), rule.Id, AutoNoteRules.Render(rule.Text, f), h.Id));
                }
            }
        if (found.Count == 0) return 0;

        return _db.Query(conn =>
        {
            int added = 0;
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO auto_notes(player, created_at, rule_id, text, hand_id) VALUES($p,$a,$r,$t,$h)";
            var p = cmd.Parameters.AddWithValue("$p", ""); var a = cmd.Parameters.AddWithValue("$a", "");
            var r = cmd.Parameters.AddWithValue("$r", ""); var t = cmd.Parameters.AddWithValue("$t", ""); var h = cmd.Parameters.AddWithValue("$h", "");
            foreach (var x in found)
            {
                p.Value = x.Player; a.Value = x.At; r.Value = x.Rule; t.Value = x.Text; h.Value = x.HandId;
                added += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return added;
        });
    }

    public List<AutoNote> GetNotes(string player, int limit = 100)
    {
        var names = player.Length == 0 ? new List<string>() : new List<string> { player };
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, player, created_at, rule_id, text, hand_id FROM auto_notes WHERE player = $p ORDER BY created_at DESC, id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$p", player);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var l = new List<AutoNote>();
            while (r.Read())
                l.Add(new AutoNote(r.GetInt64(0), r.GetString(1), DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture), r.GetString(3), r.GetString(4), r.GetString(5)));
            return l;
        });
    }

    public long Count() => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM auto_notes";
        return (long)cmd.ExecuteScalar()!;
    });

    /// <summary>Borra todas las autonotas y las regenera leyendo otra vez todas las manos guardadas.</summary>
    public int Rebuild(CancellationToken ct = default)
    {
        _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM auto_notes";
            return cmd.ExecuteNonQuery();
        });
        int total = 0;
        string lastId = "";
        while (!ct.IsCancellationRequested)
        {
            var rows = _db.Query(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT hand_id, raw FROM hands WHERE hand_id > $last ORDER BY hand_id LIMIT 1000";
                cmd.Parameters.AddWithValue("$last", lastId);
                using var r = cmd.ExecuteReader();
                var l = new List<(string, string)>();
                while (r.Read()) l.Add((r.GetString(0), r.GetString(1)));
                return l;
            });
            if (rows.Count == 0) break;
            lastId = rows[^1].Item1;
            var items = new List<(Hand, List<PlayerHandFacts>)>();
            foreach (var (_, raw) in rows)
                if (PokerStarsParser.Parse(raw) is { } hand) items.Add((hand, FactExtractor.Extract(hand)));
            total += Process(items);
        }
        SetMeta("autonotes_rules", _fingerprint);
        return total;
    }

    /// <summary>Regenera las autonotas solo si las reglas cambiaron desde la última vez (o nunca se generaron).</summary>
    public bool EnsureUpToDate(CancellationToken ct = default)
    {
        if (GetMeta("autonotes_rules") == _fingerprint) return false;
        Rebuild(ct);
        return true;
    }

    string? GetMeta(string key) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    });

    void SetMeta(string key, string value) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        return cmd.ExecuteNonQuery();
    });
}
