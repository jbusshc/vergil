using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class DslTests
{
    [Theory]
    [InlineData("pf_vpip")]
    [InlineData("pf_vpip && !pf_3bet_pot")]
    [InlineData("f_cbet && f_cbet_ip && !pf_3bet_pot")]
    [InlineData("position == \"BTN\" && pf_open")]
    [InlineData("position in [\"CO\", \"BTN\"]")]
    [InlineData("players_dealt in [2, 3]")]
    [InlineData("f_bets + f_raises + t_bets")]
    [InlineData("(pf_vpip || pf_pfr) && eff_stack_bb <= 12.5")]
    [InlineData("net_bb < -3")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("f_faced_size_bucket != \"small\"")]
    public void Valid_expressions_compile(string expr)
    {
        var c = StatDsl.Compile(expr);
        Assert.StartsWith("COALESCE(", c.Sql);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no_existe")]
    [InlineData("pf_vpip &&")]
    [InlineData("(pf_vpip")]
    [InlineData("pf_vpip pf_pfr")]
    [InlineData("position == 3")]
    [InlineData("position > \"A\"")]
    [InlineData("pf_vpip == \"x\"")]
    [InlineData("position")]
    [InlineData("position + 1")]
    [InlineData("pf_vpip; DROP TABLE hands")]
    [InlineData("'; DROP TABLE hands; --")]
    [InlineData("pf_vpip /* c */")]
    [InlineData("position in [pf_vpip]")]
    [InlineData("- pf_vpip")]
    [InlineData("pf_vpip $x")]
    public void Invalid_expressions_throw_with_position(string expr)
    {
        var ex = Assert.Throws<DslException>(() => StatDsl.Compile(expr));
        Assert.Contains("posición", ex.Message);
    }

    [Fact]
    public void Unknown_fact_reports_its_position()
    {
        var ex = Assert.Throws<DslException>(() => StatDsl.Compile("pf_vpip && nada_raro"));
        Assert.Equal(11, ex.Position);
        Assert.Contains("nada_raro", ex.Message);
    }

    [Fact]
    public void Literals_never_reach_the_sql_text()
    {
        const string evil = "BTN'); DROP TABLE hands; --";
        var c = StatDsl.Compile($"position == \"{evil}\"");
        Assert.DoesNotContain("DROP", c.Sql);
        Assert.DoesNotContain("BTN", c.Sql);
        Assert.Contains(evil, c.Parameters.Values.OfType<string>());
        Assert.All(c.Parameters.Keys, k => Assert.StartsWith("$p", k));
    }

    [Fact]
    public void Evil_literal_executes_harmlessly_and_tables_survive()
    {
        using var db = new HudDatabase(":memory:");
        var engine = new StatEngine(db, new[]
        {
            new StatDefinition { Id = "evil", Numerator = "position == \"x'); DROP TABLE hands; --\"", Denominator = "1" },
        });
        var v = engine.Get("nadie", "evil");
        Assert.Equal(0, v.Denominator);
        Assert.Equal(0L, db.GetHandCount()); // la tabla sigue existiendo
    }

    [Fact]
    public void Prefix_keeps_parameter_names_apart()
    {
        var a = StatDsl.Compile("position == \"BTN\"", "a");
        var b = StatDsl.Compile("position == \"CO\"", "b");
        Assert.Empty(a.Parameters.Keys.Intersect(b.Parameters.Keys));
    }

    [Fact]
    public void Every_builtin_stat_compiles_and_has_unique_id()
    {
        var defs = BuiltinStats.Load();
        Assert.True(defs.Count >= 40, $"solo hay {defs.Count} estadísticas");
        Assert.Equal(defs.Count, defs.Select(d => d.Id).Distinct().Count());
        foreach (var d in defs)
        {
            StatDsl.Compile(d.Numerator);
            StatDsl.Compile(d.Denominator);
            Assert.False(string.IsNullOrWhiteSpace(d.Label));
            Assert.Contains(d.Format, new[] { "pct0", "pct1", "dec1", "dec2", "int" });
        }
    }

    [Fact]
    public void Every_fact_in_the_catalog_is_unique_and_usable()
    {
        var names = FactCatalog.Columns.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.True(names.Count >= 100);
        foreach (var n in names) Assert.True(FactCatalog.Contains(n));
    }
}
