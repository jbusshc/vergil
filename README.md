# Vergil HUD

HUD y base de datos propios para PokerStars (Windows, .NET 8 + WPF + SQLite). Funciona 100 % sin conexión.
El plan completo está en [DESIGN.md](DESIGN.md); las decisiones tomadas por el camino, en [docs/DECISIONS.md](docs/DECISIONS.md).

## Uso
1. En PokerStars activa el guardado del historial de manos, con el **idioma en inglés** (el programa solo entiende ese formato).
2. Genera el ejecutable: `.\publish.ps1` → `dist\VergilHud.exe` (un único archivo, no necesita .NET instalado). Doble clic.
   También se puede lanzar desde el código: `dotnet run --project src/VergilHud.App`.
3. La primera vez, el asistente revisa la carpeta de historiales y te deja elegir entre importar todo el historial o empezar solo
   con las manos nuevas. Se puede volver a abrir desde **Ajustes**.
4. Al abrir una mesa, el HUD aparece cuando se completa la primera mano. **Clic** en un panel = detalle; **arrastrar** = moverlo.

Los datos se guardan en `%APPDATA%\VergilHud` (`hud.db`, `settings.json`, `hud-profiles.json`). Con la variable de entorno
`VERGIL_DATA_DIR` se puede usar otra carpeta.

## La aplicación
Menú lateral: **Dashboard · Jugadores · Informes · Gráficas · Sesiones · Torneos · Ajustes**.

- **Dashboard:** manos, bb/100, **EV bb/100** y suerte, neto, rake, horas y manos por hora; gráfica de resultado real frente a EV y
  últimas 30 sesiones. Periodo y formato elegibles.
- **Gráficas:** ganancias acumuladas con y sin showdown, EV de los all-ins y **banda de varianza** (±1σ y ±2σ de tu winrate: te dice si
  una racha es normal); rendimiento por hora del día y día de la semana; histograma de resultados por sesión.
- **Informes:** por posición, stack, número de jugadores, mes, límite, hora, día y formato; **matriz 13×13** de manos de salida
  (bb/100, % jugadas o nº de manos) con la lista de manos de cada casilla; exportación a CSV.
- **Sesiones:** una pausa de más de 30 minutos abre una sesión nueva; duración, manos, resultado, EV y mesas simultáneas.
- **Torneos:** ROI con su **intervalo de confianza** y una frase que dice si ya se puede distinguir de 0 %; ITM; resultados de Spin & Go
  (victorias, mano a mano alcanzado y ganado, multiplicadores, juego a 3 y a 2 jugadores).
- **EV de los all-ins:** cuando se ven las cartas de todos, se calcula lo que habrías ganado de media y la diferencia con el resultado real.
- **Importar historial antiguo:** con progreso, velocidad y cancelación (se puede reanudar).

## Qué enseña el HUD
- **Valores honestos** (por defecto): con pocas manos, el número se acerca a lo que hace la población en vez de enseñar "VPIP 100"
  tras 2 manos, se atenúa y lleva una **barra de fiabilidad** debajo. El modo **clásico** enseña x/n en crudo.
- **Color** según el estilo, **etiqueta de estilo** (Nit, TAG, LAG, Fish…) y **flechas ▲▼** cuando sus últimas 100 manos se salen de su histórico.
- **Perfil automático** según formato y tamaño de mesa (cash HU / 6-max / 9-max, Spin & Go, torneo). Editable en `hud-profiles.json`.
- En torneos y Spin: push/fold **según el stack actual** del rival. `FC 35·70` = fold a C-Bet pequeña · grande.
- **Popup** con Resumen, Por posición, Postflop (incluye fold según tamaño de apuesta) y Showdowns (la línea jugada en cada mano mostrada).
- **Modo Juego** (banner) mientras haya mesas abiertas, y **HUD por formato** configurable (por defecto, Zoom apagado).
- Si has jugado con varios nicks, se unifican como "tú".

## Estado
- **Fase 1** (hecha): importador en vivo, HUD en mesa.
- **Fase 2** (hecha): motor de estadísticas (100+ hechos por jugador y mano, lenguaje de estadísticas propio compilado a SQL, caché,
  migraciones, ejecutable único).
- **Fase 3** (hecha): estadística honesta, perfiles, popups, flechas, Modo Juego, políticas por formato, asistente, DPI por monitor.
- **Fase 4** (hecha): análisis (Dashboard, Informes, Gráficas, Sesiones, Torneos), evaluador de manos y equity, EV de all-ins,
  importación masiva. 232 pruebas.
- **Fase 5** (hecha): replayer de manos, vista Manos, notas con color, autonotas y etiquetas de mano.
- **Fase 6** (hecha): detector de leaks bayesiano con impacto, estudio con repaso espaciado (SM-2), análisis ICM posterior.
- **Fase 7** (hecha): solver Nash de push/fold (heads-up y 3-handed) con sus desviaciones, 5 manos diarias para repasar, equilibrio de tus apuestas,
  población y arquetipos aprendidos de tus rivales.
- **Fase 8** (hecha): tilt y límites de bienestar, bankroll y riesgo de ruina, diario, informe semanal y objetivos, editores de HUD y de estadísticas,
  atajos, exportar/importar configuración, copias de seguridad, registro de errores, benchmarks, español/inglés y tema claro/oscuro.
- Todas las fases del diseño están implementadas.

## Estructura
- `src/VergilHud.Core` – parser, hechos, estadísticas (DSL, bayes), perfiles de HUD, análisis, equity, base de datos e importador (sin UI).
- `src/VergilHud.App` – overlay, popups, menú y vistas de análisis.
- `tests/VergilHud.Tests` – `dotnet test`. Casos en `Fixtures/cases` (`.txt` + `.json` con los hechos esperados).
- `docs/FACTS.md` – definición de cada hecho.

## Estadísticas propias
Una estadística es un JSON (ver `src/VergilHud.Core/Stats/builtin-stats.json`):

```json
{ "id": "cbet_flop_ip_srp", "label": "CB IP", "numerator": "f_cbet && f_cbet_ip && !pf_3bet_pot",
  "denominator": "f_cbet_opp && f_cbet_ip && !pf_3bet_pot", "format": "pct0", "prior": { "strength": 15, "mean": 0.60 } }
```
Las expresiones solo pueden usar los hechos de `docs/FACTS.md`; cualquier otro nombre da un error con su posición.

## Verificar el aspecto sin abrir una mesa
- `VergilHud.exe --render "%APPDATA%\VergilHud\hud.db" salida` dibuja los paneles del HUD y los popups en PNG.
- `VergilHud.exe --render-analysis salida` dibuja las vistas de análisis con datos de demostración.
- `VergilHud.exe --view 3` abre la app directamente en una vista (0 Dashboard, 1 Jugadores, 2 Manos, 3 Informes, 4 Gráficas, 5 Sesiones, 6 Torneos, 7 Leaks, 8 Estudio, 9 Nash, 10 Población, 11 Bienestar, 12 Bankroll, 13 Ayuda, 14 Ajustes).
- `VergilHud.exe --render-replayer mano.txt salida` dibuja el replayer de la primera mano de un historial.
- `VergilHud.exe --selfcheck copia-de-hud.db informe.txt` ejecuta todos los análisis sobre una COPIA de tu base de datos y anota resultados y errores.

## Atajos y datos
- Ctrl+Alt+H oculta/muestra los paneles de las mesas; Ctrl+Alt+L fija/suelta los paneles (se pueden desactivar en Ajustes).
- Datos en `%APPDATA%\VergilHud`: `hud.db`, `settings.json`, `hud-profiles.json`, `leak-baselines.json` (rangos objetivo editables), `custom-stats.json`,
  `backups\` (una copia al día, se guardan 5), `reports\` (informe semanal HTML) y `logs\` (14 días).
- La app nunca abre una conexión de red (hay una prueba que lo vigila). Las actualizaciones son manuales.
