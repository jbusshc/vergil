using Microsoft.Data.Sqlite;

namespace VergilHud.Core;

/// <summary>Filtros combinables de las estadísticas (DESIGN.md §6.4).</summary>
public sealed record StatFilter
{
    public string? Format { get; init; }
    /// <summary>Varios formatos a la vez (p. ej. mtt + spin para "torneos"). Si se indica, tiene prioridad sobre Format.</summary>
    public IReadOnlyList<string>? Formats { get; init; }
    public double? MinBigBlind { get; init; }
    public double? MaxBigBlind { get; init; }
    public int? MinPlayers { get; init; }
    public int? MaxPlayers { get; init; }
    public IReadOnlyList<string>? Positions { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    /// <summary>Solo las últimas N manos de cada jugador.</summary>
    public int? LastHands { get; init; }
    /// <summary>Solo manos en las que también jugó este jugador (p. ej. el héroe): estadísticas de un rival contra mí.</summary>
    public string? VsPlayer { get; init; }
    /// <summary>Solo manos en las que el jugador empezó con este cubo de stack (&lt;10, 10-20, 20-40, 40+).</summary>
    public string? StackBucket { get; init; }

    public string Key => string.Join("|", Format, Formats is null ? null : string.Join(",", Formats), MinBigBlind, MaxBigBlind, MinPlayers,
        MaxPlayers, Positions is null ? null : string.Join(",", Positions), From?.ToString("s"), To?.ToString("s"), LastHands, VsPlayer, StackBucket);
}

/// <summary>
/// Calcula estadísticas definidas con el DSL sobre player_hand_facts.
/// Una sola consulta agrupada devuelve todas las estadísticas pedidas de todos los jugadores de la mesa.
/// </summary>
public sealed class StatEngine
{
    sealed record Compiled(StatDefinition Def, CompiledExpr Num, CompiledExpr Den);

    readonly HudDatabase _db;
    readonly Dictionary<string, Compiled> _stats = new();
    readonly StatCache _cache = new();
    volatile HashSet<string> _heroNames = new();
    int _nextId;

    public StatCache Cache => _cache;

    /// <summary>
    /// Todos los nombres con los que el usuario ha jugado (los que aparecen en "Dealt to"). Sus estadísticas se unifican:
    /// preguntar por cualquiera de ellos devuelve la suma de todos ("Yo").
    /// </summary>
    public IReadOnlyCollection<string> HeroNames => _heroNames;

    public StatEngine(HudDatabase db, IEnumerable<StatDefinition>? definitions = null)
    {
        _db = db;
        foreach (var d in definitions ?? BuiltinStats.Load()) Register(d);
        RefreshHeroNames();
        db.PlayersChanged += OnPlayersChanged;
    }

    void OnPlayersChanged(IReadOnlyCollection<string> players)
    {
        _cache.Invalidate(players);
        if (players.Any(_heroNames.Contains)) _cache.Invalidate(_heroNames);
        var before = _heroNames;
        RefreshHeroNames();
        if (!before.SetEquals(_heroNames)) _cache.Invalidate(_heroNames.Concat(before)); // aparece un nick nuevo del héroe
    }

    public void RefreshHeroNames() => _heroNames = _db.GetHeroNames().ToHashSet();

    public IEnumerable<StatDefinition> Definitions => _stats.Values.Select(c => c.Def);

    public bool Has(string id) => _stats.ContainsKey(id);

    public StatDefinition GetDefinition(string id) =>
        _stats.TryGetValue(id, out var c) ? c.Def : throw new KeyNotFoundException($"Estadística desconocida '{id}'");

    /// <summary>Registra (o reemplaza) una estadística. Lanza DslException si la expresión no compila.</summary>
    public void Register(StatDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Id)) throw new ArgumentException("La estadística necesita un id");
        int n = _nextId++;
        _stats[def.Id] = new Compiled(def, StatDsl.Compile(def.Numerator, $"s{n}n"), StatDsl.Compile(def.Denominator, $"s{n}d"));
        _cache.Clear();
    }

    public StatValue Get(string player, string statId, StatFilter? filter = null) =>
        GetMany(new[] { player }, new[] { statId }, filter)[player][statId];

    public Dictionary<string, Dictionary<string, StatValue>> GetMany(
        IReadOnlyCollection<string> players, IReadOnlyCollection<string> statIds, StatFilter? filter = null)
    {
        var fkey = filter?.Key ?? "";
        var result = players.Distinct().ToDictionary(p => p, _ => new Dictionary<string, StatValue>());
        var missing = new List<string>();
        foreach (var p in result.Keys)
        {
            bool all = true;
            foreach (var s in statIds)
            {
                if (_cache.TryGet(p, s, fkey, out var v)) result[p][s] = v; else all = false;
            }
            if (!all) missing.Add(p);
        }
        if (missing.Count == 0) return result;

        var chosen = statIds.Select(id => _stats.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"Estadística desconocida '{id}'")).ToList();
        var heroes = _heroNames;
        var queryPlayers = missing.Any(heroes.Contains) ? missing.Union(heroes).ToList() : missing;
        var rows = _db.Query(conn => Run(conn, queryPlayers, chosen, filter));

        double[]? Resolve(string p)
        {
            if (!heroes.Contains(p)) return rows.TryGetValue(p, out var a) ? a : null;
            double[]? sum = null;
            foreach (var h in heroes)
                if (rows.TryGetValue(h, out var a))
                {
                    sum ??= new double[a.Length];
                    for (int i = 0; i < a.Length; i++) sum[i] += a[i];
                }
            return sum;
        }

        foreach (var p in missing)
        {
            var arr = Resolve(p);
            for (int i = 0; i < chosen.Count; i++)
            {
                var def = chosen[i].Def;
                var v = arr != null ? new StatValue(arr[2 * i], arr[2 * i + 1], def.Scale) : new StatValue(0, 0, def.Scale);
                result[p][def.Id] = v;
                _cache.Set(p, def.Id, fkey, v);
            }
        }
        return result;
    }

    /// <summary>Valor agregado de TODOS los rivales (excluye al héroe): sirve de prior bayesiano.</summary>
    public StatValue GetPopulation(string statId, IReadOnlyList<string>? formats = null)
    {
        var c = _stats.TryGetValue(statId, out var cc) ? cc : throw new KeyNotFoundException($"Estadística desconocida '{statId}'");
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            foreach (var (k, v) in c.Num.Parameters.Concat(c.Den.Parameters)) cmd.Parameters.AddWithValue(k, v);
            var where = "is_hero = 0";
            if (formats is { Count: > 0 })
            {
                var names = new List<string>();
                for (int i = 0; i < formats.Count; i++) { names.Add("$pf" + i); cmd.Parameters.AddWithValue("$pf" + i, formats[i]); }
                where += $" AND format IN ({string.Join(",", names)})";
            }
            cmd.CommandText = $"SELECT SUM({c.Num.Sql}), SUM({c.Den.Sql}) FROM player_hand_facts WHERE {where}";
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0) || r.IsDBNull(1)) return new StatValue(0, 0, c.Def.Scale);
            return new StatValue(r.GetDouble(0), r.GetDouble(1), c.Def.Scale);
        });
    }

    static Dictionary<string, double[]> Run(SqliteConnection conn, List<string> players, List<Compiled> stats, StatFilter? f)
    {
        using var cmd = conn.CreateCommand();
        var sel = new List<string>();
        foreach (var s in stats)
        {
            sel.Add($"SUM({s.Num.Sql})");
            sel.Add($"SUM({s.Den.Sql})");
            foreach (var (k, v) in s.Num.Parameters.Concat(s.Den.Parameters)) cmd.Parameters.AddWithValue(k, v);
        }

        var where = new List<string>();
        var names = new List<string>();
        for (int i = 0; i < players.Count; i++)
        {
            names.Add("$pl" + i);
            cmd.Parameters.AddWithValue("$pl" + i, players[i]);
        }
        where.Add($"player IN ({string.Join(",", names)})");
        if (f != null)
        {
            if (f.Formats is { Count: > 0 })
            {
                var fs = new List<string>();
                for (int i = 0; i < f.Formats.Count; i++) { fs.Add("$f_fmt" + i); cmd.Parameters.AddWithValue("$f_fmt" + i, f.Formats[i]); }
                where.Add($"format IN ({string.Join(",", fs)})");
            }
            else if (f.Format != null) { where.Add("format = $f_format"); cmd.Parameters.AddWithValue("$f_format", f.Format); }
            if (f.StackBucket != null) { where.Add("stack_bucket = $f_sb"); cmd.Parameters.AddWithValue("$f_sb", f.StackBucket); }
            if (f.MinBigBlind != null) { where.Add("big_blind >= $f_minbb"); cmd.Parameters.AddWithValue("$f_minbb", f.MinBigBlind); }
            if (f.MaxBigBlind != null) { where.Add("big_blind <= $f_maxbb"); cmd.Parameters.AddWithValue("$f_maxbb", f.MaxBigBlind); }
            if (f.MinPlayers != null) { where.Add("players_dealt >= $f_minp"); cmd.Parameters.AddWithValue("$f_minp", f.MinPlayers); }
            if (f.MaxPlayers != null) { where.Add("players_dealt <= $f_maxp"); cmd.Parameters.AddWithValue("$f_maxp", f.MaxPlayers); }
            if (f.From != null) { where.Add("played_at >= $f_from"); cmd.Parameters.AddWithValue("$f_from", f.From.Value.ToString("s")); }
            if (f.To != null) { where.Add("played_at <= $f_to"); cmd.Parameters.AddWithValue("$f_to", f.To.Value.ToString("s")); }
            if (f.Positions is { Count: > 0 })
            {
                var ps = new List<string>();
                for (int i = 0; i < f.Positions.Count; i++) { ps.Add("$f_pos" + i); cmd.Parameters.AddWithValue("$f_pos" + i, f.Positions[i]); }
                where.Add($"position IN ({string.Join(",", ps)})");
            }
            if (f.VsPlayer != null)
            {
                where.Add("hand_id IN (SELECT hand_id FROM player_hand_facts WHERE player = $f_vs)");
                cmd.Parameters.AddWithValue("$f_vs", f.VsPlayer);
            }
        }

        string sql;
        if (f?.LastHands is { } n)
        {
            sql = $"WITH r AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY player ORDER BY played_at DESC, hand_id DESC) AS rn " +
                  $"FROM player_hand_facts WHERE {string.Join(" AND ", where)}) " +
                  $"SELECT player, {string.Join(", ", sel)} FROM r WHERE rn <= $f_last GROUP BY player";
            cmd.Parameters.AddWithValue("$f_last", n);
        }
        else
        {
            sql = $"SELECT player, {string.Join(", ", sel)} FROM player_hand_facts WHERE {string.Join(" AND ", where)} GROUP BY player";
        }
        cmd.CommandText = sql;

        var map = new Dictionary<string, double[]>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var arr = new double[sel.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = r.IsDBNull(i + 1) ? 0 : r.GetDouble(i + 1);
            map[r.GetString(0)] = arr;
        }
        return map;
    }
}
