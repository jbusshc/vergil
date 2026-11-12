using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>
/// Vista de análisis profundo: con mesas abiertas (Modo Juego) solo muestra un aviso (DESIGN.md §2).
/// Se desbloquea sola al cerrar las mesas.
/// </summary>
public abstract class GatedView : ViewBase, IRestrictedDuringPlay
{
    UIElement? _body;
    bool _hooked;

    protected GatedView(AppServices svc) : base(svc) { }

    protected abstract UIElement BuildBody();
    protected abstract void Load();

    public sealed override void Refresh()
    {
        if (!_hooked)
        {
            _hooked = true;
            Svc.Play.Changed += _ => Dispatcher.BeginInvoke(() => { if (IsLoaded) Refresh(); });
        }
        var blocked = Svc.Play.BlockedMessage(this);
        if (blocked != null)
        {
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(Ui.Label("🔒 " + Loc.T(Title), 20, true, null, new Thickness(0, 0, 0, 8)));
            panel.Children.Add(Ui.Label(blocked + ". Es análisis profundo y no se usa mientras juegas.", 14, false, Theme.Muted));
            Content = panel;
            return;
        }
        _body ??= BuildBody();
        Content = _body;
        Load();
    }
}
