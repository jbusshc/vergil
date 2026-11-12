# Definición de los hechos (`player_hand_facts`)

Una fila por **jugador y mano** (solo quien actúa, pone ciegas/ante o es el héroe; un asiento `sitting out` sin acciones no
cuenta). Toda estadística es `SUM(numerador) / SUM(denominador)` sobre estas columnas. La lista exacta está en
`Facts/FactCatalog.cs`; el cálculo, en `Facts/FactExtractor.cs`.

Convención: `X_opp` = "tuvo la oportunidad" y `X` = "la aprovechó". `X` nunca vale 1 sin `X_opp` (lo comprueban los tests).
Cada oportunidad se cuenta **una sola vez por jugador y mano**.

## Contexto
| Hecho | Significado |
|---|---|
| `position` | UTG, UTG1, UTG2, MP, HJ, CO, BTN, SB, BB. En mano a mano el botón es `SB`. |
| `format` | `cash`, `zoom`, `spin` (torneo en mesa de 3) o `mtt` (otros torneos). |
| `players_dealt` | Jugadores que participan en la mano. |
| `stack_bb`, `stack_bucket` | Stack inicial en ciegas grandes; cubos `<10`, `10-20`, `20-40`, `40+`. |
| `eff_stack_bb` | `min(su stack, el mayor stack rival)` en ciegas. |
| `m_ratio` | Stack / (SB + BB + ante × jugadores). |
| `hole_cards` | Cartas del héroe o las que se mostraron. |

## Preflop
- **`pf_vpip`**: paga o sube. **`pf_pfr`**: sube.
- **`pf_open`**: sube siendo el primero en entrar (nadie ha subido ni limpeado; no vale quien puso la ciega grande).
- **`pf_steal`**: `pf_open` desde CO, BTN o SB. **`pf_fts`**: se retira a un robo desde SB/BB sin que nadie haya pagado antes.
- **`pf_3bet`**: sube cuando hay una única subida delante y no es suya. `pf_resteal`: lo mismo contra un robo.
  `pf_3bet_vs_steal`: solo desde las ciegas. `pf_squeeze`: 3-bet con al menos un pago intermedio.
- **`pf_f3b`**: el que abrió se retira al 3-bet. **`pf_4bet`**: el que abrió vuelve a subir. **`pf_f4b`**: el 3-beteador se retira al 4-bet.
  `pf_5bet`: sube cuando ya hay un 4-bet.
- **`pf_cold_call`**: paga una subida sin haber puesto dinero antes (las ciegas quedan fuera).
- **`pf_bb_defend`**: la BB paga o sube ante una subida. `pf_sb_complete`: la SB completa una ciega sin subidas.
- **`pf_limp`**: paga siendo el primero (sin subidas delante). `pf_limp_call` / `pf_limp_raise`: tras limpear se enfrenta a una subida.
- **Contexto del bote**: `pf_raised_pot` (≥1 subida), `pf_3bet_pot` (≥2), `pf_4bet_pot` (≥3), `pf_limped_pot`.
- **Torneo / stacks cortos**: `pf_shove` (open all-in siendo el primero en entrar), `pf_call_shove` (paga un all-in),
  `pf_reshove` (all-in contra una subida).

## Flop (`f_`), turn (`t_`), river (`r_`)
- **Agresor** de una calle: flop = el último que subió preflop; turn/river = el último que apostó o subió en la calle anterior
  (ninguno si se pasó). Un jugador all-in no cuenta como agresor pendiente de actuar.
- `X_saw`: llega a esa calle con el tablero repartido. `X_multiway`: 3 o más jugadores. `X_ip`: último en actuar de los que llegan.
- `X_cbet`: el agresor apuesta primero. En turn/river es el **barril**. `X_cbet_ip` / `X_cbet_oop`: tuvo la oportunidad en
  posición / fuera de posición.
- `X_fcb`: se retira a esa apuesta (solo antes de que alguien suba). `X_raise_cb`: la sube.
- `X_donk`: apuesta antes de que actúe el agresor. `X_float_bet`: apuesta tras pasar el agresor. `X_probe` (turn/river): apuesta tras
  una calle sin apuestas y antes de actuar el agresor preflop.
- `X_check_raise`: tras pasar, se enfrenta a una apuesta y sube.
- Contadores: `X_bets`, `X_raises`, `X_calls`, `X_checks`, `X_folds`.
- **Tamaños.** Apuesta propia: `X_bet_size_pct` = % del bote antes de apostar (primera apuesta del jugador en la calle),
  `f_cbet_size_pct` para la C-Bet del flop. Apuesta rival: `X_faced_size_bucket` = lo que hay que igualar / el bote antes de esa
  apuesta. Cubos (`X_bet_size_bucket`, `X_faced_size_bucket`): `small` < 40 %, `medium` 40–80 %, `large` 80–120 %, `over` > 120 %.

## Resultado
- `wtsd`: ve el flop, hay showdown y no se retiró. `wsd`: además cobra algo. `wwsf`: ve el flop y cobra algo.
- `net` / `net_bb`: cobrado + devuelto (apuesta sin pagar) − invertido (ante, ciegas y apuestas). En una mano suman 0 menos el rake.
- `rake_paid`: reparto del rake entre quienes cobran, proporcional a lo cobrado.
- `went_allin`, `pf_allin`.
- `ev_net_bb`: EV de all-in en bb (ver docs/DECISIONS.md, Fase 4). Hasta que se calcula (o si la mano no tiene un all-in "limpio" con cartas a la vista) vale lo mismo que `net_bb`.
- Aún sin rellenar: `hero_made_hand` (fuerza de mano, Fase 7) e `is_bubble` (burbuja, requiere conocer el número de jugadores del torneo).
