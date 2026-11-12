namespace VergilHud.App;

/// <summary>
/// Interfaz en español (por defecto) o inglés. Los textos de la interfaz están escritos en español y se traducen por coincidencia exacta al
/// dibujar etiquetas, botones y menús; los mensajes con cifras dinámicas se quedan en español. El idioma se elige en Ajustes y se aplica al reiniciar.
/// </summary>
public static class Loc
{
    public static string Language { get; set; } = "es";

    public static string T(string es) => Language == "en" && En.TryGetValue(es, out var t) ? t : es;

    static readonly Dictionary<string, string> En = new()
    {
        // Navegación
        ["Dashboard"] = "Dashboard", ["Jugadores"] = "Players", ["Manos"] = "Hands", ["Informes"] = "Reports", ["Gráficas"] = "Charts",
        ["Sesiones"] = "Sessions", ["Torneos"] = "Tournaments", ["Leaks"] = "Leaks", ["Estudio"] = "Study", ["Nash"] = "Nash",
        ["Población"] = "Population", ["Bienestar"] = "Wellbeing", ["Bankroll"] = "Bankroll", ["Ayuda"] = "Help", ["Ajustes"] = "Settings",
        // Botones y etiquetas
        ["Abrir en el replayer"] = "Open in replayer", ["Agrupar por"] = "Group by", ["Analizar"] = "Analyze", ["Análisis ICM…"] = "ICM analysis…",
        ["Arquetipos aprendidos de tus rivales"] = "Archetypes learned from your opponents", ["Asistente de configuración…"] = "Setup wizard…",
        ["Autonotas (observaciones automáticas de sus manos)"] = "Auto-notes (automatic observations of their hands)",
        ["Banda de varianza"] = "Variance band", ["Bien"] = "Good", ["Buscar"] = "Search", ["Buscar jugador"] = "Search player",
        ["Buscar manos para estudiar"] = "Find hands to study", ["Calcular todas las tablas"] = "Compute all charts", ["Cerrar"] = "Close",
        ["Color:"] = "Color:", ["Colorear por"] = "Color by", ["Con showdown"] = "With showdown", ["Copiar texto"] = "Copy text",
        ["Detenido"] = "Stopped", ["Dudoso"] = "Doubtful", ["EV de all-ins"] = "All-in EV", ["Empezar repaso"] = "Start review",
        ["Enviar desviaciones al Estudio"] = "Send deviations to Study", ["Enviar todas al Estudio"] = "Send all to Study",
        ["Equity de los jugadores que siguen en la mano"] = "Equity of the players still in the hand", ["Etiqueta"] = "Tag",
        ["Etiquetas ▾"] = "Tags ▾", ["Examinar…"] = "Browse…", ["Exportar CSV"] = "Export CSV", ["Exportar PNG"] = "Export PNG",
        ["Exportar manos (CSV)"] = "Export hands (CSV)", ["Exportar matriz (CSV)"] = "Export matrix (CSV)", ["Fijar paneles"] = "Lock panels",
        ["Formato"] = "Format", ["Guardar nota"] = "Save note", ["HUD en vivo en:"] = "Live HUD in:", ["Héroe siempre abajo"] = "Hero always at the bottom",
        ["Importar"] = "Import", ["Importar historial antiguo"] = "Import old history", ["Importar historial antiguo…"] = "Import old history…",
        ["Iniciar"] = "Start", ["Juego"] = "Game", ["Listo para importar."] = "Ready to import.", ["Línea de tiempo"] = "Timeline",
        ["Mal"] = "Bad", ["Marcar para estudiar"] = "Mark for study", ["Mostrar mi panel"] = "Show my panel", ["Mín. manos"] = "Min. hands",
        ["Opacidad"] = "Opacity", ["Periodo"] = "Period", ["Proponer las 5 manos de hoy"] = "Suggest today's 5 hands", ["Recalcular"] = "Recalculate",
        ["Recalcular EV de all-ins y sesiones"] = "Recalculate all-in EV and sessions", ["Reentrenar arquetipos"] = "Retrain archetypes",
        ["Regenerar autonotas"] = "Regenerate auto-notes", ["Resultado real"] = "Actual result", ["Revelar lo que hiciste"] = "Reveal what you did",
        ["Rol"] = "Role", ["Sin datos todavía"] = "No data yet", ["Sin showdown"] = "Without showdown", ["Solo showdown"] = "Showdown only",
        ["Stack (bb)"] = "Stack (bb)", ["Tipo"] = "Type", ["Valores:"] = "Values:", ["Ver manos"] = "See hands", ["Zoom"] = "Zoom",
        ["lento"] = "slow", ["rápido"] = "fast", ["ver mano"] = "see hand", ["¿Cómo lo valoras?"] = "How do you rate it?",
        ["¿Cómo construyes tus apuestas?"] = "How do you build your bets?", ["▶ Reproducir"] = "▶ Play", ["⏸ Pausar"] = "⏸ Pause",
        ["Todavía no hay observaciones."] = "No observations yet.", ["Tus desviaciones (Spin y torneos cortos de stack)"] = "Your deviations (Spin and short-stack tournaments)",
        ["Tendencias de la población (todos tus rivales, sin ti)"] = "Population trends (all your opponents, excluding you)",
        ["Premios (% de 1.º, 2.º, 3.º…)"] = "Prizes (% for 1st, 2nd, 3rd…)",
        ["Para editar la nota abre al jugador desde la pestaña Jugadores (doble clic)."] = "To edit the note open the player from the Players tab (double click).",
        ["Aún no hay apuestas postflop tuyas con cartas conocidas."] = "No postflop bets of yours with known cards yet.",
        // Bienestar, bankroll y editores
        ["Ánimo (1-5)"] = "Mood (1-5)", ["Cansancio (1-5)"] = "Fatigue (1-5)", ["Nota"] = "Note", ["Registrar al empezar"] = "Log at start", ["Registrar al terminar"] = "Log at end",
        ["Límites que defines tú (avisan, nunca cierran mesas)"] = "Limits you define (they warn, never close tables)",
        ["Stop-loss (bb)"] = "Stop-loss (bb)", ["Sesión máxima (min)"] = "Max session (min)", ["Pausa cada (min)"] = "Break every (min)", ["Guardar límites"] = "Save limits",
        ["Diario y correlaciones"] = "Journal and correlations", ["Añadir movimiento"] = "Add transaction", ["Importe"] = "Amount", ["Tipo de movimiento"] = "Transaction type",
        ["Buy-ins mínimos"] = "Minimum buy-ins", ["Objetivos semanales"] = "Weekly goals", ["Guardar objetivos"] = "Save goals",
        ["Abrir informe semanal"] = "Open weekly report", ["Copia de seguridad ahora"] = "Back up now", ["Exportar configuración…"] = "Export settings…",
        ["Importar configuración…"] = "Import settings…", ["Pruebas de rendimiento"] = "Performance tests", ["Idioma"] = "Language", ["Tema"] = "Theme",
        ["Editor de estadísticas…"] = "Stats editor…", ["Editor de HUD…"] = "HUD editor…", ["Guardar"] = "Save", ["Probar"] = "Test", ["Nueva"] = "New", ["Borrar"] = "Delete",
        ["Se aplica al reiniciar la aplicación."] = "Applies when the app restarts.",
    };
}
