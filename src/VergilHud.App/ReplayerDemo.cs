using System.IO;
using VergilHud.Core;
using Path = System.IO.Path;

namespace VergilHud.App;

/// <summary>--render-replayer &lt;historial.txt&gt; &lt;carpeta&gt;: dibuja la primera mano del archivo en varios pasos (solo para verificar).</summary>
static class ReplayerDemo
{
    public static void Render(string file, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var tmp = Path.Combine(Path.GetTempPath(), "vergil-replayer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var settings = AppSettings.Load();
        using var svc = new AppServices(settings, Path.Combine(tmp, "hud.db"), Path.Combine(tmp, "profiles.json"));
        var hand = PokerStarsParser.ParseAll(File.ReadAllText(file)).First();
        var view = new ReplayerView(svc, hand);
        var win = new System.Windows.Window { Content = view, Width = 1240, Height = 780, ShowInTaskbar = false, Left = -4000, Top = 0, WindowStyle = System.Windows.WindowStyle.None };
        win.Show();
        var n = view.StepCount;
        foreach (var i in new[] { 0, n / 2, n - 2, n - 1 }.Distinct().Where(i => i >= 0))
        {
            view.Go(i);
            view.UpdateLayout();
            DemoRenderer.SaveAny(view, Path.Combine(outDir, $"replayer_{i:00}.png"), 1240, 780);
        }
        win.Close();
        try { Directory.Delete(tmp, true); } catch { }
    }
}
