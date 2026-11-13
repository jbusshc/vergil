namespace VergilHud.App;

/// <summary>
/// Ayuda en pantalla: qué significa cada dato y cómo leerlo. Se muestra como tooltip al dejar el cursor sobre etiquetas, tarjetas, cabeceras de
/// tablas, botones y estadísticas del HUD, y completa en la vista Ayuda. La clave es el texto visible (o el id de la estadística).
/// </summary>
public static class Help
{
    public sealed record Entry(string Term, string Group, string Text);

    static readonly List<Entry> All = new();
    static readonly Dictionary<string, string> ByKey = new(StringComparer.OrdinalIgnoreCase);

    static void Add(string group, string term, string text, params string[] aliases)
    {
        All.Add(new Entry(term, group, text));
        ByKey[term] = text;
        foreach (var a in aliases) ByKey[a] = text;
    }

    /// <summary>Texto de ayuda de un término, o null si no hay.</summary>
    public static string? For(string? key) => key != null && ByKey.TryGetValue(key.Trim(), out var t) ? t : null;

    public static IReadOnlyList<Entry> Entries => All;

    /// <summary>Ayuda de la etiqueta de tipo de jugador (TAG, LAG?, Fish 2…): qué estilo es y, si lleva «?» o número, qué significa eso.</summary>
    public static string ForBadge(string badge)
    {
        if (string.IsNullOrWhiteSpace(badge)) return "";
        if (badge == "?") return For("?") ?? "";
        bool unsure = badge.EndsWith("?", StringComparison.Ordinal);
        var name = unsure ? badge[..^1] : badge;
        var baseName = System.Text.RegularExpressions.Regex.Replace(name, @" \d+$", "");
        var text = For(baseName) ?? For(name) ?? "";
        var sb = new System.Text.StringBuilder(badge + (text.Length > 0 ? "\n" + text : ""));
        if (baseName != name) sb.Append("\n\n").Append(For("Etiqueta con número (p. ej. TAG 2)"));
        if (unsure) sb.Append("\n\n").Append(For("Etiqueta con «?» (p. ej. LAG?)"));
        sb.Append("\n\nTipo de juego: se calcula con manos del mismo tipo de juego que esta mesa.");
        return sb.ToString();
    }

    /// <summary>Ayuda para el tooltip de una estadística del HUD: su descripción y cómo interpretarla.</summary>
    public static string ForStat(string label, string? description)
    {
        var h = For(label);
        return h == null ? (description ?? label) : (string.IsNullOrEmpty(description) ? h : description + "\n\n" + h);
    }

    static Help()
    {
        // ---------- Cómo leer el HUD ----------
        Add("Leer el HUD", "Barra de fiabilidad", "La barrita bajo cada número indica cuánta muestra hay detrás. Casi vacía = pocas manos, el número es orientativo; llena = muy fiable. En modo Honesto el número ya está corregido hacia la media de la población según la muestra.");
        Add("Leer el HUD", "Flechas ▲▼", "Aparecen cuando las últimas 100 manos del jugador se salen del intervalo de su histórico: ▲ juega más de esa estadística últimamente, ▼ menos. Solo salen con muestra suficiente, para no confundir el azar con un cambio de estilo.");
        Add("Leer el HUD", "Asterisco *", "Con el stack actual de este jugador hay menos de 10 manos, así que el valor que ves es el general (con todos sus stacks) en vez del de este stack.");
        Add("Leer el HUD", "Modo Honesto / Clásico", "Honesto muestra el valor estimado con intervalo de confianza y barra de fiabilidad. Clásico muestra el porcentaje bruto (aciertos / oportunidades) como los HUD de siempre.");
        Add("Leer el HUD", "Panel del jugador", "Arriba: nombre, número de manos (en el ámbito elegido), etiqueta de tipo de jugador (acerca el cursor para ver qué significa), y un puntito de color y ✎ si tienes nota sobre él. Debajo, sus estadísticas. Clic abre su ficha; arrastrar mueve el panel.", "Panel");
        Add("Leer el HUD", "Modo Juego", "Mientras hay mesas abiertas la app solo enseña estadísticas históricas. El replayer, los leaks, el estudio, Nash, la población y demás análisis profundos se desbloquean al cerrar las mesas.");
        Add("Leer el HUD", "FC 35·70", "Fold a C-Bet según el tamaño: el primer número es ante apuestas pequeñas y el segundo ante grandes.");

        // ---------- Estadísticas preflop ----------
        Add("Preflop", "VPIP", "Porcentaje de manos en las que entra voluntariamente al bote (paga o sube). Bajo (<15) = muy selectivo; ~22-28 = normal de regular; >35 = muy suelto.", "vpip");
        Add("Preflop", "PFR", "Porcentaje de manos en las que sube preflop. Un VPIP muy por encima del PFR indica que paga mucho y sube poco (pasivo).", "pfr");
        Add("Preflop", "RFI", "Abrir el bote subiendo cuando nadie ha entrado antes (raise first in), según la posición. Desde el botón es normal abrir mucho más que desde UTG.", "rfi");
        Add("Preflop", "Limp", "Entrar solo pagando la ciega grande sin subir. Frecuente en jugadores débiles.", "limp");
        Add("Preflop", "CC", "Cold call: pagar una subida sin haber puesto antes dinero voluntario.", "cold_call");
        Add("Preflop", "3B", "3-Bet: resubir cuando tiene la oportunidad (alguien ya subió). Un rango normal ronda 5-10 %.", "3bet");
        Add("Preflop", "3B vs steal", "3-Bet desde las ciegas contra un robo (subida desde CO/BTN/SB).", "3bet_vs_steal");
        Add("Preflop", "SQZ", "Squeeze: resubir tras una subida y uno o más pagos.", "squeeze");
        Add("Preflop", "F3B", "Fold a 3-Bet: cuántas veces se retira al ser resubido tras haber abierto. Alto (>65 %) = explotable con 3-Bets; bajo = paga de más.", "f3b");
        Add("Preflop", "4B", "4-Bet: resubir cuando le han hecho 3-Bet.", "4bet");
        Add("Preflop", "F4B", "Fold a 4-Bet tras haber hecho 3-Bet.", "f4b");
        Add("Preflop", "Steal", "Intento de robo de ciegas: abrir desde CO, BTN o SB cuando nadie ha entrado.", "steal");
        Add("Preflop", "FtS", "Fold to steal: se retira desde las ciegas ante un robo. Alto = se puede robar sus ciegas con frecuencia.", "fts");
        Add("Preflop", "BBD", "Defensa de la ciega grande: no se retira ante una subida estando en la ciega grande.", "bb_defend");
        Add("Preflop", "Shv", "Open-shove: empuja all-in como primera acción. Se calcula para su rango de stack actual.", "shove");
        Add("Preflop", "CSh", "Call shove: paga un all-in. Se calcula para su rango de stack actual.", "call_shove");
        Add("Preflop", "Rsh", "Re-shove: empuja all-in sobre una subida anterior.", "reshove");

        // ---------- Postflop ----------
        Add("Postflop", "CB", "C-Bet en flop: apuesta tras haber subido preflop cuando le toca. ~50-70 % es normal.", "cbet_flop");
        Add("Postflop", "CB T", "Segundo barril: apuesta también en el turn tras la c-bet.", "cbet_turn");
        Add("Postflop", "CB R", "Tercer barril: apuesta también en el river.", "cbet_river");
        Add("Postflop", "CB IP", "C-Bet en flop estando en posición.", "cbet_flop_ip");
        Add("Postflop", "CB IP srp", "C-Bet en flop en posición en un bote con una sola subida (sin 3-Bet).", "cbet_flop_ip_srp");
        Add("Postflop", "CB OOP", "C-Bet en flop estando fuera de posición.", "cbet_flop_oop");
        Add("Postflop", "FC", "Fold a C-Bet en flop. Alto = se puede apostar de farol; bajo = hay que apostar más por valor.", "fcb_flop");
        Add("Postflop", "FC T", "Fold a la apuesta del turn.", "fcb_turn");
        Add("Postflop", "FC R", "Fold a la apuesta del river.", "fcb_river");
        Add("Postflop", "FC pq", "Fold a una C-Bet pequeña (menos del 40 % del bote).", "fcb_flop_small");
        Add("Postflop", "FC gr", "Fold a una C-Bet grande (más del 80 % del bote).", "fcb_flop_large");
        Add("Postflop", "F overbet", "Fold a una apuesta mayor que el bote en el river.", "fold_overbet_river");
        Add("Postflop", "R CB", "Sube la C-Bet del rival en el flop.", "raise_cb_flop");
        Add("Postflop", "Donk", "Apuesta fuera de posición al flop contra el agresor preflop.", "donk_flop");
        Add("Postflop", "X/R", "Check-raise en flop: pasa y sube cuando apuestan. Valores muy altos o muy bajos son señales de estilo.", "check_raise_flop");
        Add("Postflop", "X/R T", "Check-raise en turn.", "check_raise_turn");
        Add("Postflop", "Float", "Paga en el flop en posición con intención de apostar después.", "float_flop");
        Add("Postflop", "Probe T", "Apuesta en el turn cuando el agresor preflop pasó en el flop.", "probe_turn");
        Add("Postflop", "Probe R", "Apuesta en el river cuando el agresor pasó en el turn.", "probe_river");
        Add("Postflop", "AF", "Factor de agresión: (apuestas + subidas) / pagos. >2 agresivo; <1 pasivo.", "af");
        Add("Postflop", "AFq", "Frecuencia de agresión: % de acciones postflop que son apuesta o subida (sin contar pasar).", "afq");
        Add("Postflop", "WT", "WTSD: va a showdown cuando ve el flop. ~25-30 % es normal; mucho más = paga demasiado.", "WTSD", "wtsd");
        Add("Postflop", "W$SD", "Gana en el showdown cuando llega. ~50-55 % es normal; muy alto = juega solo manos fuertes.", "wsd");
        Add("Postflop", "WWSF", "Gana la mano cuando ve el flop (con o sin showdown).", "wwsf");
        Add("Postflop", "bb/100", "Ganancia en ciegas grandes por cada 100 manos: la unidad estándar de winrate. En muestras pequeñas es muy ruidoso.", "bb100");

        // ---------- Resultados ----------
        Add("Resultados", "Manos", "Número de manos jugadas en el periodo y formato elegidos.");
        Add("Resultados", "Neto", "Ganancia o pérdida total en ciegas grandes (bb).");
        Add("Resultados", "EV bb/100", "Winrate esperado: sustituye el resultado de los all-in por lo que correspondía según el equity. Si es muy distinto del real, la diferencia es suerte.", "EV de all-ins");
        Add("Resultados", "Rake pagado", "Comisión de la sala que has pagado en esas manos, en bb.");
        Add("Resultados", "Horas jugadas", "Tiempo estimado de juego, sumando las sesiones (pausas de más de 30 minutos separan sesiones).");
        Add("Resultados", "Manos por hora", "Ritmo de juego; depende del número de mesas.");
        Add("Resultados", "Mesas máx.", "Mayor número de mesas distintas con una mano en una ventana de 5 minutos.");
        Add("Resultados", "Resultado real", "Tu ganancia acumulada tal como ocurrió.");
        Add("Resultados", "Banda de varianza", "Zona en la que cae el resultado esperado por pura suerte: si tu línea está dentro, el resultado es compatible con el azar.");
        Add("Resultados", "Neto (bb)", "Resultado en ciegas grandes.");
        Add("Resultados", "EV (bb)", "Resultado esperado en bb, quitando la suerte de los all-in.");
        Add("Resultados", "Manos/h", "Manos por hora de esa sesión.");

        // ---------- Torneos ----------
        Add("Torneos", "ROI", "Retorno de la inversión: (premios − inscripciones) / inscripciones. Con pocos torneos es casi ruido: mira siempre el intervalo del 90 %.");
        Add("Torneos", "ITM", "In The Money: porcentaje de torneos en los que has cobrado premio.");
        Add("Torneos", "Torneos terminados", "Torneos con resultado conocido (puesto final).");
        Add("Torneos", "Beneficio medio", "Ganancia media por torneo.");
        Add("Torneos", "Beneficio total", "Premios menos inscripciones.");
        Add("Torneos", "Multiplicador", "Premio dividido entre la inscripción; en Spin & Go es el premio de la ruleta. Solo se conoce cuando ganas.");
        Add("Torneos", "Puesto", "Posición final en el torneo.");

        // ---------- Leaks, Estudio, Nash ----------
        Add("Análisis", "Leak", "Una estadística tuya que se sale de un rango objetivo con todo su intervalo del 90 %. Si el intervalo toca el rango, no se marca: así se evitan falsas alarmas con pocas manos.");
        Add("Análisis", "Estudio", "Repaso espaciado de tus manos: ves la mano hasta tu decisión, respondes y te puntúas. Las que fallas vuelven antes; las que aciertas, cada vez más tarde.");
        Add("Análisis", "Nash", "Equilibrio de push/fold: con pocas ciegas, con qué manos conviene empujar o pagar para que nadie gane desviándose. Es material de estudio, no un consejo en vivo.");
        Add("Análisis", "Coste (bb)", "Lo que se estima que pierdes, en ciegas grandes, por elegir la acción contraria a Nash en esa mano.");
        Add("Análisis", "Población %", "Frecuencia media de ese spot entre todos tus rivales (sin contarte a ti).");
        Add("Análisis", "Intervalo 95 %", "Rango en el que está con alta probabilidad el valor real. Cuanto más estrecho, más muestra hay detrás.");
        Add("Análisis", "Muestra", "Número de oportunidades en que se calculó el dato.");
        Add("Análisis", "Arquetipo", "Grupo de rivales con un estilo parecido, aprendido de tu propia base de datos. Describe cómo han jugado, no aconseja cómo jugarles.");
        Add("Análisis", "$EV (% del bote)", "Valor esperado de la decisión en premio (ICM), como fracción del bote de premios.");
        Add("Análisis", "EV fichas (bb)", "Valor esperado en fichas de la decisión, en ciegas grandes.");
        Add("Análisis", "Impuesto ICM", "Decisión que gana fichas pero pierde valor en premio, porque las fichas no valen lo mismo cerca del dinero.");
        Add("Análisis", "Stack ef. (bb)", "Stack efectivo: el menor de los dos stacks, en ciegas grandes.");
        Add("Análisis", "Stack (bb)", "Tu stack en ciegas grandes.");

        // ---------- Bienestar y bankroll ----------
        Add("Bienestar", "Tilt", "Puntuación de 0 a 100 de lo alterado que juegas hoy: cuánto se aleja tu estilo del habitual, tus pérdidas, el tiempo sin pausa y las mesas a la vez. Es información sobre ti.");
        Add("Bienestar", "Stop-loss (bb)", "Pérdida de la sesión, en ciegas grandes, a partir de la cual te avisa. Nunca cierra mesas.");
        Add("Bienestar", "Sesión máxima (min)", "Duración de sesión a partir de la cual te avisa.");
        Add("Bienestar", "Pausa cada (min)", "Te recuerda hacer una pausa cada tantos minutos seguidos jugando.");
        Add("Bienestar", "Saldo", "Depósitos − retiradas + rakeback y bonos + resultados de cash y torneos.");
        Add("Bienestar", "Buy-ins (cash)", "Cuántos buy-ins de 100 bb (a tu límite medio) tienes de bankroll.");
        Add("Bienestar", "Buy-ins (torneos)", "Cuántas inscripciones medias tienes de bankroll.");
        Add("Bienestar", "Rakeback y bonos", "Ingresos de la sala fuera del juego; se cuentan aparte del resultado.");
        Add("Bienestar", "Buy-ins mínimos", "Número de buy-ins por debajo del cual la app te avisa.");

        // ---------- Importar y ajustes ----------
        Add("Ajustes", "Héroe siempre abajo", "Gira la mesa para que tu asiento quede abajo, como en la pantalla de PokerStars.");
        Add("Ajustes", "Fijar paneles", "Impide arrastrar los paneles del HUD.");
        Add("Ajustes", "Opacidad", "Transparencia del fondo de los paneles.");
        Add("Ajustes", "Recalcular EV de all-ins y sesiones", "Vuelve a calcular el EV de todos los all-in y a agrupar las sesiones. Úsalo si importaste manos y los números no cuadran.");
        Add("Ajustes", "Copia de seguridad ahora", "Guarda una copia de tu base de datos. La app hace una automáticamente cada día y conserva 5.");
        Add("Ajustes", "Regenerar autonotas", "Vuelve a aplicar las reglas de autonotas a todas las manos.");
        Add("Ajustes", "Reentrenar arquetipos", "Vuelve a agrupar a tus rivales por estilo con los datos actuales.");

        // ---------- Tipos de jugador (la etiqueta junto al nombre en el HUD y la columna Perfil) ----------
        Add("Tipos de jugador", "Qué es la etiqueta", "La etiqueta junto al nombre resume cómo ha jugado ese rival en este tipo de juego, mirando sobre todo VPIP (cuántas manos juega), PFR (cuántas sube) y su agresividad. Es una descripción de lo que ha hecho, no una predicción ni un consejo. Se calcula solo con manos del mismo tipo de juego y del mismo tipo de dinero. Deja el cursor sobre una etiqueta concreta para ver qué significa; la lista completa está en esta sección.", "Perfil", "Tipo de jugador");
        Add("Tipos de jugador", "?", "Todavía sin etiqueta: hay menos de 15 manos de este jugador en este tipo de juego.");
        Add("Tipos de jugador", "Etiqueta con «?» (p. ej. LAG?)", "El sistema duda: el jugador está a medio camino entre dos estilos, o la muestra es corta. Tómala como una pista, no como un hecho.", "LAG?", "TAG?", "Nit?", "Fish?", "Pasivo?", "Regular?", "Tight?", "Maníaco?");
        Add("Tipos de jugador", "Etiqueta con número (p. ej. TAG 2)", "Hay varios grupos de rivales con el mismo nombre; el número solo los distingue (el 1 es el más numeroso). Los datos de cada grupo están en Población › Arquetipos.", "TAG 1", "TAG 2", "TAG 3", "LAG 1", "LAG 2", "Nit 1", "Nit 2", "Fish 1", "Fish 2", "Regular 1", "Regular 2", "Tight 1", "Tight 2");
        Add("Tipos de jugador", "Nit", "Muy selectivo: entra en menos del 15 % de las manos (VPIP ≤ 14 %). Cuando entra, suele tener algo fuerte.");
        Add("Tipos de jugador", "TAG", "Tight-Aggressive: selectivo (VPIP ≤ 26 %) y agresivo; casi todo lo que juega lo sube (VPIP y PFR muy cercanos). Es el estilo regular clásico.");
        Add("Tipos de jugador", "LAG", "Loose-Aggressive: suelto (VPIP > 26 %) y con mucha iniciativa (PFR ≥ 18 %). Juega muchas manos y las sube; su rango es difícil de leer.");
        Add("Tipos de jugador", "Loose-pasivo", "Intermedio: juega bastantes manos pero sube poco; paga más de lo que sube.");
        Add("Tipos de jugador", "Fish pasivo", "Suelto (VPIP ≥ 35 %) y pasivo (factor de agresión < 1,5): entra en muchas manos y paga más de lo que apuesta.");
        Add("Tipos de jugador", "Calling station", "Entra en casi todas las manos (VPIP ≥ 45 %) y casi nunca sube (PFR < 12 %). Paga mucho y se retira poco.");
        Add("Tipos de jugador", "Maníaco", "Muy suelto y muy agresivo: sube y apuesta con muchísimas manos. Juega botes grandes con rangos muy amplios.");
        Add("Tipos de jugador", "Fish", "Entra en muchísimas manos (VPIP ≥ 42 %) y sube menos de un tercio de las veces: paga casi todo y casi nunca toma la iniciativa.");
        Add("Tipos de jugador", "Pasivo", "Suelto (VPIP ≥ 30 %) que sube menos de la mitad de las veces que entra: paga más de lo que sube.");
        Add("Tipos de jugador", "Regular", "Rango medio (VPIP 22-30 %) con bastante cold-call: no sube tanto como un TAG, pero tampoco es suelto.");
        Add("Tipos de jugador", "Tight", "Selectivo (VPIP < 22 %) que mezcla subir y pagar, sin ser tan agresivo como un TAG.");
        Add("Tipos de jugador", "Roca pasiva", "Pocas manos y poca iniciativa: entra poco y sube menos aún.");
        Add("Tipos de jugador", "Arquetipos aprendidos de tus rivales", "Grupos de rivales con un estilo parecido, calculados con tu propia base de datos (separados por tipo de juego). Cada fila muestra cuántos jugadores hay y el centro del grupo: VPIP / PFR / 3B / CB / WTSD.");
        Add("Tipos de jugador", "VPIP/PFR/3B/CB/WTSD", "Los cinco números típicos del grupo, en %: manos que juega, manos que sube, 3-Bet, C-Bet en flop y va a showdown.");

        // ---------- Qué manos cuentan (ámbito) ----------
        Add("Qué manos cuentan", "Tipo de juego", "Cash, Spin & Go y torneos tienen dinámicas muy distintas (stacks, ciegas, presión de ICM), así que sus estadísticas no se mezclan. Elige el tipo de juego para ver solo esas manos.", "Formato", "Ámbito");
        Add("Qué manos cuentan", "Mismo tipo de juego", "Solo cuentan las manos del mismo tipo de juego que la mesa (cash con cash, Spin & Go con Spin & Go, torneos con torneos). Es lo que muestra el HUD por defecto.");
        Add("Qué manos cuentan", "Todo el historial", "Cuentan todas las manos de dinero real, sea cual sea el tipo de juego. Sirve para ver cómo juega una persona en general, pero mezcla dinámicas muy distintas.", "Todo el dinero real", "Todo", "Todos", "Todo (ficticio)");
        Add("Qué manos cuentan", "Esta mesa", "Solo cuentan las manos jugadas en esta mesa (o en este torneo). Útil para ver cómo juega alguien hoy, y cómo estás jugando tú en esta mesa.", "Este torneo", "Solo esta mesa", "Solo este torneo");
        Add("Qué manos cuentan", "Dinero ficticio", "Las manos con fichas de práctica se guardan aparte: nunca se mezclan con las de dinero real en estadísticas, informes ni bankroll. Las mesas de práctica enseñan solo datos de práctica.", "(ficticio)");
        Add("Qué manos cuentan", "Cambiar el ámbito del HUD", "El botón redondo de la esquina de cada mesa («HUD · … ⟳») o Ctrl+Alt+G cambia entre Mismo tipo de juego → Todo el historial → Esta mesa. Afecta a todos los paneles, incluido el tuyo, y a la ficha que se abre al hacer clic en un jugador (donde puedes cambiarlo de nuevo).", "HUD · ⟳");
        Add("Qué manos cuentan", "Cash", "Mesas de cash (no Zoom) en cualquier límite.");
        Add("Qué manos cuentan", "Spin & Go", "Torneos de 3 jugadores con premio aleatorio (la ruleta). Muy poca profundidad de stack y mucho heads-up.");
        Add("Qué manos cuentan", "Torneos MTT/SNG", "Torneos multimesa y Sit & Go de varios jugadores. El valor de las fichas cambia con el ICM y los niveles.", "MTT / SNG", "Torneos MTT / SNG");
        Add("Qué manos cuentan", "Zoom", "Mesas rápidas de PokerStars donde cambias de mesa en cada mano. Sus manos se importan pero no se muestra HUD encima.");

        // ---------- Posiciones ----------
        Add("Posiciones", "UTG", "Under the gun: la primera en hablar preflop. La posición más incómoda: hay mucha gente por hablar detrás.");
        Add("Posiciones", "UTG1", "Segunda posición desde UTG (en mesas de 8-9 jugadores).");
        Add("Posiciones", "UTG2", "Tercera posición desde UTG (en mesas de 9 jugadores).");
        Add("Posiciones", "MP", "Posición media (middle position).");
        Add("Posiciones", "HJ", "Hijack: dos asientos antes del botón.");
        Add("Posiciones", "CO", "Cutoff: el asiento justo antes del botón. Desde aquí se empieza a robar ciegas.");
        Add("Posiciones", "BTN", "Botón: habla la última después del flop. La mejor posición de la mesa.");
        Add("Posiciones", "SB", "Small blind (ciega pequeña): habla la primera después del flop, es decir, siempre sin posición.");
        Add("Posiciones", "BB", "Big blind (ciega grande): cierra la acción preflop, pero habla antes que el resto después del flop.");
        Add("Posiciones", "Posición", "Asiento relativo al botón en esa mano. Cuanto más tarde hablas, más información tienes.");

        // ---------- Fuerza de mano y cómo construyes tus apuestas ----------
        Add("Cómo construyes tus apuestas", "¿Cómo construyes tus apuestas?", "Reparto de tus apuestas postflop según la fuerza de tu mano (solo manos con cartas conocidas): cuántas son por valor y cuántas son faroles o proyectos. Es un resumen de tus manos ya jugadas.");
        Add("Cómo construyes tus apuestas", "Fuerte (set o mejor)", "Set, escalera, color o mejor, o doble pareja que usa tus dos cartas.", "Value");
        Add("Cómo construyes tus apuestas", "Valor medio (par alto o sobrepar)", "Pareja con la carta más alta de la mesa o sobrepar.", "Medium");
        Add("Cómo construyes tus apuestas", "Marginal (par bajo)", "Pareja baja o intermedia, que gana pocas veces contra apuestas grandes.", "Marginal");
        Add("Cómo construyes tus apuestas", "Proyecto", "Proyecto de color o de escalera: aún no es mano hecha.", "Draw");
        Add("Cómo construyes tus apuestas", "Aire", "Sin pareja ni proyecto: la mano solo gana si el rival se retira.", "Air");
        Add("Cómo construyes tus apuestas", "Línea de tiempo", "Barra con los puntos clave de la mano; arrástrala para moverte por las acciones.");

        // ---------- Controles y columnas ----------
        Add("Tablas", "Mano", "Número de mano de PokerStars. Doble clic abre el replayer (con mesas cerradas).");
        Add("Tablas", "Fecha", "Cuándo se jugó (hora local).", "Inicio");
        Add("Tablas", "Cartas", "Tus cartas en esa mano (o las del rival, si las mostró).");
        Add("Tablas", "Jugador", "Nombre del jugador en PokerStars.");
        Add("Tablas", "Rival", "Contra quién fue el all-in.");
        Add("Tablas", "Equity", "Probabilidad de ganar la mano en el momento del all-in, según las cartas de todos.");
        Add("Tablas", "Clase", "Tu mano agrupada por tipo, p. ej. AKs = As-Rey del mismo palo, 72o = siete-dos de distinto palo, JJ = pareja de jotas.");
        Add("Tablas", "Spot", "La situación concreta de la mano (posición, acción previa y stack).");
        Add("Tablas", "Grupo", "Cada fila de este informe es un grupo de manos (una posición, un límite, una hora…).");
        Add("Tablas", "Beneficio", "Premio menos inscripción de ese torneo.");
        Add("Tablas", "Premio", "Lo que cobraste en ese torneo (0 si no entraste en premios).");
        Add("Tablas", "Inscripción", "Lo que costó entrar al torneo (buy-in más cuota).");
        Add("Tablas", "Duración", "Cuánto duró la sesión de juego.");
        Add("Tablas", "Formato", "Tipo de juego de la mano: cash, mtt, sng, spin o zoom.");
        Add("Tablas", "Etiquetas", "Marcas que pones a tus manos para encontrarlas luego (p. ej. «dudosa» o «bluff»).", "Etiquetas ▾", "Etiqueta");
        Add("Tablas", "Cartas · Línea · bb", "En Showdowns: las cartas que mostró, lo que hizo calle a calle (X = check, C = call, F = fold, R = raise, B n % = apuesta de n % del bote) y lo que ganó o perdió en bb.");
        Add("Tablas", "Beneficio acumulado", "Tu resultado sumado a lo largo del tiempo.");
        Add("Tablas", "Resultado acumulado (real y esperado)", "Tu ganancia acumulada real junto a la esperada (sin la suerte de los all-in). Si la real se aleja mucho de la esperada, la diferencia es varianza.");
        Add("Tablas", "Últimas 30 sesiones", "Resultado de tus últimas 30 sesiones de juego.");
        Add("Tablas", "Tilt de la sesión actual", "Cuán alterado vas jugando en la sesión de hoy (0-100). Mira la entrada Tilt para ver qué incluye.");
        Add("Tablas", "Resultado cash", "Ganancia o pérdida total de las manos de cash, en dinero.");
        Add("Tablas", "Resultado torneos", "Premios menos inscripciones de torneos terminados.");
        Add("Tablas", "Premios (% de 1.º, 2.º, 3.º…)", "Reparto del premio entre los puestos pagados, en % del bote de premios.");

        Add("Controles", "Periodo", "Intervalo de fechas que se analiza: hoy, 7 o 30 días, este año o todo.");
        Add("Controles", "Agrupar por", "Elige cómo se reparten las manos en el informe: por posición, stack, límite, hora, día de la semana o formato.");
        Add("Controles", "Colorear por", "Qué se pinta en el mapa de manos de salida: tu resultado, cuántas veces juegas esa mano o su frecuencia.");
        Add("Controles", "Valores:", "Honesto = valor corregido con la media de la población y barra de fiabilidad; Clásico = porcentaje en bruto.");
        Add("Controles", "Con showdown", "Resultado solo de las manos en que se llegó a ver cartas.");
        Add("Controles", "Sin showdown", "Resultado de las manos que terminaron sin enseñar cartas (retiradas y botes ganados con apuestas).");
        Add("Controles", "Solo showdown", "Muestra solo las manos que llegaron a enseñarse.");
        Add("Controles", "Exportar CSV", "Guarda la tabla en un archivo CSV para abrirlo en Excel u otro programa.", "Exportar manos (CSV)", "Exportar matriz (CSV)");
        Add("Controles", "Exportar PNG", "Guarda la gráfica como imagen.");
        Add("Controles", "Copiar texto", "Copia el texto de la mano al portapapeles.");
        Add("Controles", "Buscar jugador", "Escribe parte del nombre para filtrar la lista.", "Buscar");
        Add("Controles", "Mín. manos", "Oculta jugadores con menos manos que este número.");
        Add("Controles", "Ver manos", "Abre la lista de las manos que forman este dato, para repasarlas.", "ver mano");
        Add("Controles", "Abrir en el replayer", "Abre la mano para repasarla paso a paso. Solo está disponible cuando no hay mesas abiertas.");
        Add("Controles", "Analizar", "Lanza el análisis de la sección actual. Puede tardar unos segundos con muchas manos.", "Recalcular", "Calcular todas las tablas");
        Add("Controles", "Análisis ICM…", "Revisa tus all-in de torneo teniendo en cuenta el valor del premio: una decisión que gana fichas puede perder dinero cerca de la burbuja. Es una aproximación para estudiar.");
        Add("Controles", "Marcar para estudiar", "Añade esta mano a tu cola de estudio para repasarla más adelante.", "Buscar manos para estudiar", "Enviar desviaciones al Estudio", "Enviar todas al Estudio", "Proponer las 5 manos de hoy");
        Add("Controles", "Empezar repaso", "Empieza una sesión de repaso con las manos pendientes de hoy.");
        Add("Controles", "Revelar lo que hiciste", "Enseña la decisión que tomaste en esa mano, para compararla con lo que habías pensado.");
        Add("Controles", "¿Cómo lo valoras?", "Puntúa tu decisión: Mal vuelve a salir pronto, Dudoso en unos días y Bien se aleja cada vez más.", "Bien", "Dudoso", "Mal");
        Add("Controles", "Repasos de estudio", "Cuántas manos has repasado en el periodo.");
        Add("Controles", "Nota", "Texto libre que guardas sobre una mano o un jugador.", "Notas");
        Add("Controles", "Ánimo (1-5)", "Cómo te sientes al empezar o terminar (1 = muy mal, 5 = muy bien). Se cruza con tus resultados en Bienestar.", "Cansancio (1-5)", "Registrar al empezar", "Registrar al terminar");
        Add("Controles", "Diario y correlaciones", "Tu ánimo y cansancio apuntados antes y después de jugar, y qué relación tienen con tus resultados (con intervalos; hacen falta al menos 5 sesiones por grupo).");
        Add("Controles", "Importe", "Cantidad del movimiento de bankroll.", "Tipo de movimiento", "Añadir movimiento");
        Add("Controles", "Objetivos semanales", "Metas de la semana (manos, torneos, repasos de estudio…). Se miden solos con tus datos.", "Objetivos de esta semana", "Guardar objetivos");
        Add("Controles", "Abrir informe semanal", "Abre el resumen HTML de tu última semana de juego.");
        Add("Controles", "Pruebas de rendimiento", "Mide la velocidad de la app en tu equipo (importación, estadísticas, evaluador de manos).");
        Add("Controles", "Idioma", "Idioma de la interfaz. Se aplica al reiniciar.");
        Add("Controles", "Tema", "Claro u oscuro. Se aplica al reiniciar.");
        Add("Controles", "Editor de HUD…", "Elige qué estadísticas y en qué líneas se ven en los paneles de cada tipo de mesa.", "Perfil de HUD", "Se usa en:", "Asientos mín.", "Líneas del panel (de arriba abajo)", "Línea nueva", "Borrar línea", "Subir", "Bajar", "Quitar", "Añadir", "Guardar y aplicar", "Restablecer perfiles de serie");
        Add("Controles", "Ventana (HUD)", "Qué manos usa el panel: todo el historial, las últimas 100 manos o solo la sesión actual.", "Ventana", "Datos");
        Add("Controles", "Editor de estadísticas…", "Crea tus propias estadísticas: un numerador (lo que cuentas) entre un denominador (las oportunidades).", "Nombre", "Numerador (expresión sobre los hechos; vale 1 o 0 por mano)", "Denominador (oportunidades; por defecto 1 = todas las manos)", "Etiqueta corta (la del panel)", "Descripción", "Probar", "Nueva", "Borrar", "Guardar");
        Add("Controles", "Asistente de configuración…", "Revisa que la carpeta de historiales y los ajustes de PokerStars estén bien.");
        Add("Controles", "HUD en vivo en:", "En qué tipos de juego se dibuja el HUD sobre las mesas. Comprueba siempre qué herramientas permite PokerStars en cada formato.", "HUD en mesa");
        Add("Controles", "Mostrar mi panel", "Dibuja también tu propio panel de estadísticas en la mesa.");
        Add("Controles", "Mostrar el tilt en la barra de estado", "Muestra tu puntuación de tilt de hoy en la barra de estado y en el panel de héroe.");
        Add("Controles", "Importar historial antiguo", "Lee de golpe todos los historiales que haya en la carpeta. Puedes cancelar y reanudar: lo importado se conserva.", "Importar", "Importar historial antiguo…", "Examinar…", "Iniciar", "Historiales de manos");
        Add("Controles", "Exportar configuración…", "Guarda tus ajustes, perfiles de HUD, estadísticas propias y objetivos en un archivo; sirve para pasarlos a otro equipo.", "Importar configuración…", "Herramientas y copias");
        Add("Controles", "Detenido", "El vigilante de la carpeta de historiales no está activo.");
        Add("Controles", "Color:", "Marca de color que ves como puntito en el panel del jugador.");
        Add("Controles", "Autonotas (observaciones automáticas de sus manos)", "Observaciones que la app anota sola cuando ve algo llamativo en sus manos. Son hechos de manos pasadas; puedes borrarlas.");
        Add("Controles", "Tendencias de la población (todos tus rivales, sin ti)", "Cómo juega el rival medio en cada situación, con intervalo, y si tú juegas distinto (▲ más, ▼ menos).");
        Add("Controles", "Tus desviaciones (Spin y torneos cortos de stack)", "Manos en que tu decisión de empujar o pagar fue la contraria al equilibrio de Nash, con una estimación de lo que cuesta, en bb.");
        Add("Controles", "3Bet", "Porcentaje de veces que resube cuando tiene la oportunidad.");
        Add("Controles", "FCB", "Fold a C-Bet en flop.");
        Add("Controles", "WTSD", "Va a showdown cuando ve el flop.");

        Add("Controles", "Juego", "Qué situación se muestra: heads-up (2 jugadores) o 3 jugadores como en un Spin & Go.");
        Add("Controles", "Rol", "Qué decisión se muestra: quién empuja all-in (p. ej. «BTN empuja») o quién paga y contra quién (p. ej. «SB paga al BTN»). Cada una tiene su propio rango de equilibrio.");
        Add("Controles", "Tipo", "Filtra por tipo de torneo (Spin & Go, MTT, SNG). En Bankroll, es el tipo de movimiento (depósito, retirada, rakeback, bono).");
        Add("Controles", "Tú", "Lo que hiciste tú en ese spot, para compararlo con la población.");
        Add("Controles", "Guardar nota", "Guarda la nota de este jugador. Se ve como ✎ en su panel del HUD.");
        Add("Controles", "Guardar límites", "Guarda tu stop-loss, duración máxima y pausas. Los avisos nunca cierran mesas.");
        Add("Controles", "Separador antes", "Texto que se pone entre la estadística anterior y esta en el panel (por ejemplo «/» entre VPIP y PFR).");
        Add("Controles", "máx.", "Número máximo de asientos de la mesa para el que sirve este perfil de HUD.");
        Add("Controles", "ALL-IN", "El jugador ha puesto todas sus fichas.");
        Add("Controles", "Cerrar", "Cierra esta ventana.");
        Add("Controles", "lento", "Velocidad de reproducción de la mano en el replayer.", "rápido");
        Add("Controles", "▶ Reproducir", "Reproduce la mano acción por acción (con los botones ◀ y ▶ avanzas a mano).", "▶", "◀", "⏮", "⏭", "⏸ Pausar");

        // ---------- Secciones del menú ----------
        Add("Menú", "Dashboard", "Resumen de resultados, gráfica de ganancias y últimas sesiones.");
        Add("Menú", "Jugadores", "Estadísticas de todos los jugadores de tu base de datos. Doble clic abre su ficha con notas.");
        Add("Menú", "Manos", "Lista de tus manos con filtros. Doble clic abre el replayer.");
        Add("Menú", "Informes", "Resultados agrupados por posición, stack, hora, etc., y matriz de manos de salida.");
        Add("Menú", "Gráficas", "Ganancias con banda de varianza, por hora, por día y por sesión.");
        Add("Menú", "Sesiones", "Tus sesiones de juego con resultado, ritmo y tilt.");
        Add("Menú", "Torneos", "Resultados de MTT, SNG y Spin & Go con ROI e intervalo.");
        Add("Menú", "Leaks", "Estadísticas tuyas fuera de rango y cómo construyes tus apuestas.");
        Add("Menú", "Población", "Cómo juega el rival medio y qué arquetipos hay en tu base de datos.");
        Add("Menú", "Bienestar", "Tilt, límites, diario y objetivos.");
        Add("Menú", "Bankroll", "Saldo, buy-ins disponibles y riesgo de ruina.");
        Add("Menú", "Ajustes", "Carpeta de historiales, opciones del HUD, copias, idioma y tema.");
        Add("Menú", "Ayuda", "Glosario de todos los datos y cómo interpretarlos.");
    }
}
