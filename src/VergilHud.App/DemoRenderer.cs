using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VergilHud.Core;
using Path = System.IO.Path;

namespace VergilHud.App;

/// <summary>Dibuja el HUD y los popups a PNG a partir de la última mano de la base de datos (solo para verificar el aspecto).</summary>
static class DemoRenderer
{
    public static void Run(string dbPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var svc = new AppServices(new AppSettings(), dbPath, Path.Combine(outDir, "profiles.json"));
        var hand = svc.Db.GetLatestHand() ?? throw new InvalidOperationException("La base de datos no tiene manos.");
        var profile = HudProfiles.ForHand(svc.Profiles, hand);
        File.WriteAllText(Path.Combine(outDir, "info.txt"),
            $"mano {hand.Id} · mesa {hand.TableName} · formato {hand.Format} · {hand.MaxSeats} asientos · perfil {profile.Id}\n");

        foreach (var mode in new[] { HudMode.Honest, HudMode.Classic })
            RenderTable(svc, hand, profile, mode, Path.Combine(outDir, $"mesa_{mode}.png"));

        var views = svc.Builder.Build(hand, profile, svc.Options);
        var who = views.FirstOrDefault(v => !v.IsHero) ?? views[0];
        foreach (var player in new[] { who.Player, hand.Hero ?? who.Player }.Distinct())
            for (int tab = 0; tab < 4; tab++)
                Save(PlayerPopup.Create(svc, player, profile, tab), Path.Combine(outDir, $"popup_{Safe(player)}_{tab}.png"), 560, 0);
    }

    static string Safe(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());

    static void RenderTable(AppServices svc, Hand hand, HudProfile profile, HudMode mode, string path)
    {
        const double W = 1190, H = 820;
        var canvas = new Canvas { Width = W, Height = H, Background = new SolidColorBrush(Color.FromRgb(20, 22, 34)) };
        var felt = new Ellipse
        {
            Width = W * 0.62, Height = H * 0.50, Fill = new SolidColorBrush(Color.FromRgb(24, 108, 52)),
            Stroke = new SolidColorBrush(Color.FromRgb(70, 70, 90)), StrokeThickness = 6,
        };
        Canvas.SetLeft(felt, W * 0.19);
        Canvas.SetTop(felt, H * 0.20);
        canvas.Children.Add(felt);

        int maxSeats = Math.Max(hand.MaxSeats, hand.Seats.Count == 0 ? 2 : hand.Seats.Max(s => s.Number));
        int? heroSeat = hand.Hero == null ? null : hand.Seats.FirstOrDefault(x => x.Player == hand.Hero)?.Number;
        var options = new HudOptions { Mode = mode };
        foreach (var view in svc.Builder.Build(hand, profile, options))
        {
            int visual = heroSeat != null ? ((view.Seat - heroSeat.Value) % maxSeats + maxSeats) % maxSeats : (view.Seat - 1) % maxSeats;
            var panel = new SeatPanel(view, visual, options, 0.85, true, () => new Border());
            System.Windows.Documents.TextElement.SetFontSize(panel, PanelLayout.FontSize(W));
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var (ax, ay) = PanelLayout.Anchor(maxSeats, visual);
            Canvas.SetLeft(panel, ax * W - panel.DesiredSize.Width / 2);
            Canvas.SetTop(panel, ay * H - panel.DesiredSize.Height / 2);
            canvas.Children.Add(panel);
        }
        Save(canvas, path, W, H);
    }

    public static void SaveAny(UIElement el, string path, double width, double height) => Save(el, path, width, height);

    static void Save(UIElement el, string path, double width, double height)
    {
        el.Measure(new Size(width > 0 ? width : double.PositiveInfinity, height > 0 ? height : double.PositiveInfinity));
        var size = el.DesiredSize;
        el.Arrange(new Rect(size));
        el.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
