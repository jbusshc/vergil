using System.Windows;
using System.Windows.Input;
using VergilHud.Core;

namespace VergilHud.App;

public sealed class ReplayerWindow : Window
{
    readonly ReplayerView _view;

    public ReplayerWindow(AppServices svc, Hand hand)
    {
        _view = new ReplayerView(svc, hand);
        Title = $"Replayer · mano #{hand.Id}";
        Width = 1240;
        Height = 780;
        MinWidth = 1000;
        MinHeight = 700;
        Background = Theme.Bg;
        Content = _view;
        PreviewKeyDown += OnKey;
        Closed += (_, _) => _view.Stop();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right: _view.Next(); break;
            case Key.Left: _view.Prev(); break;
            case Key.Home: _view.First(); break;
            case Key.End: _view.Last(); break;
            case Key.Space: _view.TogglePlay(); break;
            default: return;
        }
        e.Handled = true;
    }
}

/// <summary>
/// Abre el replayer respetando el Modo Juego: con mesas abiertas no se puede (es análisis profundo de manos) y, si se empieza
/// a jugar con un replayer abierto, se cierra solo.
/// </summary>
public static class ReplayerLauncher
{
    static readonly List<ReplayerWindow> Open = new();

    public static bool TryOpen(AppServices svc, Window? owner, string handId)
    {
        var blocked = svc.Play.BlockedMessage(new ReplayerAccess());
        if (blocked != null)
        {
            MessageBox.Show(owner, "El replayer analiza manos en profundidad, así que no se puede usar con mesas abiertas.\n\n" + blocked + ".",
                "Vergil HUD · Modo Juego", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var hand = svc.Db.GetHand(handId);
        if (hand == null)
        {
            MessageBox.Show(owner, $"La mano #{handId} no está en la base de datos.", "Vergil HUD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var win = new ReplayerWindow(svc, hand) { Owner = owner };
        win.Closed += (_, _) => Open.Remove(win);
        Open.Add(win);
        win.Show();
        return true;
    }

    /// <summary>Cierra todos los replayers abiertos (al empezar a jugar).</summary>
    public static void CloseAll()
    {
        foreach (var w in Open.ToList()) w.Close();
    }
}
