# Decisiones de implementación

Cambios respecto a `DESIGN.md` y por qué. Los números de sección son los de ese documento.

## Fase 2

1. **Los golden tests son JSON parciales escritos a mano, no generados (§6.1, §16).**
   El diseño pedía `.facts.json` completos y un comando para regenerarlos. Un JSON generado por el propio extractor solo comprueba
   que no cambia, no que sea correcto. Cada `cases/*.json` lista únicamente los hechos que importan en ese caso y sus valores se
   calcularon a mano desde las reglas del juego. Por eso no existe `RegenerateGolden`. Resultado: 40 casos (30 cash + 10 torneo) y,
   en el primer intento, 39 coincidieron; el que falló era un error de la expectativa escrita a mano, no del extractor.
2. **Se añadieron invariantes sobre todas las manos (§16).** Además del golden: la suma de `net` + rake = 0, "si ocurre X hubo
   oportunidad de X", `wsd ⇒ wtsd ⇒ f_saw`, etc. Se ejecutan también sobre las manos reales anonimizadas (`Fixtures/real/`).
3. **`StatsCalculator` (Fase 1) se conserva solo como oráculo de pruebas.** Un test compara VPIP/PFR/3-Bet/C-Bet calculados con
   el DSL contra ese cálculo independiente sobre todos los fixtures. Nada de la app lo usa ya.
4. **El DSL se amplió un poco (§6.2).** Se añadieron `+ - *`, los literales `true`/`false`, números negativos, `in` con números y
   el campo `scale` de la definición. Hacen falta para AF `(bets+raises)/calls`, AFq y bb/100. No hay división: evita divisiones
   por cero dentro del SQL; el cociente numerador/denominador se hace fuera, donde se controla el denominador 0.
   Los literales siguen yendo siempre como parámetros.
5. **No se movieron los ficheros a las carpetas `Parsing/ Domain/ Import/` (§4).** Todo sigue en el espacio de nombres
   `VergilHud.Core`; solo `Facts/`, `Stats/` y `Data/` son carpetas nuevas. Mover archivos no aporta nada hasta que haya un segundo
   parser (Winamax/GG).
6. **`SNG` y `MTT` no se distinguen con solo la mano.** El historial no dice cuántos participantes hay. `format` es `cash`, `zoom`,
   `spin` (torneo en mesa de 3) o `mtt` (cualquier otro torneo). Se afinará con los Tournament Summaries (Fase 4).
7. **`pf_call_shove` no se limita a la primera acción del jugador.** Un jugador que sube y luego paga un all-in también tuvo esa
   oportunidad. Lo destapó diseñar el caso `cash_16`.
8. **Un jugador all-in no cuenta como "agresor que aún no ha actuado".** Sin esa regla, en un bote con side pot cada apuesta
   postflop de los demás se marcaba como donk. Lo destapó el caso `cash_17`.
9. **Un jugador `sitting out` que pone ciegas sí juega la mano.** En torneos, un rival desconectado sigue poniendo ciegas y se retira
   solo. Descartarlo hacía desaparecer su dinero (13 de 27 manos reales descuadradas antes del arreglo).
10. **Los historiales de PokerStars llevan BOM UTF-8.** El importador lo salta; antes perdía la primera mano de cada archivo.
    Se detectó al ejecutar la app real, no con los tests unitarios (`File.ReadAllText` quita el BOM sin avisar), por eso hay un
    test de regresión específico.
11. **`Hand.Raw` se guarda recortado** (sin las líneas en blanco finales). La migración v1→v2 reimporta con `Parse` (una mano),
    no con `ParseAll`, que exige la cola de líneas en blanco para dar la mano por completa.
12. **El HUD y la ventana principal usaban la consulta heredada** (`GetPlayerStats`), ahora calculada sobre
    `player_hand_facts`. `StatEngine` + `StatCache` estaban listos y probados; el HUD pasó a usarlos en la Fase 3 (la tabla de
    jugadores de la ventana principal sigue con la consulta heredada).

## Pendiente de medir

Los objetivos de rendimiento de §14 (2.000 manos/s, < 50 ms de consulta con 1 M de manos) todavía no se han medido; llegan con
los benchmarks de la Fase 8. Con las 27 manos reales la importación y las consultas son instantáneas.

## Fase 3

1. **La fiabilidad se calcula solo con los datos del jugador, no con el prior (§6.3).** El diseño decía "ancho del intervalo →
   fiabilidad". Con el intervalo posterior, un prior fuerte (p. ej. VPIP con fuerza 20) hacía que un jugador de 0 manos pareciera
   49 % fiable. Ahora la fiabilidad usa un intervalo con prior uniforme (`Beta(1+x, 1+n−x)`): 0 manos → 0, 2 manos → ~0,
   100 manos → ~0,77, 300 → ~0,86. El **valor mostrado** y el **intervalo del popup** sí usan el prior.
2. **Las flechas de cambio de estilo comparan contra el intervalo del histórico basado solo en datos, al 95 % (§6.4).**
   La primera versión comparaba el valor reciente en crudo con el intervalo posterior del total: como el posterior se encoge hacia
   la población, un jugador que siempre hacía lo mismo (VPIP 100 %) recibía una flecha ↑ falsa. Detectado por una prueba.
   Se exige además total ≥ 50 manos y reciente ≥ 20.
3. **Prior = población de tu base de datos, sin el héroe, por formato, mezclada con una media de partida (§6.3).**
   `μ = (x_pob + 200·μ_defecto) / (n_pob + 200)`. Con una base casi vacía manda la media de partida (definida en cada estadística);
   con miles de manos manda la población real. Se agrupa por formato, no por límite (el diseño decía "mismo límite y formato");
   hasta que haya datos de varios límites no aporta nada. Se recalcula cada 1.000 manos importadas.
4. **El perfil se elige por formato y tamaño de mesa, y un mano a mano en una mesa de más asientos cuenta como mesa de 2.**
   PokerStars sirve el HU de cash como mesa de 6 con 2 jugadores cuando los demás se levantan. Un formato sin perfil propio
   (zoom) usa el de cash que encaja con la mesa.
5. **El ámbito de datos del perfil (cash / torneo) cae a "todo" con asterisco si hay < 30 manos (§13b).** Un panel de torneo de
   alguien al que solo has visto en cash no se queda vacío, pero se marca para avisar de que mezcla formatos.
6. **"Según stack" y "pq·gr".** Las estadísticas de push/fold se calculan con el cubo de stack actual del jugador (mín. 10 en el
   cubo; si no, global con asterisco). `FC pq·gr` exige ≥ 8 oportunidades de cada tamaño; si no, muestra el valor global.
7. **Ventana "sesión" = desde la última pausa de más de 30 minutos entre manos del héroe**, no "hoy", para que coincida con la
   definición de sesión de la Fase 4. Se elige editando `hud-profiles.json` (`"window": "session" | "last100" | "total"`);
   no hay selector en la interfaz todavía (llega con el editor visual de la Fase 8).
8. **Todos los nicks con los que sale "Dealt to" se unifican como el héroe (§19.10).** Solo el dueño del historial tiene cartas
   en "Dealt to", así que no hace falta configurarlo. El popup y el panel suman los nicks.
9. **Colocación de los paneles.** Los centros de la mesa de 9 asientos se midieron sobre una mesa real de PokerStars y los paneles
   van dentro del tapete, sin tapar nombres ni cartas. Para 6, 3 o 2 asientos se usa una elipse interior **sin medir**: puede
   necesitar ajustes (se corrige arrastrando; la posición se guarda por tamaño de mesa).
10. **Posicionamiento con `SetWindowPos` en píxeles físicos + manifiesto PerMonitorV2 (§7.3).** Evita convertir DIPs a mano y
    funciona con monitores de distinta escala. La letra se escala con el ancho de la mesa (9–14 pt).
11. **El popup se cierra al hacer clic fuera o al empezar otra mano, pero no con Esc.** El overlay no puede recibir foco (si no,
    robaría el foco de la mesa y el teclado dejaría de ir a PokerStars). Es una limitación asumida.
12. **El Modo Juego se activa por ventanas de mesa de PokerStars** (clase `GLFW30`, proceso `PokerStars*`, no el lobby), también
    sin mano jugada todavía. Todavía no hay vistas restringidas que bloquear (llegan con el replayer, los leaks y el estudio);
    el banner y la interfaz `IRestrictedDuringPlay` ya están.
13. **`VERGIL_DATA_DIR`** redirige dónde guarda sus datos la app (pruebas y uso portátil). Y `VergilHud.exe --render <db> <carpeta>`
    dibuja los paneles y los popups a PNG sin abrir ventanas, para verificar el aspecto.

### Sin verificar todavía
- ~~Mesas de torneo / Spin & Go en vivo~~ **Verificado (2026-11-11):** el título es `PM 10000.00 NLHE Spin & Go - 10/20 - Tournament 4046692840 Table 1 - Logged In as <nick>` y el HUD lo reconoce por el id del torneo.
- ~~Colocación del overlay en vivo~~ **Verificado** en una mesa de cash (9 asientos con 2 jugadores, perfil de mano a mano elegido solo) y en un Spin & Go. Las mesas de 6 asientos y de 3 con rivales sentados siguen sin medirse.

## Fase 4

1. **Los resultados de torneo salen de los historiales, no de los Tournament Summaries (§13b).** En tu equipo no hay resúmenes y no
   tengo ninguno para validar un parser. Las manos ya traen lo necesario: `X finished the tournament in Nth place [and received $N]`
   y `X wins the tournament and receives N`. Con eso salen puesto, premio, ROI e ITM. **Limitación:** el multiplicador de un Spin solo
   se conoce cuando ganas (premio / inscripción); el historial no dice qué premio había en las derrotas. El tamaño del torneo en
   MTT/SNG tampoco se sabe (`entrants` solo se rellena en Spin: 3). Si algún día aparecen resúmenes, se pueden añadir sin cambiar el ROI.
2. **ROI con intervalo del 90 % por el método delta y la t de Student, no la normal (§18.3).** Un cociente de sumas (Σ ganancia / Σ
   inscripciones). La primera versión usaba la normal y con tus 2 Spins ganados (x2 y x3) dijo *"ROI positivo con un 90 % de
   confianza: +150 %"*: falso. Con t de Student (6,3 con 2 torneos, ~1,65 con cientos) el intervalo es enorme y el informe dice que
   no se puede distinguir de 0 %. El "cuántos torneos harían falta" usa la normal (es una estimación para muestras grandes).
3. **EV de all-in (§6.5).** Se calcula cuando (a) hay showdown, (b) tras el último all-in no hay más acción que respuestas
   (call/fold) en esa misma calle, (c) no es en el river, y (d) se ven las cartas de todos los que siguen. El bote se reparte
   por capas (side pots): cada capa usa el equity de quienes siguen vivos en ella, y el rake se reparte proporcionalmente.
   Una mano con all-in preflop en la que los demás siguen apostando después (side pot con juego posterior) **no** entra: no es un
   all-in "limpio". Hasta que se calcula, `ev_net_bb` vale lo mismo que el resultado real, así que las sumas siempre cuadran.
4. **Equity exacto solo hasta 100.000 tableros; si no, simulación con semilla fija de 100.000 tiradas (error típico ±0,15 %).**
   Enumerar el preflop exacto (1,7 M tableros) tardaba ~0,65 s por mano y una prueba con 700 manos, 55 s. Todo lo postflop sigue exacto.
5. **Sesiones (§8.5):** una pausa de más de 30 minutos abre una sesión nueva. `tables_max` = mesas distintas con una mano en una
   ventana de 5 minutos. `tilt_score` queda en NULL hasta la Fase 8. Las horas jugadas del Dashboard suman la duración de las sesiones.
6. **Cómo se llega a < 1 s con 100.000 manos (§14):** índice parcial **de cobertura** solo para las filas del héroe
   (`ix_facts_hero_time`, con las columnas que leen el Dashboard, los informes y la matriz): las consultas no tocan la tabla de 144
   columnas. Medido: 415–650 ms (la primera versión, sin cobertura, 807–1.942 ms). La prueba corre sola, sin paralelismo.
7. **Importación masiva (§13) y su velocidad.** Medido en Release: **2.000–3.300 manos/s** según la carga de la máquina (el objetivo era
   ≥ 2.000: se cumple con poco margen). La prueba automática es una guarda de regresión (≥ 1.200), no una promesa. Lo que dio el salto
   de 800 a 2.000+: enlazar ~146 parámetros por fila era cuadrático (SQLite busca cada nombre recorriendo la lista), así que cada fila
   solo envía las columnas con valor (~25-40) con sentencias preparadas y reutilizadas. Intentos descartados: SQL con literales
   (más lento: compilar una sentencia por fila), `Prepare()` solo (poco efecto). Se quitan los índices durante la importación y se
   recrean al final (también al abrir, por si se interrumpió). Durante una importación masiva no se avisa al HUD de cada mano antigua.
8. **El evaluador de manos hace ~6,7 M de evaluaciones/s en Release; el diseño pedía 50 M/s.** Basta para el EV (en segundo plano),
   pero no se cumple el objetivo: queda como mejora para los benchmarks de la Fase 8 (tabla de consulta en lugar de contar rangos).
9. **Las vistas se arman en código, con la lógica en Core (probada), y no con MVVM + CommunityToolkit (§4).** Así también se pueden
   dibujar fuera de pantalla (`--render-analysis`) para verificarlas. Es una desviación del diseño.
10. **Del diseño que no se hizo:** la comparación del multiplicador real con la distribución teórica de cada stake (no hay una tabla
    oficial fiable a mano), la exportación a PNG de las gráficas (hay CSV de informes, matriz y manos) y la lista de manos con clic desde
    las gráficas. La matriz sí permite ver las manos de cada casilla.
11. **Fechas:** los periodos "Hoy / 7 días / 30 días / Este año" comparan con la hora UTC. Las manos se guardan con la hora que escribe
    el cliente en la cabecera (en tu caso UTC); un cliente con otra zona horaria podría desplazarlas unas horas.
12. **Mantenimiento en segundo plano:** tras importar, un temporizador (5 s, agrupando avisos) recalcula el EV de los all-ins y las
    sesiones fuera del hilo del HUD. Se puede forzar desde Ajustes.
13. **Argumentos de la app para verificar la interfaz:** `--view N` abre una vista concreta (0 = Dashboard … 6 = Ajustes) y
    `--render-analysis <carpeta>` dibuja todas las vistas con datos de demostración. `VERGIL_DATA_DIR` aísla los datos.
14. **La migración 3 se probó con una copia de tu base de datos real** (v2 → v3: torneo reconstruido, EV por defecto, sesiones).

### Sin hacer / pendiente
- ICM posterior (Fase 6) y mejora de velocidad del evaluador (Fase 8).
- Los colores del desplegable de periodo/formato siguen siendo los del sistema (claros).


## Fase 5: replayer, notas, autonotas y etiquetas
1. El replayer describe cada paso en español y reconstruye bote, apuestas, devoluciones y showdown a partir de la mano guardada (`HandReplay`).
2. El equity se muestra desde el último all-in, y solo si se ven las cartas de todos los jugadores vivos.
3. Con mesas abiertas el replayer no se abre (Modo Juego: es análisis profundo) y, si se empieza a jugar con uno abierto, se cierra solo.
4. Las notas y colores se guardan por apodo del jugador; se editan en la ficha (Jugadores, doble clic) y se ven en el HUD como punto de color, ✎ y texto del tooltip. El popup del overlay es de solo lectura porque no puede recibir teclado.
5. Las autonotas salen de reglas en JSON (`autonotes.json`, 8 de serie); se aplican a cada mano nueva y se regeneran si cambian las reglas o desde Ajustes.
6. «Copiar texto» anonimiza a los rivales (aparecen por posición) para pegar en foros.
7. Las etiquetas de mano (estudiar, error, bluff, valor, bien jugada y las tuyas) se filtran desde la vista Manos. Migración 4 añade índices únicos y la tabla `meta`.


## Fase 6: leaks, estudio e ICM
1. Solo es leak si TODO el intervalo del 90 % cae fuera del rango objetivo. El intervalo usa un prior centrado en el rango (fuerza 15): con 20 oportunidades un 80 % de fold a 3-Bet no basta, con 300 sí.
2. El impacto compara lo que ganas en esos spots con tu media (bb/100 de todas tus manos). Los rangos son orientativos (regular de NL baja 6-max) y se editan en `leak-baselines.json`.
3. El Estudio usa SM-2 con tres notas (Mal=1, Dudoso=3, Bien=5). Entran manos con etiqueta «estudiar»/«error», botes grandes perdidos (≥ 50 bb en showdown), las de un leak y las de Nash.
4. La pregunta se hace justo antes de tu última decisión con algo que pagar; el resultado queda oculto hasta responder.
5. La tabla `review_cards` reservada en la v2 nunca se usó: la migración 5 la sustituye.
6. El ICM es una aproximación para estudiar: stacks de la mesa como todo el campo, un único rival con cartas visibles, sin ciegas ni antes. En Spin no aplica (ganador se lo lleva todo). Los premios por defecto solo existen para SNG; en el análisis se escriben a mano.

## Fase 7: estudio avanzado
1. La matriz de equity 169×169 se calcula por Monte Carlo (2.500–3.000 tableros por pareja, semilla fija) y se guarda en disco la primera vez.
2. El solver es fictitious play con EV de fichas. HU a 10 bb: el SB empuja ≈ 58 % (tolerancia ±2 de las tablas publicadas). La tabla HU completa (1–25 bb) sale en segundos.
3. El 3-handed es una aproximación: cuando el BTN empuja, SB y BB responden como si fueran heads-up contra él; no se modelan botes con dos all-in a la vez.
4. «Tus desviaciones» solo juzga casos claros (Nash ≥ 80 % o ≤ 20 %) y toma el stack efectivo del héroe como el del rival.
5. Las 5 manos diarias se puntúan con tamaño del resultado, rareza, leak activo y desviación de Nash; se guardan para no cambiar durante el día.
6. El diagnóstico de equilibrio clasifica fuerza de mano (fuerte/medio/marginal/proyecto/aire) con el evaluador y avisa de patrones con ≥ 85 % en una sola categoría y al menos 20 manos.
7. Los arquetipos (k-means, k=6) sustituyen al clasificador fijo en el HUD cuando hay modelo (≥ 6 rivales con 200 manos; 50 en Spin); el badge lleva «?» si duda entre dos.

## Fase 8: bienestar, bankroll y pulido
1. El tilt score (0–100) suma estilo de hoy frente al habitual (30), pérdidas (35), tiempo sin pausa (20) y mesas (15). Los avisos son una ventana discreta (no una notificación de Windows) y nunca cierran mesas.
2. Bankroll: rakeback y bonos se cuentan aparte del juego. El riesgo de ruina se da como rango con el intervalo del 80 % del winrate; si no se puede descartar un winrate ≤ 0, el máximo es 100 %.
3. Los objetivos semanales usan «repasos de estudio» en lugar de minutos (la app no mide el tiempo de estudio).
4. El informe semanal se genera al abrir la app la primera vez de cada semana, y se puede abrir desde el Dashboard o Ajustes.
5. Evaluador: el camino de 7 cartas pasó de 6,7 a ≈ 16 millones de evaluaciones por segundo en un Ryzen 5 3450U. El objetivo de diseño (50 M/s) NO se alcanza en este equipo; la prueba es una guardia contra regresiones (10 M/s).
6. Idioma: los textos de la interfaz se traducen por coincidencia exacta (navegación, botones y etiquetas); los mensajes con cifras dinámicas siguen en español. Tema y idioma se aplican al reiniciar.
7. El tilt aparece en la barra de estado y en el tooltip de tu propio panel; no se dibuja como badge dentro del HUD.
8. Copia de seguridad diaria con VACUUM INTO (se conservan 5), registro de errores diario (14 días) y manejo global de excepciones que no cierra la app.

## Multiplicadores de Spin & Go
- Las probabilidades de los premios de 3 jugadores salen de la tabla publicada por PokerStars (pokerstars.com/es-419/poker/spin-and-go-max/payouts, consultada el 2026-11-12). Solo cambia con la inscripción el reparto entre ×3 y ×2.
- La vista Torneos compara los multiplicadores de tus victorias con la tabla (intervalo normal del 95 %) y da el ROI de un jugador sin ventaja (≈ −8 %). Hace falta al menos 10 victorias. Si juegas Spin & Go normal y su tabla difiere de la de «Max», habría que sustituir los datos de `SpinMultipliers.cs`.
