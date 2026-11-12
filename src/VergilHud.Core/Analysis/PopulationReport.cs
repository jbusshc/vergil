namespace VergilHud.Core;

/// <param name="Pop">Valor medio del rival medio (0..1) con su intervalo del 95 % solo de datos.</param>
/// <param name="Hero">Tu propio valor en el mismo spot, si lo has jugado.</param>
public sealed record PopulationRow(string StatId, string Label, string Description, double Pop, double PopLo, double PopHi, int PopN, double? Hero, int HeroN)
{
    /// <summary>Tu frecuencia queda fuera del intervalo de la población (y tienes muestra suficiente).</summary>
    public bool HeroDiffers => Hero is { } h && HeroN >= 30 && (h < PopLo || h > PopHi);
}

/// <summary>Cómo juega el rival medio de tu formato y límite en cada spot, comparado con tu propio juego (DESIGN.md §19.3).</summary>
public static class PopulationReport
{
    public static List<PopulationRow> Build(HudDatabase db, StatEngine engine, IReadOnlyList<string>? formats, double? minBb = null, double? maxBb = null)
    {
        var heroName = db.GetHeroName();
        var filter = new StatFilter { Formats = formats is { Count: > 0 } ? formats : null, MinBigBlind = minBb, MaxBigBlind = maxBb };
        var rows = new List<PopulationRow>();
        foreach (var def in engine.Definitions.Where(d => d.IsProportion).OrderBy(d => d.Label))
        {
            var (num, den) = PopulationValue(db, def, filter);
            if (den < 30) continue;
            var (lo, hi) = Bayes.DataInterval(num, den);
            double? hero = null; int heroN = 0;
            if (heroName != null)
            {
                var v = engine.Get(heroName, def.Id, filter);
                heroN = (int)v.Denominator;
                hero = v.Value;
            }
            rows.Add(new PopulationRow(def.Id, def.Label, def.Description, num / den, lo, hi, (int)den, hero, heroN));
        }
        return rows;
    }

    /// <summary>Numerador y denominador agregados de todos los rivales (excluye al héroe).</summary>
    static (double Num, double Den) PopulationValue(HudDatabase db, StatDefinition def, StatFilter f)
    {
        var n = StatDsl.Compile(def.Numerator, "pn");
        var d = StatDsl.Compile(def.Denominator, "pd");
        return db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            foreach (var (k, v) in n.Parameters.Concat(d.Parameters)) cmd.Parameters.AddWithValue(k, v);
            var where = "is_hero = 0";
            if (f.Formats is { Count: > 0 })
            {
                for (int i = 0; i < f.Formats.Count; i++) cmd.Parameters.AddWithValue("$pf" + i, f.Formats[i]);
                where += $" AND format IN ({string.Join(",", f.Formats.Select((_, i) => "$pf" + i))})";
            }
            if (f.MinBigBlind != null) { where += " AND big_blind >= $pmin"; cmd.Parameters.AddWithValue("$pmin", f.MinBigBlind); }
            if (f.MaxBigBlind != null) { where += " AND big_blind <= $pmax"; cmd.Parameters.AddWithValue("$pmax", f.MaxBigBlind); }
            cmd.CommandText = $"SELECT COALESCE(SUM({n.Sql}),0), COALESCE(SUM({d.Sql}),0) FROM player_hand_facts WHERE {where}";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (Convert.ToDouble(r.GetValue(0)), Convert.ToDouble(r.GetValue(1)));
        });
    }
}
