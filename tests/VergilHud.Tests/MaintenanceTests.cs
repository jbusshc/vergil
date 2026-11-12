using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class MaintenanceTests
{
    [Fact]
    public async Task Poking_runs_ev_and_sessions_once_after_the_delay_and_groups_repeated_pokes()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        using var m = new BackgroundMaintenance(db, TimeSpan.FromMilliseconds(150));
        int completed = 0;
        var done = new TaskCompletionSource();
        m.Completed += () => { Interlocked.Increment(ref completed); done.TrySetResult(); };

        for (int i = 0; i < 20; i++) m.Poke();                 // 20 avisos seguidos…
        Assert.Empty(SessionService.GetSessions(db, Period.All)); // …y todavía no se ha hecho nada
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(400);                                  // margen por si se hubiera lanzado un segundo ciclo

        Assert.Equal(1, completed);
        Assert.NotEmpty(SessionService.GetSessions(db, Period.All));
        Assert.Equal(0L, db.Query(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM hands WHERE ev_state = 0";
            return (long)cmd.ExecuteScalar()!;
        }));
    }

    [Fact]
    public async Task Nothing_runs_without_a_poke_and_RunNow_forces_it()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        using var m = new BackgroundMaintenance(db, TimeSpan.FromMilliseconds(50));
        int completed = 0;
        m.Completed += () => Interlocked.Increment(ref completed);
        await Task.Delay(250);
        Assert.Equal(0, completed);
        await m.RunNowAsync();
        Assert.Equal(1, completed);
        Assert.NotEmpty(SessionService.GetSessions(db, Period.All));
    }
}

public class HistogramTests
{
    [Fact]
    public void Values_are_grouped_into_equal_width_bins_and_nothing_is_lost()
    {
        var values = Enumerable.Range(0, 100).Select(i => (double)i).ToList();
        var bins = Histogram.Build(values, 10);
        Assert.Equal(10, bins.Count);
        Assert.Equal(100, bins.Sum(b => b.Count));
        Assert.All(bins, b => Assert.Equal(10, b.Count));
        Assert.Equal(0, bins[0].From, 9);
        Assert.Equal(99, bins[^1].To, 9);
        Assert.Contains("…", bins[0].Label);
    }

    [Fact]
    public void The_maximum_falls_in_the_last_bin_and_edge_cases_are_safe()
    {
        var b = Histogram.Build(new[] { 0.0, 10.0 }, 5);
        Assert.Equal(1, b[0].Count);
        Assert.Equal(1, b[^1].Count);
        Assert.Empty(Histogram.Build(Array.Empty<double>(), 5));
        Assert.Single(Histogram.Build(new[] { 3.0, 3.0, 3.0 }, 5));                 // todos iguales: una sola barra
        Assert.Equal(3, Histogram.Build(new[] { 3.0, 3.0, 3.0 }, 5)[0].Count);
        Assert.Empty(Histogram.Build(new[] { 1.0 }, 0));
    }
}
