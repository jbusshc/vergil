using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class ParserTests
{
    const string Hand1 = """
PokerStars Hand #250000000001:  Hold'em No Limit ($0.01/$0.02 USD) - 2024/03/01 20:00:00 CET [2024/03/01 14:00:00 ET]
Table 'Alpha II' 6-max Seat #3 is the button
Seat 1: Alice ($2.00 in chips)
Seat 2: Bob ($2.00 in chips)
Seat 3: Carl ($2.00 in chips)
Seat 4: Dave ($2.00 in chips)
Seat 5: Hero ($2.00 in chips)
Seat 6: Fred ($2.00 in chips)
Dave: posts small blind $0.01
Hero: posts big blind $0.02
*** HOLE CARDS ***
Dealt to Hero [Ah Kd]
Fred: folds
Alice: folds
Bob: raises $0.04 to $0.06
Carl: calls $0.06
Dave: folds
Hero: raises $0.16 to $0.22
Bob: folds
Carl: calls $0.16
*** FLOP *** [2c 7d Jh]
Hero: bets $0.20
Carl: folds
Uncalled bet ($0.20) returned to Hero
Hero collected $0.49 from pot
Hero: doesn't show hand
*** SUMMARY ***
Total pot $0.51 | Rake $0.02
Board [2c 7d Jh]
Seat 1: Alice folded before Flop (didn't bet)



""";

    const string Hand2 = """
PokerStars Hand #250000000002:  Hold'em No Limit ($0.01/$0.02 USD) - 2024/03/01 20:01:00 CET [2024/03/01 14:01:00 ET]
Table 'Alpha II' 6-max Seat #4 is the button
Seat 1: Alice ($1.98 in chips)
Seat 2: Bob ($1.94 in chips)
Seat 4: Dave ($1.99 in chips)
Seat 5: Hero ($2.27 in chips)
Seat 6: Fred ($2.00 in chips)
Hero: posts small blind $0.01
Fred: posts big blind $0.02
*** HOLE CARDS ***
Dealt to Hero [Qs Qh]
Alice: folds
Bob: folds
Dave: raises $0.04 to $0.06
Hero: calls $0.05
Fred: folds
*** FLOP *** [Qd 5s 2h]
Hero: checks
Dave: bets $0.08
Hero: raises $0.20 to $0.28
Dave: calls $0.20
*** TURN *** [Qd 5s 2h] [9c]
Hero: bets $0.40
Dave: calls $0.40
*** RIVER *** [Qd 5s 2h 9c] [3d]
Hero: bets $1.25 and is all-in
Dave: calls $1.25 and is all-in
*** SHOW DOWN ***
Hero: shows [Qs Qh] (three of a kind, Queens)
Dave: shows [As Ad] (a pair of Aces)
Hero collected $3.93 from pot
*** SUMMARY ***
Total pot $4.00 | Rake $0.07
Board [Qd 5s 2h 9c 3d]



""";

    [Fact]
    public void Parses_header_seats_and_actions()
    {
        var h = PokerStarsParser.Parse(Hand1)!;
        Assert.Equal("250000000001", h.Id);
        Assert.Equal("Alpha II", h.TableName);
        Assert.Equal(6, h.MaxSeats);
        Assert.Equal(3, h.ButtonSeat);
        Assert.Equal(0.02m, h.BigBlind);
        Assert.Equal("Hero", h.Hero);
        Assert.Equal("Ah Kd", h.HeroCards);
        Assert.Equal(6, h.Seats.Count);
        Assert.Equal(new[] { "2c", "7d", "Jh" }, h.Board);
        Assert.Equal(0.49m, h.Collected["Hero"]);
    }

    [Fact]
    public void Computes_preflop_and_flop_stats()
    {
        var s = StatsCalculator.Compute(PokerStarsParser.Parse(Hand1)!).ToDictionary(x => x.Player);

        Assert.Equal("CO", s["Bob"].Position);
        Assert.Equal("BTN", s["Carl"].Position);
        Assert.Equal("BB", s["Hero"].Position);
        Assert.Equal("UTG", s["Fred"].Position);

        Assert.True(s["Bob"].Steal);
        Assert.True(s["Bob"].Pfr);
        Assert.True(s["Bob"].FoldTo3BetOpp && s["Bob"].FoldTo3Bet);
        Assert.True(s["Carl"].ThreeBetOpp);
        Assert.False(s["Carl"].ThreeBet);
        Assert.True(s["Carl"].Vpip);
        Assert.True(s["Hero"].ThreeBet);
        Assert.False(s["Dave"].FoldToStealOpp); // hubo un call entre medias
        Assert.False(s["Fred"].Vpip);

        Assert.True(s["Hero"].CbetOpp && s["Hero"].Cbet);
        Assert.True(s["Carl"].FoldToCbetOpp && s["Carl"].FoldToCbet);
        Assert.Equal(0.27m, s["Hero"].Net);
        Assert.Equal(-0.22m, s["Carl"].Net);
        Assert.Equal(-0.06m, s["Bob"].Net);
    }

    [Fact]
    public void Showdown_and_aggression()
    {
        var s = StatsCalculator.Compute(PokerStarsParser.Parse(Hand2)!).ToDictionary(x => x.Player);
        Assert.True(s["Dave"].Steal);
        Assert.True(s["Hero"].FoldToStealOpp);
        Assert.False(s["Hero"].FoldToSteal);
        Assert.False(s["Fred"].FoldToStealOpp); // el SB ya había pagado
        Assert.True(s["Dave"].CbetOpp && s["Dave"].Cbet);
        Assert.True(s["Hero"].FoldToCbetOpp);
        Assert.False(s["Hero"].FoldToCbet);
        Assert.True(s["Hero"].Wtsd && s["Hero"].Wsd);
        Assert.True(s["Dave"].Wtsd);
        Assert.False(s["Dave"].Wsd);
        Assert.Equal(3, s["Hero"].PostflopBets);
        Assert.Equal(1.94m, s["Hero"].Net);
        Assert.Equal(-1.99m, s["Dave"].Net);
    }

    // Mano real de un Spin (3-max) con un rival desconectado que sigue poniendo ciegas. Nicks sustituidos.
    const string SittingOutHand = """
PokerStars Hand #262681563957: Tournament #4046679805, 9000+1000 Hold'em No Limit - Level II (15/30) - 2026/11/12 0:17:30 UTC [2026/11/11 19:17:30 ET]
Table '4046679805 1' 3-max Seat #2 is the button
Seat 1: Villain1 (280 in chips) is sitting out
Seat 2: Villain2 (285 in chips)
Seat 3: Hero (935 in chips)
Hero: posts small blind 15
Villain1: posts big blind 30
*** HOLE CARDS ***
Dealt to Hero [Tc 5c]
Villain2: folds
Hero: raises 30 to 60
Villain1: folds
Uncalled bet (30) returned to Hero
Hero collected 60 from pot
Hero: doesn't show hand
*** SUMMARY ***
Total pot 60 | Rake 0
Seat 1: Villain1 (big blind) folded before Flop
Seat 2: Villain2 (button) folded before Flop (didn't bet)
Seat 3: Hero (small blind) collected (60)



""";

    [Fact]
    public void Sitting_out_player_who_posts_blinds_is_counted()
    {
        var h = PokerStarsParser.Parse(SittingOutHand)!;
        Assert.True(h.IsTournament);
        var s = StatsCalculator.Compute(h).ToDictionary(x => x.Player);
        Assert.Equal(3, s.Count);
        Assert.Equal(0m, s.Values.Sum(x => x.Net));
        Assert.Equal(30m, s["Hero"].Net);
        Assert.Equal(-30m, s["Villain1"].Net);
    }

    [Fact]
    public void Split_keeps_incomplete_last_hand()
    {
        var partial = Hand1 + Hand2[..300];
        var (hands, consumed) = PokerStarsParser.SplitComplete(partial);
        Assert.Single(hands);
        Assert.Equal(Hand1.Length, consumed);
    }

    [Fact]
    public void Database_aggregates_and_ignores_duplicates()
    {
        using var db = new HudDatabase(":memory:");
        var items = PokerStarsParser.ParseAll(Hand1 + Hand2).Select(h => (h, FactExtractor.Extract(h))).ToList();
        Assert.Equal(2, db.InsertHands(items).Count);
        Assert.Empty(db.InsertHands(items));

        var hero = db.GetPlayerStats("Hero");
        Assert.Equal(2, hero.Hands);
        Assert.Equal(100, hero.VpipPct);
        Assert.Equal(2.21, hero.Net, 2);
        Assert.Equal("Hero", db.GetHeroName());
    }
}
