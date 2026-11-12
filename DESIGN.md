# Vergil HUD — Documento de diseño

> **Para quien implementa:** este documento es la especificación completa. Impleméntalo **por fases, en orden** (sección 15). Cada fase termina con `dotnet build` sin warnings, `dotnet test` en verde y sus criterios de aceptación cumplidos. No empieces una fase hasta que la anterior esté cerrada. Si algo del documento choca con el código existente, manda el documento salvo que rompa un test; en ese caso, documenta la decisión en `docs/DECISIONS.md`.

---

## 1. Visión

Un tracker + HUD para PokerStars que:

1. **Iguala** lo esencial de HM3/PT4 (importar, base de datos, HUD, popups, informes, gráficas, replayer, notas).
2. **Los supera** en cuatro puntos donde las herramientas de pago están estancadas:
   - **Estadísticas honestas**: cada número muestra lo fiable que es (estadística bayesiana con intervalos), en lugar de enseñar "VPIP 100" con 2 manos.
   - **Stats definibles sin programar**: un mini-lenguaje seguro para crear estadísticas propias, compilado a SQL.
   - **Estudio activo**: detector de leaks con significación estadística, y una cola de repaso espaciado (tipo Anki) de tus propias manos.
   - **Cumplimiento integrado**: el programa sabe cuándo estás jugando y bloquea solo lo que las reglas no permiten.
3. Es rápido: importa más de 2.000 manos/s y el HUD se actualiza en menos de 200 ms tras cada mano.

### Fuera de alcance

- Asistencia en tiempo real (RTA), consejos de decisión, solvers o rangos recomendados **durante** la partida.
- Minería de datos: importar manos que no has jugado tú.
- Scripts de sentarse en mesas o de selección automática de mesas.
- Mesas Zoom en el HUD: la mesa cambia en cada mano. Sus manos sí se importan y cuentan para las estadísticas.

---

## 2. Cumplimiento (restricciones obligatorias)

Las reglas de PokerStars permiten los HUD que muestran **estadísticas de manos que el propio usuario ha jugado**. Prohíben todo lo que dé **consejo en tiempo real**.

| Regla | Implementación |
|---|---|
| Solo datos propios | El importador solo lee la carpeta HandHistory local. No habrá función de importar manos de terceros. |
| Nada de RTA | Ninguna vista del HUD sugiere acciones. El HUD muestra frecuencias históricas, nunca "deberías…". |
| Análisis profundo fuera de juego | **Modo Juego**: si hay alguna ventana de mesa abierta, se bloquean el Replayer, el Detector de leaks, el Estudio y la calculadora de equity. Aparece un banner: *"Disponible al cerrar las mesas"*. |
| Transparencia | Pantalla *Acerca de / Cumplimiento* con lo que hace la app y un enlace a la política de herramientas de PokerStars. |

**Modo Juego** = `OverlayManager` detecta ≥1 ventana de mesa (por título o por clase `PokerStarsTableFrameClass`). Lo expone un servicio `PlaySessionState` con el evento `Changed`. Cada vista bloqueable implementa `IRestrictedDuringPlay`.

> ⚠️ Las reglas de las salas cambian. Antes de publicar cualquier versión, el usuario debe revisar la lista vigente de herramientas permitidas de PokerStars.

---

## 3. Estado actual (Fase 1, ya implementada)

```
src/VergilHud.Core   Models, PokerStarsParser, StatsCalculator, PlayerStats, HudDatabase, HandHistoryImporter, TableTracker
src/VergilHud.App    MainWindow, OverlayManager, OverlayWindow, SeatPanel, AppSettings, NativeMethods
tests/VergilHud.Tests ParserTests (5 tests)
```

Qué hay que tener en cuenta del estado actual:

- Las estadísticas están fijas en código: 17 flags en la tabla `player_hands`.
- No hay migraciones de esquema.
- El HUD consulta la base de datos para cada jugador en cada mano, sin caché.
- No hay estadísticas por posición, turn/river, torneos (ICM), notas ni gráficas.

---

## 4. Arquitectura objetivo

```
src/
  VergilHud.Core/          sin dependencias de UI
    Parsing/               IHandHistoryParser, PokerStarsParser (+ futuras salas)
    Domain/                Hand, Seat, HandAction, Street, Card, Board
    Facts/                 FactExtractor  -> PlayerHandFacts (una fila por jugador y mano)
    Stats/                 StatDefinition, StatDsl (lexer/parser/compiler a SQL), StatEngine, Bayes
    Data/                  HudDatabase, Migrations/, Repositories
    Import/                HandHistoryImporter, ImportQueue
    Equity/                HandEvaluator, EquityCalculator (all-in EV)
    Analysis/              Sessions, LeakDetector, Reports, Graphs (series de datos)
    Study/                 ReviewQueue (SM-2), Tags
    Compliance/            PlaySessionState
  VergilHud.App/           WPF, MVVM (CommunityToolkit.Mvvm)
    Hud/                   OverlayManager, OverlayWindow, SeatPanel, PopupView, HudLayouts
    Views/                 Dashboard, Players, Reports, Graphs, Replayer, Leaks, Study, Settings, HudEditor
    Services/              Settings, Theme, Notifications
tests/
  VergilHud.Tests/         unitarios
  VergilHud.Tests/Fixtures/ historiales reales anonimizados (.txt)
```

**Reglas de arquitectura**

- La UI solo habla con Core mediante servicios/repositorios. No hay SQL en la App.
- Todo lo que lee la base de datos fuera del hilo de UI es `async`.
- El importador escribe en un único hilo (cola). Las lecturas usan conexiones separadas: SQLite en modo WAL lo permite.
- Paquetes permitidos: `Microsoft.Data.Sqlite`, `CommunityToolkit.Mvvm`, `ScottPlot.WPF` (gráficas), `xunit`. Para añadir cualquier otro hace falta una decisión documentada.

---

## 5. Modelo de datos (esquema v2)

### 5.1 Migraciones

Tabla `schema_version(version INTEGER)`. Las migraciones son clases `IMigration { int Version; void Up(SqliteConnection) }` y se ejecutan en orden al abrir la base de datos, cada una en una transacción.

La migración v1→v2 **reimporta** desde `hands.raw`: vuelve a parsear cada mano y regenera los facts. Así, cada vez que se mejore el extractor basta con subir la versión.

### 5.2 Tablas

```sql
hands(
  hand_id TEXT PK, site TEXT, table_name TEXT, game TEXT,          -- 'NLH','PLO',...
  format TEXT,            -- 'cash' | 'mtt' | 'sng' | 'spin' | 'zoom'
  tournament_id TEXT NULL, buyin REAL NULL, level INTEGER NULL,
  played_at TEXT, small_blind REAL, big_blind REAL, ante REAL,
  max_seats INTEGER, players_dealt INTEGER, hero TEXT NULL, hero_cards TEXT NULL,
  board TEXT, pot REAL, rake REAL, session_id INTEGER NULL, raw TEXT)

player_hand_facts(      -- una fila por jugador repartido por mano; ver 6.1
  hand_id, player, seat, position, ... columnas de facts ...,
  PRIMARY KEY(hand_id, player))
  INDEX (player, played_at)  INDEX (player, position)

players(player TEXT PK, first_seen TEXT, last_seen TEXT, hands INTEGER,
        color_label TEXT NULL, note TEXT NULL)              -- notas y etiqueta de color

auto_notes(id PK, player, created_at, rule_id, text, hand_id)

sessions(id PK, started_at, ended_at, hands INTEGER, net REAL, net_bb REAL,
         ev_net_bb REAL, tables_max INTEGER, tilt_score REAL)

hand_tags(hand_id, tag TEXT, created_at)                     -- 'error', 'estudiar', 'bluff', libres

review_cards(hand_id PK, ease REAL, interval_days INTEGER, due_at TEXT,
             reps INTEGER, last_result TEXT)                 -- SM-2

allin_ev(hand_id, player, street TEXT, equity REAL, ev_net REAL)  -- EV ajustado de all-ins

custom_stats(id TEXT PK, json TEXT)                          -- definiciones DSL del usuario

files(path PK, offset INTEGER)
```

---

## 6. Motor de estadísticas (la pieza central)

### 6.1 Facts: una fila rica por jugador y mano

En lugar de 17 flags, `FactExtractor` genera unos **80 hechos atómicos**: booleanos y enteros pequeños. **Toda estadística se define como `SUM(numerador)/SUM(denominador)` sobre estos hechos.** Así una estadística nueva no exige reimportar nada.

Lista mínima de columnas (prefijo = calle):

**Contexto:** `position` (UTG, UTG1, UTG2, MP, HJ, CO, BTN, SB, BB), `players_dealt`, `eff_stack_bb` (stack efectivo frente al agresor o a la mesa), `is_hero`, `format`, `big_blind`.

**Preflop**
- `pf_vpip`, `pf_pfr`, `pf_limp`, `pf_limp_call`, `pf_limp_raise`
- `pf_open_opp`, `pf_open` (RFI)
- `pf_cold_call_opp`, `pf_cold_call`
- `pf_3bet_opp`, `pf_3bet`, `pf_3bet_vs_steal_opp`, `pf_3bet_vs_steal`, `pf_squeeze_opp`, `pf_squeeze`
- `pf_f3b_opp`, `pf_f3b`, `pf_4bet_opp`, `pf_4bet`, `pf_f4b_opp`, `pf_f4b`, `pf_5bet`
- `pf_steal_opp`, `pf_steal`, `pf_fts_opp`, `pf_fts`, `pf_resteal`
- `pf_bb_defend_opp`, `pf_bb_defend`, `pf_sb_complete`
- `pf_allin`

**Flop / Turn / River** (sustituir `X` por `f`, `t`, `r`)
- `X_saw`
- `X_cbet_opp`, `X_cbet` (en turn y river es la *barrel*: el agresor de la calle anterior apuesta otra vez)
- `X_fcb_opp`, `X_fcb`, `X_raise_cb_opp`, `X_raise_cb`
- `X_donk_opp`, `X_donk`
- `X_float_bet_opp`, `X_float_bet` (apostar en IP cuando el agresor pasa)
- `X_check_raise_opp`, `X_check_raise`
- `X_probe_opp`, `X_probe` (OOP apuesta cuando el agresor no apostó la calle anterior)
- `X_bets`, `X_raises`, `X_calls`, `X_checks`, `X_folds`
- `X_cbet_ip` / `X_cbet_oop` y `X_multiway` como contexto

**Showdown y dinero:** `wtsd`, `wsd`, `wwsf` (gana habiendo visto el flop), `went_allin`, `net`, `net_bb`, `ev_net_bb` (6.5), `rake_paid`.

**Tamaños:** `pf_open_size_bb`, `f_cbet_size_pct` (porcentaje del bote), `t_bet_size_pct`, `r_bet_size_pct`. Permiten estadísticas como "C-Bet ≤ 33% del bote".

`FactExtractor` necesita tests de *golden files*: para cada fixture `.txt` existe un `.facts.json` con los facts esperados. Hacen falta como mínimo 30 manos que cubran limp, squeeze, 4-bet, donk, check-raise, all-in preflop, side pots, SB muerta, heads-up, 9-max, torneo con ante y jugador sentado-fuera.

### 6.2 Mini-lenguaje de stats (innovación)

Una estadística es un JSON:

```json
{
  "id": "cbet_flop_ip_srp",
  "label": "CB IP",
  "description": "C-Bet en flop en posición en botes de una subida",
  "numerator":   "f_cbet && f_cbet_ip && !pf_3bet_pot",
  "denominator": "f_cbet_opp && f_cbet_ip && !pf_3bet_pot",
  "format": "pct0",
  "prior": { "strength": 15 },
  "thresholds": [ { "lt": 40, "color": "low" }, { "lt": 70, "color": "mid" }, { "color": "high" } ]
}
```

**Gramática:**

```
expr   := or
or     := and ('||' and)*
and    := not ('&&' not)*
not    := '!' not | cmp
cmp    := atom (('=='|'!='|'<'|'<='|'>'|'>=') atom)?
atom   := IDENT | NUMBER | STRING | '(' expr ')' | IDENT 'in' '[' STRING (',' STRING)* ']'
```

- `IDENT` tiene que ser una columna de la lista blanca de facts. Cualquier otro identificador da un error de compilación con su posición.
- El compilador genera `SUM(CASE WHEN <sql> THEN 1 ELSE 0 END)` y **siempre usa parámetros para los literales**, sin interpolarlos. No hay inyección posible.
- Las estadísticas incorporadas (VPIP, PFR, etc.) se definen con este mismo lenguaje en `Stats/builtin-stats.json`, un recurso embebido. Así el lenguaje se prueba en cada uso.
- La App incluye un **Editor de stats** con validación en vivo y vista previa del valor sobre tu propia base de datos.

### 6.3 Estadísticas bayesianas con intervalo (innovación)

Problema: con 3 manos, "VPIP 67" engaña. HM3 y PT4 solo atenúan el color.

Solución, para cada estadística de cada jugador:

- **Prior** = Beta(α₀, β₀), con α₀ = k·μ y β₀ = k·(1−μ).
  - μ = media de la **población** de tu base de datos para esa estadística, en el mismo límite y formato.
  - μ se recalcula cada 1.000 manos importadas y se guarda en caché.
  - k = `prior.strength` de la definición (por defecto 10).
- **Posterior** = Beta(α₀+x, β₀+n−x).
- **Valor mostrado** = media posterior. Tiende a la media de la población cuando la muestra es pequeña y al valor real cuando es grande.
- **Intervalo creíble del 80%** con aproximación normal sobre la Beta. Basta: no hace falta la beta incompleta exacta.
- **Ancho del intervalo** → **fiabilidad** de 0 a 1 = `1 − clamp(ancho / 0.5)`.

**En el HUD:**

- **Modo "Honesto"** (por defecto): se muestra el valor posterior. Su opacidad o subrayado depende de la fiabilidad, con una barra fina bajo el número (llena = fiable).
- **Modo "Clásico"**: x/n en crudo, como HM3.
- El popup muestra siempre el valor crudo, el posterior, el intervalo `[lo–hi]` y n.

Tests de Bayes: con n=0 el valor es igual a μ; con n muy grande tiende a x/n; el intervalo se estrecha de forma monótona al crecer n.

### 6.4 Filtros y ventanas de tiempo

`StatQuery { player, filters }`, con filtros combinables:

- límite (BB), formato, número de jugadores (HU / 3-6 / 7-10), posición
- rango de fechas, últimas N manos, sesión actual
- `vs_player` (estadísticas de un rival solo en manos contra el héroe)

El HUD admite tres ventanas por panel (configurables): **total**, **últimas 100 manos** y **sesión de hoy**. Muestra una flecha ↑↓ si la ventana reciente se sale del intervalo del total. **Es una detección de cambio de estilo o de tilt del rival**: solo describe datos históricos, no aconseja nada.

### 6.5 EV de all-in (suerte)

Cuando todos los que siguen en la mano están all-in y hay cartas mostradas:

1. `EquityCalculator` enumera los runouts exactos. Es posible: como mucho C(48,5) = 1,7 M en preflop HU, y usa Monte Carlo de 200 k cuando hay 3 o más jugadores.
2. Calcula `ev_net = equity·bote_disputado − invertido`.
3. Lo guarda en `allin_ev` y en `ev_net_bb`.

`HandEvaluator`: evaluador de 7 cartas por tabla de lookup (perfect hash al estilo Cactus Kev, o tabla 2+2 generada al primer arranque y cacheada en disco). Objetivo: ≥ 50 M evaluaciones/s.

El cálculo se hace en segundo plano después de importar, nunca en el camino crítico del HUD.

---

## 7. HUD

### 7.1 Paneles

- Cada panel tiene **N líneas** configurables. Cada línea es una lista de stats separadas por `/` o espacio.
- Perfil por defecto:
  - `VPIP/PFR/3B  AF`
  - `CB F-CB WTSD`
  - `Steal FtS 4B`
- Cabecera: nombre (recortado), manos, **icono de etiqueta de color** del jugador y un **badge de perfil** (Nit, TAG, LAG, Fish, Whale, Maníaco, Calling station), calculado con las estadísticas posteriores.
- Colores por umbral desde la definición de la stat. Paleta accesible y con opción para daltonismo.
- **Perfiles de HUD** por tipo de mesa: HU, 6-max, 9-max y torneo. Cambian solos según `max_seats` y `format`.
- **Editor de HUD visual** (vista `HudEditor`): una mesa de ejemplo en la que se arrastran las stats a las líneas y se elige color y formato. Se guarda en `hud-profiles.json`.

### 7.2 Popups

- **Clic** en el panel: popup de varias pestañas.
  1. **Resumen**: todas las estadísticas con crudo, posterior, intervalo y n.
  2. **Por posición**: tabla de RFI, 3B y F3B por posición.
  3. **Postflop**: CB, FCB, X/R, donk, probe y float por calle, con los **tamaños de apuesta habituales** (mediana del % del bote).
  4. **Showdowns**: últimas 20 manos mostradas, con la línea jugada (ej. `CO open · call 3B · flop X/C · turn B 75%` → `KJs`). *Ventaja frente a HM3: la línea se ve en una fila, no hay que abrir la mano.*
  5. **Notas**: nota libre, etiqueta de color y autonotas.
- **Mantener Ctrl** sobre una stat: popup de esa stat desglosada (por posición y por tamaño del bote).
- Los popups se cierran al hacer clic fuera, con Esc o cuando empieza una mano nueva en esa mesa.

### 7.3 Colocación y ventanas

- Posiciones por perfil de mesa en coordenadas relativas. Arrastrar guarda la posición (ya implementado; se migra a los perfiles).
- Opción "fijar posición" (bloquea el arrastre).
- Escalado según el tamaño de la mesa: fuente mínima 9 pt y máxima 14 pt.
- DPI por monitor: manifiesto `PerMonitorV2` y conversión con `VisualTreeHelper.GetDpi` en cada movimiento.
- Detección de mesa: título, clase de ventana y proceso `PokerStars.exe`. Si no hay ninguna mano todavía, se muestra un panel *"Esperando mano…"*.
- Mesas que tapan otras: el overlay pertenece a su mesa (ya implementado), así que el orden Z es correcto.

### 7.4 Autonotas (reglas sobre facts)

Reglas en JSON con condición DSL sobre **una mano** y plantilla de texto. Ejemplos:

- `pf_limp_raise` → "Limp-raise desde {position} con {cards}" (solo si se mostraron cartas)
- `r_bet_size_pct >= 150 && wtsd && !wsd` → "Overbet river de farol ({r_bet_size_pct}%)"

Se generan al importar y aparecen en la pestaña Notas con un enlace a la mano.

### 7.5 Rendimiento del HUD

- `StatCache` en memoria: `(player, profileId) → valores`. Se invalida solo para los jugadores de la mano recién importada.
- Una sola consulta agrupada por mano, `WHERE player IN (...)`, en vez de una por jugador.
- Objetivo: < 50 ms de consulta y < 200 ms desde que termina de escribirse el archivo hasta que se repinta el HUD, con 1 M de manos en la base de datos.

---

## 8. App principal (análisis)

Navegación lateral con estas vistas: **Dashboard · Jugadores · Informes · Gráficas · Sesiones · Replayer · Leaks · Estudio · HUD · Ajustes**.

### 8.1 Dashboard

- Tarjetas: manos, bb/100, EV bb/100, neto, rake pagado, horas jugadas y hands/hora del periodo.
- Minigráfica de las últimas 30 sesiones.
- Selector de periodo: hoy, 7 días, 30 días, año, todo o rango libre.

### 8.2 Jugadores

- Tabla virtualizada (`VirtualizingStackPanel`) con columnas elegidas entre cualquier stat y filtros (6.4).
- Doble clic: ficha del jugador con las mismas pestañas que el popup del HUD, más la gráfica de lo que te ha ganado o perdido.

### 8.3 Informes

- Informes agrupables por: **posición, cartas propias (matriz 13×13), stack efectivo, número de jugadores, mes, límite y rival**.
- Matriz 13×13 coloreada por bb/100 o por frecuencia de juego. Clic en una casilla → lista de manos.
- Exportación a CSV.

### 8.4 Gráficas (ScottPlot)

- Ganancias acumuladas en fichas o bb, con las líneas: **neto**, **all-in EV**, **sin showdown (roja)** y **con showdown (azul)**.
- **Banda de varianza** (innovación sencilla): banda de ±1σ y ±2σ alrededor de la recta de tu winrate, usando la desviación típica por mano observada. Responde a si un downswing es normal.
- Histograma de resultados por sesión.
- Winrate por hora del día y por día de la semana.

### 8.5 Sesiones

- Detección automática: una pausa de más de 30 minutos entre manos abre una sesión nueva.
- Tabla de sesiones con duración, manos, mesas máximas simultáneas, neto, EV y **tilt score** (sección 12).

---

## 9. Replayer

- Mesa dibujada en WPF: asientos, stacks, apuestas, bote, board y cartas mostradas.
- Controles: ⏮ ◀ ▶ ⏭, reproducción automática con velocidad ajustable y salto a cada calle.
- Línea de tiempo con las acciones. Clic en una acción → estado de la mesa en ese momento.
- Cantidades en fichas o en BB.
- En cada decisión del héroe se muestran el **tamaño del bote**, las **pot odds** y el **SPR**. Es aritmética de la mano ya terminada, no consejo.
- Si la mano tuvo all-in: equity en cada calle.
- Botones: etiquetar, añadir al Estudio, copiar en formato texto para foros y exportar como PNG.
- Abrir desde cualquier lista de manos. Atajos de teclado: ← → espacio.
- Bloqueado en Modo Juego.

---

## 10. Detector de leaks (innovación)

1. **Referencia**: un perfil de rangos objetivo por stat y posición, en `leak-baselines.json` y editable por el usuario. Viene precargado con valores orientativos de un regular ganador de NL baja 6-max (por ejemplo, RFI BTN 40–50%, F3B 50–60%, CB flop SRP 50–70%).
2. Para cada stat del héroe:
   - Posterior bayesiano (6.3) y su intervalo del 90%.
   - **Solo es un leak si todo el intervalo cae fuera del rango objetivo.** Así se evitan los falsos positivos por muestras pequeñas, el gran defecto de los "leak finders" actuales.
3. **Impacto estimado**: bb/100 del héroe en los spots afectados frente a su winrate general, en bb/100 ponderado por frecuencia. Ordena los leaks por impacto, no por desviación.
4. Cada leak muestra: descripción, tu valor con intervalo, el rango objetivo, n, el impacto y el botón **"Ver manos"**, que lleva a la lista filtrada y permite enviarlas al Estudio.
5. Se recalcula al cerrar la sesión o a demanda. Bloqueado en Modo Juego.

---

## 11. Estudio (repaso espaciado de tus manos) (innovación)

- Las manos entran a la cola de tres formas:
  - con la etiqueta `estudiar` o `error`;
  - desde "Ver manos" de un leak;
  - con la regla automática "botes ≥ 50 bb perdidos en showdown".
- Sesión de estudio:
  1. Se muestra la mano en el Replayer **hasta la decisión del héroe**, ocultando el resultado.
  2. El usuario escribe o elige qué haría: fold / call / raise / tamaño.
  3. Se revela lo que hizo y el resultado.
  4. El usuario se puntúa: *Mal / Dudoso / Bien*.
- Programación **SM-2**: ease inicial 2,5; la siguiente fecha depende de la puntuación. Se guarda en `review_cards`.
- Métricas: tarjetas pendientes, racha de días y porcentaje de "Bien" por categoría de leak.
- Bloqueado en Modo Juego.

---

## 12. Bienestar y control de tilt del héroe (innovación)

Es información sobre **ti mismo**, no consejos de juego.

- Se calcula durante la sesión con las manos ya terminadas del héroe.
- **Tilt score** de 0 a 100, que combina:
  - VPIP y PFR de la sesión frente a tu posterior histórico (desviación en σ);
  - neto de la sesión en BB (pérdidas grandes);
  - minutos sin pausa;
  - mesas simultáneas.
- Indicador discreto en la ventana principal y, si se activa, un mini-badge en tu propio panel del HUD.
- **Límites que define el usuario**: stop-loss en BB, tiempo máximo de sesión y recordatorio de pausa cada X minutos. Avisan con una notificación de Windows y **nunca cierran mesas**.
- Gráfica del tilt score por sesión en la vista Sesiones.

---

## 13. Importación y salas

- Interfaz `IHandHistoryParser { bool CanParse(string header); Hand? Parse(string block); }`. Hay un registro de parsers y la cabecera decide cuál usar. PokerStars es el primero; GGPoker (solo análisis posterior; PokerCraft exporta historiales) y Winamax quedan como trabajo futuro.
- Formatos de PokerStars: cash NLH y PLO (4 y 5 cartas, `hero_cards` con más de 2 cartas), Zoom, MTT, SNG y Spin & Go (antes, bounties y niveles).
- **Cliente en español**: detectar cabeceras localizadas (`Mano n.º`) y avisar en la UI con el texto *"Cambia el idioma de los historiales a inglés"*. No se parsean.
- **Importación masiva**: un botón "Importar carpeta/archivos" para historiales antiguos, con barra de progreso, cancelación y throughput mostrado. Usa transacciones de 1.000 manos y desactiva temporalmente los índices secundarios.
- **Torneos**: `buyin` y `level`; resultados por torneo en una vista de Informes (ROI e ITM). Si existen, se importan también los *tournament summaries* de PokerStars.
- **Errores**: las manos que no se pueden parsear van a `import_errors(path, offset, message, raw)` y aparece un contador en la UI. Nunca detienen la importación.

---

## 13b. Torneos (MTT, SNG, Spin & Go)

**Datos**
- Al importar, cada mano lleva `tournament_id`, `buyin` (entrada + rake + bounty), `level`, `ante` y `format`.
- Se importan los *Tournament Summaries* de PokerStars (`TS*.txt`, en la misma carpeta) a una tabla nueva:
  `tournaments(id PK, format, buyin, rake, bounty, prize_pool, entrants, started_at, finished_at, finish_position, prize, bounties_won, is_turbo, is_knockout, re_entries)`.
- Si no hay summary, la posición final se deduce de la última mano del héroe ("finished the tournament in Nth place") y el premio queda pendiente.

**Facts propios de torneo** (añadir a 6.1)
- `stack_bb` (stack del jugador en ciegas al empezar la mano) y `m_ratio`
- `stack_bucket`: `<10`, `10-20`, `20-40`, `40+`
- `pf_shove_opp`, `pf_shove` (open-shove)
- `pf_call_shove_opp`, `pf_call_shove`
- `pf_reshove_opp`, `pf_reshove`
- `is_bubble` (quedan ≤ 115 % de los premiados; solo si hay summary)

**HUD**
- Perfil de HUD de torneo propio, elegido automáticamente con `format != cash`.
- Líneas por defecto:
  - `VPIP/PFR/3B  stack_bb`
  - `Shove% / CallShove%` (dependen de `stack_bucket`)
  - `Steal FtS CB`
- Las estadísticas de push/fold se muestran **para el rango de stack actual del jugador**. Ejemplo: si tiene 12 bb, se ve su Shove% con 10–20 bb, no el global. Ninguna herramienta de pago lo hace por defecto.
- Las estadísticas de cash y de torneo de un mismo jugador se guardan juntas pero se filtran por formato. El HUD de torneo usa solo manos de torneo, salvo que haya menos de 30; en ese caso usa todas y marca el panel con un asterisco.

**Análisis**
- Vista **Torneos**:
  - tabla con buy-in, posición, premio, neto, bounties y duración;
  - totales de **ROI**, **ITM %**, beneficio medio y número de torneos;
  - filtros por tipo, buy-in, velocidad y fecha.
- Gráfica de beneficio acumulado por torneo, con la banda de varianza de 8.4 (la de torneos es mucho más ancha que en cash).
- Informes por fase del torneo (temprana, media, burbuja, mesa final) y por `stack_bucket`.
- **Spin & Go**: ROI por multiplicador del premio, si la summary lo trae.
- **Análisis ICM posterior** (solo fuera de juego, como en 2):
  - en manos de all-in con summary disponible, se calcula el $EV con ICM (Malmuth-Harville) frente al chip EV;
  - se muestra en el Replayer y entra en el detector de leaks como "calls/shoves que pierden $EV".
  - **Nunca se muestra en el HUD ni con mesas abiertas.**
- Detector de leaks: `leak-baselines.json` tiene una sección propia de torneos por `stack_bucket` (por ejemplo, el Fold a steal en BB con 10–20 bb).

**Fixtures:** como mínimo 10 manos de torneo (con ante, bounty, eliminación, all-in multiway y heads-up final) y 3 summaries.

---

## 14. Rendimiento y calidad

| Métrica | Objetivo |
|---|---|
| Importación masiva | ≥ 2.000 manos/s en SSD |
| Retardo del HUD tras escribir la mano | < 200 ms (p95) |
| Consulta de stats de 9 jugadores (1 M de manos en la BD) | < 50 ms |
| Memoria con 12 mesas abiertas | < 300 MB |
| Arranque | < 2 s |

- Benchmarks en `tests/VergilHud.Benchmarks` (BenchmarkDotNet) a partir de la Fase 3.
- Logging con `Microsoft.Extensions.Logging`, escrito en un archivo rotativo en `%APPDATA%\VergilHud\logs`.
- Copia de seguridad automática de `hud.db` cada 7 días (se conservan 3).

---

## 15. Roadmap por fases

Cada fase incluye sus tests. El orden importa: las fases posteriores dependen de los facts y del DSL.

### Fase 2 — Cimientos del motor
00. **Paso 0 (18.2): validar el parser con los historiales reales del usuario antes de tocar nada más.**
0. `publish.ps1` y ejecutable único (ver 17.4).
1. Migraciones y esquema v2 (secc. 5), con reimportación desde `raw`.
2. `FactExtractor` completo (6.1), con los facts de torneo de 13b, más los golden tests: ≥ 30 fixtures de cash y ≥ 10 de torneo.
3. DSL: lexer, parser y compilador a SQL (6.2), con tests de errores y de inyección.
4. `builtin-stats.json` con todas las stats actuales y las nuevas, incluidas las de tamaño de apuesta (19.1).
5. `StatEngine` + `StatCache` + la consulta agrupada (7.5).
6. Facts de tamaño (`X_faced_size_bucket`, `X_bet_size_bucket`, 19.1) y `hero_made_hand` (19.4). Este último requiere el evaluador, así que puede quedar a `NULL` hasta la Fase 4, que lo rellena con una migración.

**Aceptación:** los tests actuales siguen pasando una vez adaptados; VPIP/PFR/3B calculados con el DSL coinciden con los de la Fase 1 sobre los fixtures; mutar la DSL con `'; DROP TABLE` da un error de compilación.

### Fase 3 — Estadística honesta + HUD configurable
1. Bayes (6.3), con priors de población cacheados.
2. Perfiles de HUD, líneas configurables, umbrales y colores, modo Honesto/Clásico.
3. Popups con pestañas (7.2): Resumen, Por posición, Postflop, Showdowns.
4. Ventanas total / 100 / sesión con flechas de cambio.
5. DPI PerMonitorV2, escalado y fijar posición.
6. `PlaySessionState` y el banner de Modo Juego.
7. Fila por tamaño de apuesta en el popup Postflop y `FCB pequeña·grande` en el panel (19.1).
8. Tabla `hud_allowed_by_format` (18.6).
9. Asistente de primer arranque y fusión de nicks del héroe (19.10).

**Aceptación:** con un jugador de 2 manos, el HUD muestra un valor cercano a la media de población y la barra de fiabilidad casi vacía; pasar de 6-max a HU cambia el perfil solo.

### Fase 4 — Análisis
1. Navegación MVVM con las vistas del apartado 8 (Dashboard, Jugadores, Informes, Sesiones).
2. Gráficas con banda de varianza (8.4).
3. `HandEvaluator` + `EquityCalculator` + all-in EV (6.5), con tests contra equities conocidas (AA vs KK preflop ≈ 81,9 %).
4. Importación masiva con progreso (13).
5. Torneos: summaries, vista Torneos (ROI/ITM), perfil de HUD de torneo y stats de push/fold por stack (13b). El ICM posterior va en la Fase 6, junto a los leaks.
6. ROI con intervalo de confianza (18.3) y datos propios de Spin & Go: multiplicadores, 3-handed frente a heads-up y tasa de heads-up (18.5).

**Aceptación:** con 100 k manos sintéticas, el Dashboard carga en < 1 s; la línea de EV aparece en la gráfica; con 400 Spins sintéticos de ROI real 0 %, el intervalo incluye el 0 y el mensaje lo dice.

### Fase 5 — Replayer, notas y autonotas
1. Replayer completo (9).
2. Notas, etiquetas de color y autonotas (7.4).
3. Etiquetas de mano.

### Fase 6 — Leaks y Estudio
1. Detector de leaks bayesiano con impacto (10).
2. Cola de Estudio SM-2 (11).

**Aceptación:** con un héroe sintético cuyo F3B real es del 80 %, a partir de unas 300 oportunidades se detecta como leak, y con 20 oportunidades no.

### Fase 7 — Estudio avanzado (prioridad: Spin & Go)
1. Solver Nash de push/fold HU y 3-handed, matriz de equity 169×169 cacheada, e informe "Tus desviaciones" (19.5).
2. Selección automática diaria de manos para repasar (19.6).
3. Diagnóstico de equilibrio por fuerza de mano (19.4).
4. Vista Población (19.3).
5. Arquetipos por clusters (19.2), que sustituyen al clasificador fijo.

**Aceptación:** el Nash HU a 10 bb sin ante coincide con las tablas publicadas de referencia (por ejemplo, el SB empuja ≈ 58 % de las manos, con una tolerancia de ±2 puntos); el solver HU completo de 1 a 25 bb se calcula en < 60 s y después se lee de la caché; un héroe sintético que nunca empuja 22 con 8 bb aparece en "Tus desviaciones".

### Fase 8 — Bienestar, bankroll y pulido
1. Tilt score y límites (12).
2. Bankroll y riesgo de ruina (18.4). ~~PLO~~: descartado.
3. Diario de sesión (19.7), informe semanal (19.8) y objetivos (19.9).
4. Editor visual de HUD (7.1) y Editor de stats DSL (6.2).
5. Dinámica de mesa, atajos, exportar/importar configuración y aviso de carpeta sin cambios (18.7).
6. Español/inglés, tema claro/oscuro, recuperación ante fallos y comprobación de que no hay red (19.10).
7. Copias de seguridad, logs y benchmarks (14).

---

## 16. Estrategia de pruebas

- **Golden files** (`Fixtures/*.txt` + `*.facts.json`) para el parser y los facts. Para regenerar los JSON esperados hay un comando `dotnet test -- RegenerateGolden=true` que solo se usa conscientemente.
- **Propiedades**: en cualquier mano, la suma de `net` de todos los jugadores + rake = 0 (±0,01). Se aplica a todos los fixtures. Es el mejor detector de bugs del cálculo de dinero.
- **Equity** contra valores de referencia conocidos.
- **DSL**: tabla de expresiones válidas e inválidas y su SQL esperado.
- **Bayes**: propiedades límite (6.3).
- **UI**: lógica en ViewModels testeables. Las ventanas se prueban a mano con una checklist en `docs/QA.md`, que incluye cómo simular una mesa: una ventana de prueba titulada como una mesa del fixture y un archivo que se escribe mano a mano.
- Las manos de prueba se anonimizan con nombres ficticios y nunca contienen nicks reales de terceros.

---

## 17. Decisiones

### Tomadas (2026-11-11)
1. **Juego: No Limit Hold'em, en cash, MTT, SNG y Spin & Go.** El formato favorito es **Spin & Go**: cuando haya que priorizar dentro de torneos, primero Spin & Go.
   - `leak-baselines.json` tiene secciones por formato: `cash` (NL baja 6-max), `mtt`, `sng` y `spin`, todas editables.
   - En `spin`, los rangos de referencia van por número de jugadores (3-handed / heads-up) y por `stack_bucket`, porque los Spin se juegan casi siempre con menos de 25 bb.
   - Perfiles de HUD por defecto: cash 6-max, cash HU, MTT/SNG 9-max y **Spin 3-max**. El de Spin es compacto: 2 líneas centradas en VPIP/PFR, Shove% por stack, Limp% en el BTN y Call-shove.
2. **PLO: fuera de alcance.** No implementar.
3. **Torneos: soporte completo, al mismo nivel que cash** (el usuario juega torneos). Ver sección 13b.
4. **Distribución: ejecutable único.** Desde la Fase 2, `publish.ps1` en la raíz ejecuta `dotnet publish src/VergilHud.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist/` y genera `dist/VergilHud.exe`. Se ejecuta con doble clic y no requiere tener .NET instalado.

5. **Sin consultas en lenguaje natural ni ninguna conexión a APIs externas.** La app funciona 100 % offline.

---

## 18. Correcciones y mejoras añadidas tras la revisión

### 18.1 Corrección: el ICM no aplica a Spin & Go
En un Spin & Go de 3 jugadores el premio es **winner-takes-all**: el ICM coincide con el chip EV. Por tanto el análisis ICM de 13b **solo se calcula en MTT y SNG con premios repartidos**. En Spin se usa el EV de fichas (6.5) y no se muestra ninguna comparación ICM.

### 18.2 Paso 0 obligatorio: validar con manos reales
La Fase 1 solo se ha probado con manos inventadas. **Antes de empezar la Fase 2**, hay que importar los historiales reales del usuario (cash, MTT, SNG y Spin & Go) y revisar:
- cuántas manos fallan al parsear (`import_errors`);
- que la suma de `net` de cada mano cuadre (propiedad de 16);
- que los títulos de ventana de PokerStars se detecten (cash y torneo tienen títulos distintos).
Los fallos se convierten en fixtures de los golden tests, anonimizados.

### 18.3 ROI con intervalo de confianza (torneos)
El ROI de unos pocos cientos de torneos es casi ruido, sobre todo en Spin. La vista Torneos muestra el **ROI con su intervalo del 90 %** y una frase del tipo *"con 400 Spins aún no se puede distinguir tu ROI de 0 %"*, además de cuántos torneos harían falta para que el intervalo deje de incluir el 0. Es la misma filosofía de 6.3.

### 18.4 Bankroll y riesgo de ruina
- Vista Bankroll: saldo, buy-in medio, nº de buy-ins disponibles.
- **Riesgo de ruina** estimado por simulación Monte Carlo con tu winrate y desviación reales (cash: bb/100; torneos: ROI y desviación por torneo), mostrado como rango y no como cifra exacta.
- Alerta si bajas de los buy-ins mínimos que tú configures.
- Seguimiento de rakeback y bonos como ingreso aparte.

### 18.5 Spin & Go: datos propios del formato
- Registrar el **multiplicador** de cada Spin (viene en el summary) y mostrar la distribución real que has visto frente a la teórica.
- Estadísticas de tu juego en 3-handed y en heads-up por separado, con el stack en ciegas al empezar cada fase.
- Seguimiento de la **tasa de heads-up alcanzados** y de ganados.

### 18.6 Cumplimiento por formato
Antes de activar el HUD en un formato, comprobar en la política de PokerStars si ese formato permite herramientas de terceros. Algunos formatos, sobre todo los de ritmo rápido, pueden tener restricciones distintas a las del cash. La app mantiene una tabla `hud_allowed_by_format` editable. Si un formato figura como no permitido, el overlay **no se muestra** ahí y esa sesión solo se importa para análisis posterior. **Verificado por el usuario (2026-11-11): el HUD está permitido en Spin & Go en la forma planteada (solo stats históricas propias).** Por defecto la tabla viene con todos los formatos permitidos excepto Zoom (sin HUD por limitación técnica).

### 18.7 Otras mejoras menores
- **Dinámica de mesa**: agresividad y VPIP medios de la mesa actual, calculados con los rivales sentados ahora.
- **Atajos de teclado** para abrir/ocultar los paneles y el popup.
- **Exportar e importar** la configuración (perfiles de HUD, stats propias, notas) para no perderla al reinstalar.
- **Aviso si la carpeta de historiales deja de crecer** mientras hay una mesa abierta (típico de cuando se desactiva el guardado en PokerStars).

---

## 19. Innovaciones de segunda ronda

Todas respetan la sección 2. Las de análisis (19.3–19.7) están **bloqueadas en Modo Juego**.

### 19.1 Stats según el tamaño de la apuesta (en el HUD)
Las herramientas clásicas mezclan en un solo número el fold ante una apuesta de 25 % del bote y el fold ante una de 100 %, y así el número no dice casi nada.
- Facts nuevos por calle: `X_faced_size_bucket` (`small` < 40 %, `medium` 40–80 %, `large` 80–120 %, `over` > 120 %) y `X_bet_size_bucket` para las apuestas propias.
- Stats derivadas con el DSL: *Fold a CB pequeña / mediana / grande*, *Fold a overbet river*, etc.
- El popup Postflop (7.2) muestra una fila por tamaño. En el panel, "FCB" pasa a mostrar los dos valores extremos (`FCB 35·70` = pequeña·grande) si hay muestra suficiente; si no, el valor global.

### 19.2 Arquetipos aprendidos de tus propios datos
Las reglas fijas (Nit / TAG / Fish…) de 7.1 se sustituyen por **clusters aprendidos de tu base de datos**, por formato:
- k-means (k = 6 por defecto) sobre el vector de stats posteriores (6.3) de los rivales con ≥ 200 manos (≥ 50 en Spin).
- Cada cluster recibe automáticamente una etiqueta legible a partir de su centroide (ej. "Pasivo que paga", "Agresor de blinds") y un color.
- Los rivales con pocas manos se asignan al cluster más probable, con la confianza mostrada en el badge.
- Se recalcula una vez al día o a demanda. Es una clasificación de su historial, no un consejo.

### 19.3 Tendencias de población
Vista **Población** (solo análisis): cómo juega *el rival medio* de tu formato y límite en cada spot, con todos los facts y filtros del DSL. Ejemplos: "La población en Spin $5 hace fold al open-shove de 12 bb desde el BTN el 71 % de las veces". Incluye intervalos (6.3) y una comparación con tus propias frecuencias en el mismo spot.

### 19.4 Diagnóstico de tu equilibrio (fuerza de mano)
En las manos del héroe se conocen sus cartas, así que cada acción postflop se clasifica por fuerza:
- `hero_made_hand` con el evaluador de 6.5: nuts/fuerte, valor medio, marginal, proyecto, aire.
- Informe "¿Cómo construyes tus apuestas?": por calle y tamaño, qué porcentaje de tus apuestas son valor, proyecto y farol, y su resultado en bb.
- Detecta patrones explotables en **tu** juego: "Apuestas pequeño en river casi solo con valor medio" o "Tu check-raise en flop es 90 % valor".

### 19.5 Push/fold frente a Nash (ideal para Spin & Go)
Un **solver Nash de push/fold integrado y local** para estudiar después de jugar:
- **Heads-up**: equilibrio SB-shove / BB-call para stacks de 1 a 25 bb, con ante opcional. Se calcula por *fictitious play* sobre una matriz de equity 169×169, que se genera la primera vez con el evaluador y se guarda en disco.
- **3-handed** (BTN/SB/BB, formato Spin): el mismo método con 3 jugadores, con chip EV porque el Spin es winner-takes-all (18.1).
- **Informe "Tus desviaciones"**: en cada spot de push/fold que hayas jugado, compara tu decisión con la de Nash para ese stack. Muestra las manos donde más te alejas, el coste estimado en bb y el botón *"Enviar al Estudio"* (11).
- **Gráfico de rangos**: matrices 13×13 de Nash por stack, para repasar.
- Todo esto es material de estudio: **nunca se muestra en el HUD ni con mesas abiertas** (sección 2).

### 19.6 Selección automática de manos para repasar
En lugar de pedir al usuario que etiquete, el sistema propone cada día **5 manos** para el Estudio, puntuadas por:
- tamaño del bote en bb;
- desviación respecto a tu frecuencia habitual en ese spot (6.3);
- desviación respecto a Nash en push/fold (19.5);
- spots que caen dentro de un leak activo (10).

### 19.7 Diario de sesión
- Al iniciar y al cerrar la sesión, la app pregunta opcionalmente por tu estado: ánimo de 1 a 5, cansancio y una nota breve.
- Informe de correlaciones: winrate y tilt score (12) frente a ánimo, hora, duración y número de mesas. Por ejemplo: *"Después de 2 h tu bb/100 cae a −8"* o *"Con ánimo ≤ 2 tu VPIP sube 6 puntos"*.
- Todo con intervalos: sin muestra suficiente, se dice que aún no hay datos.

### 19.8 Informe semanal
- Cada lunes se genera un informe local (HTML en `%APPDATA%\VergilHud\reports`) con:
  - volumen, resultados y EV por formato;
  - leaks nuevos o resueltos;
  - progreso del Estudio;
  - ROI de Spin con intervalo (18.3);
  - mejores y peores manos.
- Se abre con un clic desde el Dashboard.

### 19.9 Objetivos
Objetivos semanales configurables: volumen (manos o torneos), minutos de Estudio y límites de bienestar. Se muestra el progreso en el Dashboard. Sin gamificación invasiva.

### 19.10 Calidad de producto
- **Asistente de primer arranque**:
  - detecta la carpeta de historiales y comprueba que el idioma es inglés y que el guardado está activado (analizando el archivo más reciente);
  - ofrece importar el historial antiguo;
  - elige los perfiles de HUD según los formatos detectados.
- **Fusión de nicks del héroe**: si has usado varios nicks, se agrupan bajo "Yo".
- **Interfaz en español e inglés** (recursos `.resx`), con tema oscuro y claro.
- **Sin conexión**: la app nunca abre una conexión de red (verificable: no hay ningún `HttpClient` en el código). Las actualizaciones son manuales, sustituyendo el exe.
- **Recuperación ante fallos**: si la app se cierra de golpe, al volver a abrirla se retoma la importación desde los offsets guardados. Los errores no controlados se escriben en el log y se muestra un diálogo sin perder datos.
