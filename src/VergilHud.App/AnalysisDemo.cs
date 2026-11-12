using System.IO;
using System.Windows;
using VergilHud.Core;
using Path = System.IO.Path;

namespace VergilHud.App;

/// <summary>Datos sintéticos y render de las vistas de análisis (solo para verificar el aspecto, con --render-analysis).</summary>
static class AnalysisDemo
{
    static readonly string[] HoleCards = { "Ah Kd", "Qs Qh", "9c 8c", "Ad 5d", "Jh Td", "7s 7d", "Kc Qd", "As Ac", "6h 5h", "Ts 2c", "Kh Jh", "3c 3d", "Ac Qs", "8d 4h" };
    static readonly string[] Positions = { "BTN", "CO", "HJ", "BB", "SB", "UTG", "MP" };

    static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    public static void Populate(AppServices svc)
    {
        var rng = new Random(42);
        var t0 = new DateTime(2026, 10, 1, 17, 0, 0);
        svc.Db.Query(conn =>
        {
            using var tx = conn.BeginTransaction();
            using var h = conn.CreateCommand();
            h.Transaction = tx;
            h.CommandText = "INSERT INTO hands(hand_id,site,table_name,format,played_at,big_blind,hero,raw,ev_state,tournament_id) " +
                            "VALUES($id,'PokerStars',$t,$f,$at,$bb,'Hero','',1,$tid)";
            var hp = new[] { "$id", "$t", "$f", "$at", "$bb", "$tid" }.Select(n => h.Parameters.AddWithValue(n, DBNull.Value)).ToArray();
            using var f = conn.CreateCommand();
            f.Transaction = tx;
            f.CommandText = "INSERT INTO player_hand_facts(hand_id,player,seat,played_at,is_hero,format,big_blind,net_bb,ev_net_bb,wtsd,pf_vpip,pf_pfr," +
                            "position,rake_paid,hole_cards,players_dealt,stack_bucket,tournament_id) " +
                            "VALUES($id,'Hero',1,$at,1,$f,$bb,$n,$e,$w,$v,$p,$pos,$r,$c,$pd,'40+',$tid)";
            var fp = new[] { "$id", "$at", "$f", "$bb", "$n", "$e", "$w", "$v", "$p", "$pos", "$r", "$c", "$pd", "$tid" }
                .Select(n => f.Parameters.AddWithValue(n, DBNull.Value)).ToArray();

            int id = 0;
            void Hand(DateTime t, string fmt, double bb, double net, double ev, string? tid, int players)
            {
                var at = t.ToString("s");
                var hid = "D" + (++id);
                hp[0].Value = hid;
                hp[1].Value = tid == null ? "Mesa" + (id % 3) : tid + " 1";
                hp[2].Value = fmt;
                hp[3].Value = at;
                hp[4].Value = bb;
                hp[5].Value = (object?)tid ?? DBNull.Value;
                h.ExecuteNonQuery();
                fp[0].Value = hid; fp[1].Value = at; fp[2].Value = fmt; fp[3].Value = bb;
                fp[4].Value = Math.Round(net, 2); fp[5].Value = Math.Round(ev, 2);
                fp[6].Value = rng.NextDouble() < 0.27 ? 1 : 0;
                fp[7].Value = rng.NextDouble() < 0.3 ? 1 : 0;
                fp[8].Value = rng.NextDouble() < 0.18 ? 1 : 0;
                fp[9].Value = Positions[rng.Next(Positions.Length)];
                fp[10].Value = fmt == "cash" ? Math.Max(0, net) * 0.04 : 0.0;
                fp[11].Value = HoleCards[rng.Next(HoleCards.Length)];
                fp[12].Value = players;
                fp[13].Value = (object?)tid ?? DBNull.Value;
                f.ExecuteNonQuery();
            }

            // 30 días de cash: dos sesiones al día, winrate ~ +4 bb/100 con mucha varianza y algún all-in con suerte
            for (int day = 0; day < 30; day++)
                foreach (var start in new[] { 17, 22 })
                {
                    var t = t0.AddDays(day).AddHours(start - 17);
                    int n = 70 + rng.Next(90);
                    for (int i = 0; i < n; i++)
                    {
                        t = t.AddSeconds(25 + rng.Next(40));
                        double net = 0.045 * 4 + Gauss(rng) * 4.4;
                        double ev = net;
                        if (rng.NextDouble() < 0.035) ev = net - Gauss(rng) * 14;
                        Hand(t, "cash", 0.1, net, ev, null, 6);
                    }
                }

            // 120 Spins de 6 manos; ~38 % de victorias con multiplicadores 2x/3x/5x/10x
            for (int s = 0; s < 120; s++)
            {
                var ts = t0.AddDays(s % 30).AddHours(19).AddMinutes(s * 7 % 50);
                var tid = (4000000000L + s).ToString();
                bool win = rng.NextDouble() < 0.38;
                for (int i = 0; i < 6; i++)
                    Hand(ts.AddMinutes(i), "spin", 40, Gauss(rng) * 3 + (win ? 0.3 : -0.3), 0, tid, win && i >= 3 ? 2 : 3);
                double mult = win ? (rng.NextDouble() < 0.7 ? 2 : rng.NextDouble() < 0.6 ? 3 : rng.NextDouble() < 0.7 ? 5 : 10) : 0;
                using var t = conn.CreateCommand();
                t.Transaction = tx;
                t.CommandText = "INSERT INTO tournaments(id,format,buyin,rake,bounty,entrants,started_at,finished_at,hands,hero,finish_position,prize,max_seats) " +
                                "VALUES($id,'spin',10,1,0,3,$s,$e,6,'Hero',$p,$pr,3)";
                t.Parameters.AddWithValue("$id", tid);
                t.Parameters.AddWithValue("$s", ts.ToString("s"));
                t.Parameters.AddWithValue("$e", ts.AddMinutes(6).ToString("s"));
                t.Parameters.AddWithValue("$p", win ? 1 : 2 + rng.Next(2));
                t.Parameters.AddWithValue("$pr", win ? 10 * mult : 0.0);
                t.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        });
        SessionService.Rebuild(svc.Db);
    }

    public static void Render(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var db = Path.Combine(outDir, "demo.db");
        if (File.Exists(db)) File.Delete(db);
        using var svc = new AppServices(new AppSettings(), db, Path.Combine(outDir, "profiles.json"));
        Populate(svc);
        var a = svc.Analytics;
        var all = Period.All;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var dash = new DashboardView(svc);
        dash.Show(a.GetOverview(all, "cash"), SessionService.GetSessions(svc.Db, all).TakeLast(30).ToList(), a.GetProfitSeries(all, "cash", 1500), "cash");
        DemoRenderer.SaveAny(dash, Path.Combine(outDir, "vista_dashboard.png"), 1100, 1000);

        var charts = new ChartsView(svc);
        charts.Show(a.GetProfitSeries(all, "cash", 2000), a.GroupBy("hour", all, "cash"), a.GroupBy("weekday", all, "cash"), SessionService.GetSessions(svc.Db, all));
        DemoRenderer.SaveAny(charts, Path.Combine(outDir, "vista_graficas.png"), 1100, 640);

        var reports = new ReportsView(svc);
        reports.Show(a.GroupBy("position", all, "cash"), a.GetHandMatrix(all, "cash"));
        DemoRenderer.SaveAny(reports, Path.Combine(outDir, "vista_informes_grupo.png"), 1100, 560);
        reports.SelectTab(1);
        DemoRenderer.SaveAny(reports, Path.Combine(outDir, "vista_informes_matriz.png"), 1100, 700);

        var sessions = new SessionsView(svc);
        sessions.Show(SessionService.GetSessions(svc.Db, all));
        DemoRenderer.SaveAny(sessions, Path.Combine(outDir, "vista_sesiones.png"), 1100, 560);

        var tour = new TournamentsView(svc);
        var rows = TournamentReport.Load(svc.Db, all);
        tour.Show(rows, TournamentReport.Spin(svc.Db, all), null);
        DemoRenderer.SaveAny(tour, Path.Combine(outDir, "vista_torneos.png"), 1100, 1100);

        var bl = LeakDetector.LoadBaselines();
        var fake = new List<Leak>
        {
            new(bl.First(b => b.Id == "f3b"), 240, 300, 0.79, 0.76, 0.82, true, -3.1, -4.2),
            new(bl.First(b => b.Id == "rfi_btn"), 120, 400, 0.31, 0.27, 0.35, false, 0.4, -1.1),
        };
        var leaksView = new LeaksView(svc);
        leaksView.Refresh();
        leaksView.Show(fake);
        DemoRenderer.SaveAny(leaksView, Path.Combine(outDir, "vista_leaks.png"), 1100, 520);

        var tx = new List<BankrollTx> { new(1, DateTime.Now.AddDays(-30), "deposit", 200, "inicial"), new(2, DateTime.Now.AddDays(-5), "rakeback", 12.5, "mensual") };
        svc.Bankroll.Add("deposit", 200, "inicial");
        svc.Bankroll.Add("rakeback", 12.5, "mensual");
        var bank = new BankrollView(svc);
        bank.Show(svc.Bankroll.Summarize(), svc.Bankroll.List());
        DemoRenderer.SaveAny(bank, Path.Combine(outDir, "vista_bankroll.png"), 1100, 620);

        var well = new WellbeingView(svc);
        well.Refresh();
        DemoRenderer.SaveAny(well, Path.Combine(outDir, "vista_bienestar.png"), 1100, 900);

        var statsEd = new StatsEditorWindow(svc);
        DemoRenderer.SaveAny((UIElement)statsEd.Content, Path.Combine(outDir, "editor_stats.png"), 860, 600);
        var hudEd = new HudEditorWindow(svc, () => { });
        DemoRenderer.SaveAny((UIElement)hudEd.Content, Path.Combine(outDir, "editor_hud.png"), 740, 760);

        var help = new HelpView(svc);
        help.Refresh();
        DemoRenderer.SaveAny(help, Path.Combine(outDir, "vista_ayuda.png"), 1000, 700);

        // Las gráficas también por separado: el control de ScottPlot no siempre sale en una captura fuera de pantalla.
        var series = a.GetProfitSeries(all, "cash", 2000);
        var p = new ScottPlot.Plot();
        Charts.Profit(p, series, new ProfitChartOptions());
        p.SavePng(Path.Combine(outDir, "grafica_ganancias.png"), 1100, 520);
        var p2 = new ScottPlot.Plot();
        Charts.TournamentProfit(p2, rows);
        p2.SavePng(Path.Combine(outDir, "grafica_torneos.png"), 900, 400);
        var hours = a.GroupBy("hour", all, "cash");
        var p3 = new ScottPlot.Plot();
        Charts.Bars(p3, hours.Select(h => h.Key + "h").ToList(), hours.Select(h => h.BbPer100).ToList(), "bb/100");
        p3.SavePng(Path.Combine(outDir, "grafica_horas.png"), 900, 400);

        File.WriteAllText(Path.Combine(outDir, "info.txt"),
            $"overview: {a.GetOverview(all, "cash")}\nroi: {TournamentReport.Roi(rows).Message}\nrender: {sw.ElapsedMilliseconds} ms\n");
    }
}
