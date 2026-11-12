using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class BayesTests
{
    [Fact]
    public void With_no_data_the_value_is_the_population_mean_and_reliability_is_zero()
    {
        var r = Bayes.Posterior(0, 0, 0.30, 20);
        Assert.Equal(0.30, r.Posterior, 9);
        Assert.Null(r.Raw);
        Assert.Equal(0, r.Reliability, 9);
    }

    [Fact]
    public void With_a_lot_of_data_the_value_tends_to_the_raw_ratio()
    {
        var r = Bayes.Posterior(1500, 5000, 0.60, 20);
        Assert.Equal(0.30, r.Posterior, 2);
        Assert.Equal(0.30, r.Raw!.Value, 9);
        Assert.True(r.Reliability > 0.9, $"fiabilidad {r.Reliability}");
        Assert.InRange(0.30, r.Lo, r.Hi);
    }

    [Fact]
    public void Interval_narrows_and_reliability_grows_monotonically_with_n()
    {
        double prevWidth = double.MaxValue, prevRel = -1;
        foreach (var n in new[] { 0, 5, 10, 30, 100, 300, 1000, 5000 })
        {
            var r = Bayes.Posterior(n * 0.3, n, 0.3, 15);
            Assert.True(r.Hi - r.Lo <= prevWidth + 1e-12, $"n={n}: el intervalo no se estrecha");
            Assert.True(r.Reliability >= prevRel - 1e-12, $"n={n}: la fiabilidad no crece");
            prevWidth = r.Hi - r.Lo;
            prevRel = r.Reliability;
        }
    }

    [Fact]
    public void Two_hands_at_100_percent_stay_close_to_the_population_and_look_unreliable()
    {
        var r = Bayes.Posterior(2, 2, 0.30, 20);
        Assert.InRange(r.Posterior, 0.30, 0.40);    // lejos del 100 % crudo
        Assert.Equal(1.0, r.Raw!.Value);
        Assert.True(r.Reliability < 0.1, $"fiabilidad {r.Reliability}");
    }

    [Fact]
    public void Prior_strength_does_not_inflate_reliability()
    {
        var weak = Bayes.Posterior(2, 2, 0.3, 1);
        var strong = Bayes.Posterior(2, 2, 0.3, 1000);
        Assert.Equal(weak.Reliability, strong.Reliability, 9);
        Assert.True(strong.Posterior < weak.Posterior); // el prior fuerte tira más hacia la media
    }

    [Fact]
    public void Extreme_priors_are_clamped_and_bad_inputs_rejected()
    {
        foreach (var mu in new[] { 0.0, 1.0, -3.0, 9.0 })
        {
            var r = Bayes.Posterior(3, 10, mu, 10);
            Assert.InRange(r.Posterior, 0.0001, 0.9999);
            Assert.True(r.Lo >= 0 && r.Hi <= 1 && r.Lo <= r.Hi);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Bayes.Posterior(5, 3, 0.3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bayes.Posterior(-1, 3, 0.3));
    }
}

public class PriorsTests
{
    [Fact]
    public void Empty_database_gives_the_default_mean()
    {
        using var db = new HudDatabase(":memory:");
        var engine = new StatEngine(db);
        var priors = new PopulationPriors(db, engine);
        Assert.Equal(0.30, priors.Mean(engine.GetDefinition("vpip"), null), 9);
        Assert.Equal(0.55, priors.Mean(engine.GetDefinition("cbet_flop"), null), 9);
    }

    [Fact]
    public void Population_excludes_the_hero_and_blends_with_the_default()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        // El héroe siempre juega (VPIP 100 %); los dos rivales siempre se retiran preflop (VPIP 0 %).
        using var db = Synth.Db(Enumerable.Range(0, 100).Select(i => Synth.PreHand(1000 + i, t0.AddMinutes(i), "Hero", false)));
        var engine = new StatEngine(db);
        var priors = new PopulationPriors(db, engine);
        // (0 + 200 × 0,30) / (200 + 200) = 0,15. Si contase al héroe sería 0,32.
        Assert.Equal(0.15, priors.Mean(engine.GetDefinition("vpip"), null), 6);
        var pop = engine.GetPopulation("vpip");
        Assert.Equal(200, pop.Denominator);
        Assert.Equal(0, pop.Numerator);
    }

    [Fact]
    public void Prior_is_recalculated_only_after_enough_new_hands()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        using var db = Synth.Db(Enumerable.Range(0, 3).Select(i => Synth.PreHand(2000 + i, t0.AddMinutes(i), "Hero", false)));
        var engine = new StatEngine(db);
        var priors = new PopulationPriors(db, engine) { RefreshEvery = 10, HandCountTtl = TimeSpan.Zero };
        var def = engine.GetDefinition("vpip");
        var first = priors.Mean(def, null);

        db.InsertHands(Enumerable.Range(0, 5).Select(i => Synth.PreHand(2100 + i, t0.AddMinutes(10 + i), "Hero", true))
            .Select(h => (h, FactExtractor.Extract(h))).ToList());
        Assert.Equal(first, priors.Mean(def, null)); // 5 manos nuevas < 10: sigue en caché

        db.InsertHands(Enumerable.Range(0, 10).Select(i => Synth.PreHand(2200 + i, t0.AddMinutes(30 + i), "Hero", true))
            .Select(h => (h, FactExtractor.Extract(h))).ToList());
        Assert.NotEqual(first, priors.Mean(def, null)); // 15 nuevas ≥ 10: se recalcula
    }
}

public class ProfileTests
{
    [Theory]
    [InlineData("cash", 6, "cash-6max")]
    [InlineData("cash", 3, "cash-6max")]
    [InlineData("cash", 2, "cash-hu")]
    [InlineData("cash", 9, "cash-9max")]
    [InlineData("spin", 3, "spin")]
    [InlineData("mtt", 9, "tournament")]
    [InlineData("mtt", 6, "tournament")]
    [InlineData("zoom", 6, "cash-6max")]   // sin perfil propio: cae en cash
    public void Profile_is_chosen_from_format_and_table_size(string format, int seats, string expected)
    {
        Assert.Equal(expected, HudProfiles.Select(HudProfiles.CreateDefaults(), format, seats).Id);
    }

    [Fact]
    public void Narrower_profile_wins_when_several_match()
    {
        var custom = new HudProfile { Id = "wide", Formats = { "cash" }, MinSeats = 2, MaxSeats = 10 };
        var list = HudProfiles.CreateDefaults();
        list.Add(custom);
        Assert.Equal("cash-hu", HudProfiles.Select(list, "cash", 2).Id);
        Assert.Equal("cash-6max", HudProfiles.Select(list, "cash", 6).Id);
    }

    [Fact]
    public void Every_default_profile_only_uses_stats_that_exist()
    {
        using var db = new HudDatabase(":memory:");
        var engine = new StatEngine(db);
        foreach (var p in HudProfiles.CreateDefaults())
        {
            Assert.NotEmpty(p.Lines);
            foreach (var r in p.Lines.SelectMany(l => l.Stats))
            {
                Assert.True(engine.Has(r.Id), $"{p.Id}: la estadística '{r.Id}' no existe");
                if (r.SizeSplit)
                {
                    Assert.True(engine.Has(r.Id + "_small"), $"{p.Id}: falta {r.Id}_small");
                    Assert.True(engine.Has(r.Id + "_large"), $"{p.Id}: falta {r.Id}_large");
                }
            }
        }
    }

    [Fact]
    public void Profiles_survive_a_json_round_trip_and_bad_files_fall_back_to_defaults()
    {
        var json = HudProfiles.Serialize(HudProfiles.CreateDefaults());
        Assert.Equal(json, HudProfiles.Serialize(HudProfiles.Parse(json)));

        var path = Path.Combine(Path.GetTempPath(), $"vergil-prof-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(5, HudProfiles.LoadOrDefault(path).Count);        // no existe
            File.WriteAllText(path, "{ esto no es json");
            Assert.Equal(5, HudProfiles.LoadOrDefault(path).Count);        // corrupto
            HudProfiles.Save(path, new[] { new HudProfile { Id = "solo", Formats = { "cash" } } });
            Assert.Equal("solo", Assert.Single(HudProfiles.LoadOrDefault(path)).Id);
        }
        finally { File.Delete(path); }
    }
}

public class SnapshotTests
{
    static HudSnapshotBuilder Builder(HudDatabase db, out StatEngine engine)
    {
        engine = new StatEngine(db);
        return new HudSnapshotBuilder(db, engine, new PopulationPriors(db, engine));
    }

    static HudCell Cell(HudSeatView seat, string statId) => seat.Lines.SelectMany(l => l.Cells).First(c => c.StatId == statId);

    [Fact]
    public void Acceptance_two_hands_player_shows_a_value_near_the_population_and_an_empty_reliability_bar()
    {
        var h4 = Synth.Case("cash_04_utg_open_call_cbet_fold");
        var h8 = Synth.Case("cash_08_bb_defend_donk");
        using var db = Synth.Db(new[] { h4, h8 });
        var b = Builder(db, out _);
        var profile = HudProfiles.Select(HudProfiles.CreateDefaults(), "cash", 6);

        // Villain3 ha jugado 2 manos y entró (subió) en las dos: VPIP crudo = 100 %.
        var honest = b.Build(h8, profile, new HudOptions { Mode = HudMode.Honest }).First(s => s.Player == "Villain3");
        var vpip = Cell(honest, "vpip");
        Assert.Equal(2, honest.Hands);
        Assert.InRange(vpip.Value!.Value, 28, 45);          // cerca de la población (~30), no 100
        Assert.True(vpip.Reliability < 0.1, $"fiabilidad {vpip.Reliability}");
        Assert.Equal("?", honest.Badge);                    // con 2 manos no se etiqueta a nadie

        var classic = b.Build(h8, profile, new HudOptions { Mode = HudMode.Classic }).First(s => s.Player == "Villain3");
        Assert.Equal("100", Cell(classic, "vpip").Text);    // el modo clásico enseña el crudo engañoso
    }

    [Fact]
    public void Heads_up_table_changes_the_profile_and_therefore_the_panel_lines()
    {
        var hu = Synth.Case("cash_19_heads_up");
        var six = Synth.Case("cash_03_open_steal_btn");
        using var db = Synth.Db(new[] { hu, six });
        var b = Builder(db, out _);
        var profiles = HudProfiles.CreateDefaults();

        var pHu = HudProfiles.ForHand(profiles, hu);
        var p6 = HudProfiles.ForHand(profiles, six);
        Assert.NotEqual(pHu.Id, p6.Id);   // 6-max → mano a mano cambia el perfil solo

        var seatHu = b.Build(hu, pHu).First(s => s.Player == "Hero");
        var seat6 = b.Build(six, p6).First(s => s.Player == "Hero");
        var idsHu = seatHu.Lines.Last().Cells.Select(c => c.StatId).ToArray();
        var ids6 = seat6.Lines.Last().Cells.Select(c => c.StatId).ToArray();
        Assert.NotEqual(ids6, idsHu);
    }

    [Fact]
    public void Panels_only_exist_for_players_dealt_in()
    {
        var h = Synth.Case("cash_24_sitting_out_ignored");
        using var db = Synth.Db(new[] { h });
        var seats = Builder(db, out _).Build(h, HudProfiles.CreateDefaults()[1]);
        Assert.Equal(4, seats.Count);
        Assert.DoesNotContain(seats, s => s.Player is "Villain4" or "Villain5");
    }

    [Fact]
    public void An_arrow_appears_when_the_last_hands_leave_the_credible_interval_of_the_total()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        // Villain1 se retira en las primeras 100 manos y paga en las últimas 100: total 50 %, reciente 100 %.
        var hands = Enumerable.Range(0, 200).Select(i => Synth.PreHand(3000 + i, t0.AddMinutes(i), "Hero", v1Calls: i >= 100)).ToList();
        using var db = Synth.Db(hands);
        var b = Builder(db, out _);
        var seats = b.Build(hands[^1], HudProfiles.CreateDefaults()[1]);

        Assert.Equal("up", Cell(seats.First(s => s.Player == "Villain1"), "vpip").Arrow);
        Assert.Null(Cell(seats.First(s => s.Player == "Hero"), "vpip").Arrow);       // el héroe siempre igual: sin flecha
        Assert.Null(Cell(seats.First(s => s.Player == "Villain2"), "vpip").Arrow);   // nunca entra: sin flecha
    }

    [Fact]
    public void No_arrow_when_there_are_too_few_hands()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        var hands = Enumerable.Range(0, 30).Select(i => Synth.PreHand(3500 + i, t0.AddMinutes(i), "Hero", v1Calls: i >= 15)).ToList();
        using var db = Synth.Db(hands);
        var seats = Builder(db, out _).Build(hands[^1], HudProfiles.CreateDefaults()[1]);
        Assert.Null(Cell(seats.First(s => s.Player == "Villain1"), "vpip").Arrow);   // 30 < 50 manos
    }

    [Fact]
    public void Stack_aware_stats_flag_an_asterisk_when_the_current_stack_has_little_data()
    {
        var h = Synth.Case("tour_02_spin_shove_call");
        using var db = Synth.Db(new[] { h });
        var spin = HudProfiles.Select(HudProfiles.CreateDefaults(), h.Format, h.MaxSeats);
        Assert.Equal("spin", spin.Id);
        var hero = Builder(db, out _).Build(h, spin).First(s => s.Player == "Hero");
        var shove = Cell(hero, "shove");
        Assert.True(shove.Asterisk);          // 1 sola mano en el cubo 10-20: no hay muestra para "según stack"
        Assert.NotEqual("", shove.Text);
    }

    [Fact]
    public void Stack_bucket_filter_only_counts_hands_started_with_that_stack()
    {
        var h = Synth.Case("tour_06_stack_buckets");
        using var db = Synth.Db(new[] { h });
        var e = new StatEngine(db);
        Assert.Equal(1, e.Get("Villain1", "vpip", new StatFilter { StackBucket = "<10" }).N);
        Assert.Equal(0, e.Get("Villain1", "vpip", new StatFilter { StackBucket = "40+" }).N);
        Assert.Equal(1, e.Get("Hero", "vpip", new StatFilter { StackBucket = "40+" }).N);
        Assert.Equal(1, e.Get("Hero", "vpip", new StatFilter { Formats = new[] { "mtt", "spin" } }).N);
        Assert.Equal(0, e.Get("Hero", "vpip", new StatFilter { Formats = new[] { "cash" } }).N);
    }

    [Fact]
    public void Size_split_shows_small_dot_large_when_both_buckets_have_enough_data()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        // 12 C-Bets pequeñas ($5 en un bote de 15) a las que Hero se retira; 12 grandes ($14) a las que sube.
        var hands = Enumerable.Range(0, 12).Select(i => Synth.CbetHand(4000 + i, t0.AddMinutes(i), 5, heroFolds: true))
            .Concat(Enumerable.Range(0, 12).Select(i => Synth.CbetHand(4100 + i, t0.AddMinutes(20 + i), 14, heroFolds: false))).ToList();
        using var db = Synth.Db(hands);
        var b = Builder(db, out var engine);
        var seats = b.Build(hands[^1], HudProfiles.CreateDefaults()[1]);
        var fc = Cell(seats.First(s => s.Player == "Hero"), "fcb_flop");

        Assert.Contains("·", fc.Text);
        var parts = fc.Text.Split('·').Select(int.Parse).ToArray();
        Assert.True(parts[0] > parts[1], $"FC {fc.Text}: debería retirarse más a la apuesta pequeña que a la grande");
        Assert.Equal(12, engine.Get("Hero", "fcb_flop_small").N);
        Assert.Equal(12, engine.Get("Hero", "fcb_flop_large").N);

        // Con poca muestra de un tamaño vuelve al valor global
        var few = hands.Take(14).ToList();
        using var db2 = Synth.Db(few);
        var seats2 = Builder(db2, out _).Build(few[^1], HudProfiles.CreateDefaults()[1]);
        Assert.DoesNotContain("·", Cell(seats2.First(s => s.Player == "Hero"), "fcb_flop").Text);
    }

    [Fact]
    public void All_the_hero_nicknames_are_merged_into_one()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        var a = Enumerable.Range(0, 10).Select(i => Synth.PreHand(5000 + i, t0.AddMinutes(i), "Hero", true));
        var b = Enumerable.Range(0, 10).Select(i => Synth.PreHand(5100 + i, t0.AddMinutes(20 + i), "HeroAlt", true));
        using var db = Synth.Db(a, b);
        var e = new StatEngine(db);
        Assert.Equal(new[] { "Hero", "HeroAlt" }, e.HeroNames.OrderBy(x => x).ToArray());
        Assert.Equal(20, e.Get("Hero", "vpip").N);
        Assert.Equal(20, e.Get("HeroAlt", "vpip").N);
        Assert.Equal(20, e.Get("Villain1", "vpip").N);   // el rival no se mezcla: juega las 20 manos
        Assert.Equal(20, e.GetMany(new[] { "Hero", "Villain1" }, new[] { "vpip" })["Hero"]["vpip"].N);

        // El popup también unifica: cuenta las apuestas de los dos nicks
        var detail = new PlayerDetailService(db, e, new PopulationPriors(db, e));
        Assert.NotNull(detail.MedianBetPct("HeroAlt", "f_bet_size_pct"));
    }

    [Fact]
    public void Hero_cache_is_refreshed_when_a_new_nick_appears()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        using var db = Synth.Db(Enumerable.Range(0, 5).Select(i => Synth.PreHand(5200 + i, t0.AddMinutes(i), "Hero", true)));
        var e = new StatEngine(db);
        Assert.Equal(5, e.Get("Hero", "vpip").N);
        db.InsertHands(Enumerable.Range(0, 5).Select(i => Synth.PreHand(5300 + i, t0.AddMinutes(30 + i), "HeroAlt", true))
            .Select(h => (h, FactExtractor.Extract(h))).ToList());
        Assert.Equal(10, e.Get("Hero", "vpip").N);
    }

    [Fact]
    public void Session_window_uses_only_the_hands_of_the_current_session()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        var morning = Enumerable.Range(0, 10).Select(i => Synth.PreHand(6000 + i, t0.AddMinutes(i), "Hero", false)).ToList();
        var evening = Enumerable.Range(0, 5).Select(i => Synth.PreHand(6100 + i, t0.AddHours(3).AddMinutes(i), "Hero", true)).ToList();
        using var db = Synth.Db(morning, evening);
        Assert.Equal(t0.AddHours(3), db.GetCurrentSessionStart(30));
        Assert.Equal(t0, db.GetCurrentSessionStart(240));   // con una pausa tolerada mayor, todo es una sola sesión

        var profile = HudProfiles.CreateDefaults()[1];
        profile.Window = "session";
        var seats = Builder(db, out _).Build(evening[^1], profile);
        Assert.Equal(5, seats.First(s => s.Player == "Villain1").Hands);
        profile.Window = "total";
        Assert.Equal(15, Builder(db, out _).Build(evening[^1], profile).First(s => s.Player == "Villain1").Hands);
        profile.Window = "last100";
        Assert.Equal(15, Builder(db, out _).Build(evening[^1], profile).First(s => s.Player == "Villain1").Hands);
    }

    [Fact]
    public void Tournament_profiles_fall_back_to_all_hands_with_an_asterisk_when_the_scope_has_little_data()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        // Villain1 tiene 40 manos de cash pero solo 1 de torneo (tour_06): el perfil de torneo debe usar las 41, con asterisco.
        var cash = Enumerable.Range(0, 40).Select(i => Synth.PreHand(7000 + i, t0.AddMinutes(i), "Hero", true)).ToList();
        var tour = Synth.Case("tour_06_stack_buckets");
        using var db = Synth.Db(cash, new[] { tour });
        var prof = HudProfiles.Select(HudProfiles.CreateDefaults(), tour.Format, tour.MaxSeats);
        Assert.Equal("tournament", prof.Id);
        var seat = Builder(db, out _).Build(tour, prof).First(s => s.Player == "Villain1");
        Assert.Equal(41, seat.Hands);
        Assert.True(Cell(seat, "vpip").Asterisk);
    }
}

public class HandLineTests
{
    [Theory]
    [InlineData("cash_14_barrels_showdown_win", "Hero", "BTN open · flop B62% · turn B69% · river B72%")]
    [InlineData("cash_14_barrels_showdown_win", "Villain2", "BB call · flop X/C · turn X/C · river X/C")]
    [InlineData("cash_02_check_raise_flop", "Hero", "BB call · flop X/R · turn X · river B49%")]
    [InlineData("cash_02_check_raise_flop", "Villain2", "UTG open · flop B46%/C · turn X · river F")]
    [InlineData("cash_06_four_bet_fold", "Villain3", "CO open · 4bet")]
    [InlineData("cash_06_four_bet_fold", "Hero", "BTN 3bet · fold")]
    [InlineData("cash_10_limp_raise", "Villain3", "CO limp · 3bet")]
    [InlineData("cash_09_limp_sb_complete", "Villain2", "BB · flop X/F")]
    [InlineData("cash_01_fold_to_bb", "Hero", "BTN fold")]
    public void Line_describes_what_the_player_did(string fixture, string player, string expected)
    {
        Assert.Equal(expected, HandLine.Build(Synth.Case(fixture), player));
    }

    [Fact]
    public void Call_of_a_three_bet_is_labelled()
    {
        var h = Synth.Case("cash_05_three_bet_vs_open");
        Assert.Equal("CO open · fold", HandLine.Build(h, "Villain3"));
    }
}

public class ComplianceTests
{
    sealed class DeepView : IRestrictedDuringPlay { }
    sealed class PlainView { }

    [Fact]
    public void Play_mode_follows_the_open_tables_and_notifies_only_on_changes()
    {
        var s = new PlaySessionState();
        var events = new List<bool>();
        s.Changed += events.Add;
        s.Update(0);
        Assert.False(s.IsPlaying);
        s.Update(2);
        s.Update(3);
        Assert.True(s.IsPlaying);
        Assert.Equal("Modo Juego · 3 mesas abiertas", s.BannerText[..29]);
        s.Update(0);
        Assert.Equal(new[] { true, false }, events);
        Assert.Equal("", s.BannerText);
    }

    [Fact]
    public void Restricted_views_are_blocked_only_while_playing()
    {
        var s = new PlaySessionState();
        Assert.Null(s.BlockedMessage(new DeepView()));
        s.Update(1);
        Assert.Equal("Disponible al cerrar las mesas", s.BlockedMessage(new DeepView()));
        Assert.Null(s.BlockedMessage(new PlainView()));
        Assert.Contains("1 mesa abierta", s.BannerText);
    }

    [Fact]
    public void Format_policy_allows_everything_the_user_plays_and_blocks_zoom_by_default()
    {
        var p = new FormatPolicy();
        Assert.True(p.IsAllowed("cash"));
        Assert.True(p.IsAllowed("spin"));
        Assert.True(p.IsAllowed("mtt"));
        Assert.False(p.IsAllowed("zoom"));
        Assert.True(p.IsAllowed("formato_nuevo"));   // lo desconocido no se bloquea en silencio
        p.Allowed["spin"] = false;
        Assert.False(p.IsAllowed("spin"));
    }
}

public class FirstRunTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"vergil-fr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Detects_hero_formats_and_a_healthy_folder()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), Fixtures.Text("cases/cash_01_fold_to_bb") + Fixtures.Text("cases/cash_03_open_steal_btn"),
                new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "b.txt"), Fixtures.Text("cases/tour_02_spin_shove_call"));
            var r = FirstRunCheck.Analyze(dir);
            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.Equal(2, r.Files);
            Assert.Equal(3, r.HandsFound);
            Assert.Equal("Hero", r.Hero);
            Assert.Equal(1, r.FormatsSeen["cash"] > 0 ? 1 : 0);
            Assert.True(r.FormatsSeen.ContainsKey("spin"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reports_missing_empty_and_non_english_folders()
    {
        Assert.False(FirstRunCheck.Analyze(null).Ok);
        Assert.False(FirstRunCheck.Analyze(@"C:\no\existe\nunca").FolderExists);

        var empty = TempDir();
        var spanish = TempDir();
        try
        {
            var r = FirstRunCheck.Analyze(empty);
            Assert.True(r.FolderExists);
            Assert.Contains("aún no hay historiales", r.Problems[0]);

            File.WriteAllText(Path.Combine(spanish, "x.txt"), "Mano de PokerStars n.º 99: Hold'em No Limit (1/2)\n" + new string('x', 300));
            var s = FirstRunCheck.Analyze(spanish);
            Assert.True(s.LooksNonEnglish);
            Assert.Contains(s.Problems, p => p.Contains("inglés"));
        }
        finally { Directory.Delete(empty, true); Directory.Delete(spanish, true); }
    }

    [Fact]
    public void Skipping_existing_history_imports_only_new_hands()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "HH.txt");
            File.WriteAllText(file, Fixtures.Text("cases/cash_01_fold_to_bb") + Fixtures.Text("cases/cash_03_open_steal_btn"));
            using var db = new HudDatabase(":memory:");
            using var imp = new HandHistoryImporter(db);
            Assert.Equal(1, imp.SkipExisting(dir));
            Assert.Equal(0, imp.ImportFile(file));
            File.AppendAllText(file, Fixtures.Text("cases/cash_04_utg_open_call_cbet_fold"));
            Assert.Equal(1, imp.ImportFile(file));
            Assert.Equal(1, db.GetHandCount());
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class DetailTests
{
    static (HudDatabase db, PlayerDetailService d, StatEngine e) Setup()
    {
        var db = Synth.Db(Fixtures.AllHands());
        var e = new StatEngine(db);
        return (db, new PlayerDetailService(db, e, new PopulationPriors(db, e)), e);
    }

    [Fact]
    public void Summary_lists_every_stat_with_raw_posterior_and_interval()
    {
        var (db, d, e) = Setup();
        using (db)
        {
            var rows = d.Summary("Hero");
            Assert.True(rows.Count >= 40);
            var vpip = rows.First(r => r.Id == "vpip");
            Assert.Matches(@"^\d+ \(\d+/\d+\)$", vpip.Raw);
            Assert.Contains("–", vpip.Interval);
            Assert.True(vpip.N > 20);
            Assert.DoesNotContain(rows, r => r.Id.StartsWith("fold_"));
            var af = rows.First(r => r.Id == "af");
            Assert.Equal("", af.Interval);   // AF no es una proporción: sin intervalo bayesiano
        }
    }

    [Fact]
    public void By_position_returns_a_row_for_every_position()
    {
        var (db, d, _) = Setup();
        using (db)
        {
            var t = d.ByPosition("Hero");
            Assert.Equal(PlayerDetailService.Positions.Length, t.Count);
            Assert.True(t["BTN"]["rfi"].N > 0);
            Assert.Equal(0, t["UTG2"]["rfi"].N);
        }
    }

    [Fact]
    public void Postflop_report_has_rows_size_table_and_median_bet_sizes()
    {
        var (db, d, _) = Setup();
        using (db)
        {
            var r = d.Postflop("Hero");
            Assert.Equal(6, r.Rows.Count);
            Assert.Equal(4, r.FoldBySize.Count);
            Assert.InRange(r.MedianBetPct["flop"]!.Value, 10, 300);
            Assert.Throws<ArgumentException>(() => d.MedianBetPct("Hero", "pf_vpip"));       // no es numérico
            Assert.Throws<ArgumentException>(() => d.MedianBetPct("Hero", "x; DROP TABLE"));  // ni existe
        }
    }

    [Fact]
    public void Showdowns_show_the_line_played_in_one_row()
    {
        var (db, d, _) = Setup();
        using (db)
        {
            var rows = d.Showdowns("Hero", 50);
            Assert.True(rows.Count >= 4);
            var win = rows.First(r => r.HandId == "100000014");
            Assert.Equal("Ac Kd", win.Cards);
            Assert.Equal("BTN open · flop B62% · turn B69% · river B72%", win.Line);
            Assert.True(win.NetBb > 0);
            Assert.True(rows.Zip(rows.Skip(1)).All(p => p.First.Time >= p.Second.Time), "debe estar ordenado de más reciente a más antiguo");
            Assert.Equal(2, d.Showdowns("Hero", 2).Count);
        }
    }
}
