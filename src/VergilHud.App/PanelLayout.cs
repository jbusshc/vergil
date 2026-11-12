namespace VergilHud.App;

/// <summary>
/// Dónde se coloca el panel de cada asiento, en fracciones del área de la mesa. Los paneles van dentro del tapete, junto al
/// asiento y sin tapar su nombre ni sus cartas. La posición "visual" 0 es el héroe (abajo) y crece en sentido horario.
/// </summary>
public static class PanelLayout
{
    // Centros del panel para la mesa de 9 asientos de PokerStars, medidos sobre la mesa real.
    static readonly (double X, double Y)[] Nine =
    {
        (0.50, 0.545), // 0 héroe: encima de sus cartas
        (0.32, 0.55),  // 1
        (0.246, 0.44), // 2
        (0.27, 0.31),  // 3
        (0.376, 0.247),// 4
        (0.624, 0.247),// 5
        (0.71, 0.31),  // 6
        (0.754, 0.43), // 7
        (0.69, 0.56),  // 8
    };

    public static (double X, double Y) Anchor(int maxSeats, int visual)
    {
        if (visual == 0) return (0.50, 0.545);
        if (maxSeats == 9 && visual < Nine.Length) return Nine[visual];
        // Resto de tamaños: elipse interior al tapete.
        double angle = Math.PI / 2 + visual * 2 * Math.PI / Math.Max(2, maxSeats);
        return (0.5 + 0.27 * Math.Cos(angle), 0.43 + 0.19 * Math.Sin(angle));
    }

    /// <summary>Tamaño de letra según el ancho de la mesa (entre 9 y 14 pt).</summary>
    public static double FontSize(double widthDip) => Math.Clamp(widthDip / 88.0, 9, 14);
}
