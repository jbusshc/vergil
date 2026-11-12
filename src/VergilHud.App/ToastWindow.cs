using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VergilHud.App;

/// <summary>Aviso discreto en la esquina inferior derecha: no roba el foco, no cierra nada y desaparece solo.</summary>
public sealed class ToastWindow : Window
{
    static readonly List<ToastWindow> Open = new();

    public static void Show(string title, string message, int seconds = 14)
    {
        var t = new ToastWindow(title, message, seconds);
        Open.Add(t);
        t.Closed += (_, _) => Open.Remove(t);
        t.Show();
        double bottom = SystemParameters.WorkArea.Bottom - 16;
        foreach (var w in Open.Take(Open.Count - 1)) bottom = Math.Min(bottom, w.Top - 10);
        t.Left = SystemParameters.WorkArea.Right - t.Width - 16;
        t.Top = bottom - t.ActualHeight;
    }

    ToastWindow(string title, string message, int seconds)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Width = 340;
        SizeToContent = SizeToContent.Height;

        var body = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
        body.Children.Add(Ui.Label(title, 13, true, Theme.Yellow));
        body.Children.Add(Ui.Label(message, 12, false, Theme.Text, new Thickness(0, 4, 0, 0)));
        Content = new Border { Background = Theme.Panel, BorderBrush = Theme.Yellow, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = body };
        MouseLeftButtonDown += (_, _) => Close();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }
}
