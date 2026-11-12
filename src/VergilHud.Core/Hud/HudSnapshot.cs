namespace VergilHud.Core;

public enum HudMode
{
    /// <summary>Valor posterior bayesiano + barra de fiabilidad (por defecto).</summary>
    Honest,
    /// <summary>x/n en crudo, como los HUD clásicos.</summary>
    Classic,
}

public sealed class HudOptions
{
    public HudMode Mode { get; set; } = HudMode.Honest;
    /// <summary>Si en el ámbito del perfil (p. ej. torneos) hay menos manos que esto, se usan todas y se marca con asterisco.</summary>
    public int ScopeFallbackMinHands { get; set; } = 30;
    /// <summary>Muestra mínima, dentro del cubo de stack actual, para usar la estadística "según stack".</summary>
    public int StackBucketMinSample { get; set; } = 10;
    /// <summary>Muestra mínima de cada tamaño (pequeña y grande) para mostrar "pq·gr".</summary>
    public int SizeSplitMinSample { get; set; } = 8;
    /// <summary>Muestras mínimas (total y reciente) para que una flecha de cambio de estilo tenga sentido.</summary>
    public int ArrowMinTotal { get; set; } = 50;
    public int ArrowMinRecent { get; set; } = 20;
    public int RecentWindow { get; set; } = 100;
}

/// <summary>Una estadística lista para pintar.</summary>
/// <param name="Color">Clave de color según los umbrales de la estadística (low, mid, loose, high) o null.</param>
/// <param name="Arrow">"up" o "down" si las últimas manos se salen del intervalo del total; null si no.</param>
/// <param name="Asterisk">Calculada con datos de fuera del ámbito del perfil, o sin muestra suficiente en el stack actual.</param>
public sealed record HudCell(string StatId, string Label, string Sep, bool ShowLabel, string Text, double? Value,
    double Reliability, string? Color, string? Arrow, bool Asterisk, int N);

public sealed record HudLineView(IReadOnlyList<HudCell> Cells);

/// <param name="ColorLabel">Etiqueta de color que el usuario puso a este jugador (red, orange, yellow, green, blue, purple, grey).</param>
/// <param name="Note">Nota libre sobre el jugador (se ve al pasar el ratón por el panel).</param>
public sealed record HudSeatView(string Player, int Seat, int Hands, string Badge, bool IsHero, IReadOnlyList<HudLineView> Lines,
    string? ColorLabel = null, string? Note = null);

/// <summary>Clasificación orientativa del estilo de un jugador a partir de sus estadísticas posteriores.</summary>
public static class ProfileClassifier
{
    public static string Classify(double vpip, double pfr, double? af, int hands)
    {
        if (hands < 15) return "?";
        if (vpip >= 45 && pfr < 12) return "Calling station";
        if (vpip >= 35 && (af ?? 2) < 1.5) return "Fish pasivo";
        if (vpip >= 35) return "Maníaco";
        if (vpip <= 14) return "Nit";
        if (vpip <= 26 && vpip - pfr <= 6) return "TAG";
        if (vpip > 26 && pfr >= 18) return "LAG";
        return "Loose-pasivo";
    }
}

/// <summary>
/// Prepara lo que muestra el HUD para los jugadores de una mano: valores bayesianos, fiabilidad, colores, flechas de cambio
/// de estilo, estadísticas según stack y según tamaño de apuesta. No depende de WPF, así que se prueba entero.
/// </summary>
public sealed class HudSnapshotBuilder
{
    readonly HudDatabase _db;
    readonly StatEngine _engine;
    readonly PopulationPriors _priors;
    readonly NotesService? _notes;

    /// <summary>Modelo de arquetipos por tipo de juego (cash/spin/mtt). Si no hay (o no hay muestra), se usa el clasificador fijo.</summary>
    public Func<string, ArchetypeModel?>? Archetypes { get; set; }

    public HudSnapshotBuilder(HudDatabase db, StatEngine engine, PopulationPriors priors, NotesService? notes = null)
    {
        _db = db;
        _engine = engine;
        _priors = priors;
        _notes = notes;
    }

    public IReadOnlyList<HudSeatView> Build(Hand hand, HudProfile profile, HudOptions? options = null)
    {
        var o = options ?? new HudOptions();
        var participants = new HashSet<string>(hand.Actions.Select(a => a.Player));
        if (hand.Hero != null) participants.Add(hand.Hero);
        var seats = hand.Seats.Where(s => participants.Contains(s.Player)).OrderBy(s => s.Number).ToList();
        if (seats.Count == 0) return Array.Empty<HudSeatView>();

        var players = seats.Select(s => s.Player).ToList();
        decimal bb = hand.BigBlind > 0 ? hand.BigBlind : 1;
        var scope = profile.ScopeFormats();
        DateTime? sessionStart = profile.Window == "session" ? _db.GetCurrentSessionStart() : null;

        StatFilter Make(IReadOnlyList<string>? fmts) => new()
        {
            Formats = fmts,
            LastHands = profile.Window == "last100" ? o.RecentWindow : null,
            From = sessionStart,
        };
        var scopeFilter = Make(scope);
        var allFilter = Make(null);

        // 1) ¿Hay manos suficientes en el ámbito del perfil? Si no, se usan todas (con asterisco).
        var inAll = _engine.GetMany(players, new[] { "vpip" }, allFilter);
        var inScope = scope == null ? null : _engine.GetMany(players, new[] { "vpip" }, scopeFilter);
        var useAll = players.ToDictionary(p => p, p =>
            scope != null && inScope![p]["vpip"].N < o.ScopeFallbackMinHands && inAll[p]["vpip"].N > inScope[p]["vpip"].N);
        var filterOf = players.ToDictionary(p => p, p => useAll[p] ? allFilter : scopeFilter);

        // 2) Estadísticas necesarias (las del perfil, las de tamaño y las del badge).
        var refs = profile.Lines.SelectMany(l => l.Stats).Where(r => _engine.Has(r.Id)).ToList();
        var ids = new HashSet<string> { "vpip", "pfr", "af" };
        foreach (var r in refs)
        {
            ids.Add(r.Id);
            if (r.SizeSplit && _engine.Has(r.Id + "_small") && _engine.Has(r.Id + "_large"))
            {
                ids.Add(r.Id + "_small");
                ids.Add(r.Id + "_large");
            }
        }
        var idList = ids.ToList();

        // 3) Una consulta agrupada por tipo de filtro (total y, para las flechas, últimas N manos).
        var values = new Dictionary<string, Dictionary<string, StatValue>>();
        var recent = new Dictionary<string, Dictionary<string, StatValue>>();
        foreach (var g in players.GroupBy(p => filterOf[p].Key))
        {
            var f = filterOf[g.First()];
            foreach (var kv in _engine.GetMany(g.ToList(), idList, f)) values[kv.Key] = kv.Value;
            if (profile.Window == "total")
                foreach (var kv in _engine.GetMany(g.ToList(), idList, f with { LastHands = o.RecentWindow })) recent[kv.Key] = kv.Value;
        }

        // 4) Estadísticas "según stack": solo con las manos en que tenía el mismo cubo de stack que ahora.
        var stackIds = refs.Where(r => r.StackAware).Select(r => r.Id).Distinct().ToList();
        var stackValues = new Dictionary<string, Dictionary<string, StatValue>>();
        if (stackIds.Count > 0)
            foreach (var s in seats)
            {
                var bucket = FactExtractor.StackBucket((double)(s.Stack / bb));
                stackValues[s.Player] = _engine.GetMany(new[] { s.Player }, stackIds, filterOf[s.Player] with { StackBucket = bucket })[s.Player];
            }

        // 5) Celdas.
        var annotations = _notes?.GetMany(players) ?? new Dictionary<string, PlayerNote>();
        var result = new List<HudSeatView>();
        foreach (var seat in seats)
        {
            var p = seat.Player;
            var vals = values[p];
            var priorFormats = useAll[p] ? null : scope;
            var lines = new List<HudLineView>();
            foreach (var line in profile.Lines)
            {
                var cells = new List<HudCell>();
                foreach (var r in line.Stats)
                {
                    if (!_engine.Has(r.Id)) continue;
                    var def = _engine.GetDefinition(r.Id);
                    var v = vals[r.Id];
                    bool asterisk = useAll[p];
                    StatValue? rec = null;
                    if (r.StackAware)
                    {
                        if (stackValues.TryGetValue(p, out var sv) && sv[r.Id].N >= o.StackBucketMinSample) v = sv[r.Id];
                        else asterisk = true; // poca muestra en este stack: se usa la global, avisando
                    }
                    else if (recent.TryGetValue(p, out var rv)) rec = rv[r.Id];

                    var cell = MakeCell(r, def, v, rec, priorFormats, asterisk, o);
                    if (r.SizeSplit && vals.TryGetValue(r.Id + "_small", out var small) && vals.TryGetValue(r.Id + "_large", out var large)
                        && small.N >= o.SizeSplitMinSample && large.N >= o.SizeSplitMinSample)
                    {
                        var ds = _engine.GetDefinition(r.Id + "_small");
                        var dl = _engine.GetDefinition(r.Id + "_large");
                        var cs = MakeCell(r, ds, small, null, priorFormats, asterisk, o);
                        var cl = MakeCell(r, dl, large, null, priorFormats, asterisk, o);
                        cell = cell with { Text = $"{cs.Text}·{cl.Text}", Reliability = Math.Min(cs.Reliability, cl.Reliability) };
                    }
                    cells.Add(cell);
                }
                lines.Add(new HudLineView(cells));
            }

            annotations.TryGetValue(p, out var ann);
            result.Add(new HudSeatView(p, seat.Number, (int)vals["vpip"].Denominator, Badge(p, hand.Format, vals, priorFormats), p == hand.Hero, lines,
                ann?.ColorLabel, ann?.Note));
        }
        return result;
    }

    string Badge(string player, string? format, Dictionary<string, StatValue> vals, IReadOnlyList<string>? priorFormats)
    {
        var model = Archetypes?.Invoke(ArchetypeModel.KeyForFormat(format));
        if (model != null)
        {
            try
            {
                if (model.Classify(player, _engine, _priors, out var n) is { } r && n >= 15)
                    return r.Confidence >= 0.6 ? r.Archetype.Label : r.Archetype.Label + "?";
            }
            catch (Exception) { /* sin modelo utilizable: clasificador fijo */ }
        }
        double Post(string id)
        {
            var def = _engine.GetDefinition(id);
            var v = vals[id];
            return Bayes.Posterior(v.Numerator, v.Denominator, _priors.Mean(def, priorFormats), def.Prior?.Strength ?? 10).Posterior * 100;
        }
        return ProfileClassifier.Classify(Post("vpip"), Post("pfr"), vals["af"].Value, (int)vals["vpip"].Denominator);
    }

    HudCell MakeCell(HudStatRef r, StatDefinition def, StatValue v, StatValue? rec, IReadOnlyList<string>? priorFormats,
        bool asterisk, HudOptions o)
    {
        double? shown;
        double reliability;
        string text;
        string? arrow = null;

        if (def.IsProportion)
        {
            var b = Bayes.Posterior(v.Numerator, v.Denominator, _priors.Mean(def, priorFormats), def.Prior?.Strength ?? 10);
            reliability = b.Reliability;
            if (o.Mode == HudMode.Honest)
            {
                shown = b.Posterior * 100;
                text = def.FormatValue(new StatValue(b.Posterior, 1));
            }
            else
            {
                shown = v.Value is { } x ? x * 100 : null;
                text = def.FormatValue(v);
            }
            if (rec is { } rv && v.N >= o.ArrowMinTotal && rv.N >= o.ArrowMinRecent && rv.Value is { } rx)
            {
                // Se compara con el intervalo del histórico basado solo en sus datos: el encogimiento hacia la población
                // del valor mostrado inventaría flechas en jugadores que siempre hacen lo mismo.
                var (lo, hi) = Bayes.DataInterval(v.Numerator, v.Denominator);
                arrow = rx < lo ? "down" : rx > hi ? "up" : null;
            }
        }
        else
        {
            shown = v.Value;
            text = def.FormatValue(v);
            reliability = Math.Clamp(v.Denominator / 30.0, 0, 1);
        }

        string? color = v.N > 0 && shown is { } s ? ColorFor(def, s) : null;
        return new HudCell(def.Id, def.Label, r.Sep, r.ShowLabel, text, shown, reliability, color, arrow, asterisk, v.N);
    }

    static string? ColorFor(StatDefinition def, double shown)
    {
        if (def.Thresholds is null) return null;
        foreach (var t in def.Thresholds)
            if (t.Lt is null || shown < t.Lt) return t.Color;
        return null;
    }
}
