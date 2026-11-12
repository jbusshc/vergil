using System.Windows;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Ficha de un jugador en una ventana normal (admite teclado), con notas editables.</summary>
public sealed class PlayerCardWindow : Window
{
    public PlayerCardWindow(AppServices svc, string player)
    {
        Title = "Jugador · " + player;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Theme.Bg;
        ResizeMode = ResizeMode.CanMinimize;
        var profile = svc.Profiles.FirstOrDefault() ?? HudProfiles.CreateDefaults().First();
        Content = PlayerPopup.Create(svc, player, profile, 0, editableNotes: true);
    }
}
