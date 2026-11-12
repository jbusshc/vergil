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
        Add("Leer el HUD", "Asterisco *", "El valor se calculó con manos de fuera del ámbito del perfil (por ejemplo, de cash en una mesa de torneo) o con poca muestra en el stack actual.");
        Add("Leer el HUD", "Modo Honesto / Clásico", "Honesto muestra el valor estimado con intervalo de confianza y barra de fiabilidad. Clásico muestra el porcentaje bruto (aciertos / oportunidades) como los HUD de siempre.");
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
