# Hoja de ruta de implementación (Persona 2)

**Arquitectura vigente:** [Arquitectura_Agente_Poker_Actualizada.pdf](Arquitectura_Agente_Poker_Actualizada.pdf). El detalle matemático está en [DISENO_AGENTE.md](DISENO_AGENTE.md).

**Método de trabajo:** se construye una parte, se explica, la entiendes y la compruebas tú (con un mini-quiz y un comando que la pone a prueba). Solo después se pasa a la siguiente.

**Estimaciones:** suponen 3–4 horas al día de trabajo conjunto, incluido el tiempo para entender. No incluyen las horas de CPU del entrenamiento, que pueden correr de noche.

| # | Parte | Componentes (nombres del PDF) | Depende de | Construir | Entender | Cómo sabemos que está bien | Estado |
|---|---|---|---|---|---|---|---|
| 1 | Seguridad y respaldo (**V0**) | `ActionValidator`, `FallbackPolicy`, `TimeBudgetController` | — | hecho | 1–2 h | 25 pruebas OK; 0 folds gratis; 0 errores; p99 < 1 ms | ✅ construida, ⏳ por entender |
| 2 | Cartas y fuerza de mano | `CardUtil`, `HandStrengthCalc` | 1 | hecho | 2–3 h | HS = enumeración manual; draws en 10 casos a mano | ✅ construida, ⏳ por entender |
| 3 | Adaptador y codificador de estados | `SdkObservationAdapter`, `StateEncoder` | 2 | 1–2 días | 3–4 h | Clave determinista; umbrales numéricos fijados; recuento **real** de estados válidos; datos ausentes cubiertos | ⬜ |
| 4 | Simulador propio + validación | `LhGame` (CreateInitialState, CloneState, GetLegalActions, ApplyAction, …) | 3 | 2–3 días | 3–4 h | Mismas cartas (`StackDeckForNextHand`) y mismas acciones que el SDK → mismo bote, turnos, ganador y reparto en 10⁵ manos; **con stacks y all-in** | ⬜ |
| 5 | ES-MCCFR en miniatura (Kuhn poker, 3 cartas) | `EsMccfrTrainer` genérico | — | 1 día | 4–6 h | Converge al valor −1/18 conocido | ⬜ |
| 6 | ES-MCCFR sobre el simulador real | Entrenador + checkpoints | 4, 5 | 2–3 días | 2–3 h | Curva de estabilidad δ; % de estados visitados; iteraciones/s | ⬜ |
| 7 | Política embebida (**V1**) | `CfrPolicy`, exportador | 6 | 1 día | 2 h | Tamaño **medido**; versión, máscara e integridad verificadas; V1 frente a V0 | ⬜ |
| 8 | Modelo de rivales | `OpponentModel` | 1 | 1 día | 2–3 h | Detecta a Station, Raiser y CheckFold en 30 manos; priors en muestras pequeñas | ⬜ |
| 9 | Rangos | `RangeEstimator` | 2, 8 | 1–2 días | 3 h | Pesos ≥ 0, combinaciones imposibles eliminadas, normalizados | ⬜ |
| 10 | Monte Carlo | `MonteCarloEvaluator` | 9 | 1 día | 2 h | AA frente a una mano aleatoria ≈ 0,852; respeta el presupuesto de tiempo | ⬜ |
| 11 | EV con incertidumbre (**V2**) | `ExpectedValueEstimator` | 10 | 2 días | 3–4 h | EV, error y **covarianza medida** en escenarios emparejados | ⬜ |
| 12 | Orquestador (**V3 / V3b**) | `ExploitPolicy` / `AdaptivePolicySelector` | 7, 11 | 1–2 días | 3 h | λ = 0 sin datos; LCB de ΔEV; V3 frente a V3b | ⬜ |
| 13 | Torneo (**V4-T1**, opcional **T2**) | `TournamentContext` | 12 | 1–2 días (+1 T2) | 2 h | Escenarios forzados: proteger ventaja, recuperar, final | ⬜ |
| 14 | Endurecimiento y entrega | Todo | 13 | 1 día | 1 h | Checklist del kit; percentiles de latencia; nombre registrado | ⬜ |

**Total aproximado:** 17–25 días de trabajo, unas 4–5 semanas a 3–4 h/día. La Persona 3 puede empezar a comparar versiones desde la Parte 7 (V0 frente a V1).

## Plan reducido ejecutado (plazo < 5 h) y resultados

Las 14 partes se comprimieron en el plan A–F. Archivo entregable: `Agents/AgenteHibrido.cs` (1 144 líneas, 62 KB, de los cuales la política ocupa unos 9,5 KB).

| Parte | Qué se hizo | Verificación |
|---|---|---|
| A | `StateEncoder`: 3 564 claves (preflop 12 buckets; flop y turn 6 HS × proyecto; river 6) × rivales detrás × rivales activos × apuestas × enfrentando | Compila con las reglas de la Arena |
| B | Simulador `LhGame` + validación diferencial contra el SDK | **20 000 manos y 292 367 decisiones: 0 diferencias** (turnos, ciegas, legalidad, neto y clave) |
| B/C | ES-MCCFR (`Entrenamiento/Cfr/EsMccfr.cs`), 8 hilos, ~33 500 repartos/s; **62,3 M repartos** en 31 min; política embebida con versión, huella y suma de control | 1 584 claves con política (el resto son, en su mayoría, combinaciones imposibles) |
| D | `OpponentModel` (VPIP, PFR, AFq, FTB con priors) + Monte Carlo emparejado + EV + ΔEV con varianza **medida** + λ por LCB + V3 / V3b | — |
| E | `TournamentContext` T1: normal / proteger ventaja / recuperación / crítica / final | — |
| F | Comparación emparejada de 1 000 mesas por versión; checklist del kit | Ver abajo |

**Comparación de versiones** (`bash entrenar.sh comparar 1000`). Δ de Clasif% frente a V0 ± IC95 % pareado; ✅ = significativo:

| Versión | Heuristic×5 | Mixto (2 Heur, Station, Raiser, Random) | Heads-up contra Heuristic (Jefe) |
|---|---|---|---|
| V0 heurística | 59,4 % | 65,4 % | 65,6 % |
| V1 CFR | +1,3 ± 3,0 | −0,7 ± 3,4 | **+4,3 ± 3,0** ✅ |
| V2 explotador | +0,3 ± 1,3 | **+5,8 ± 2,8** ✅ | +0,3 ± 1,5 |
| V3 lineal | −0,7 ± 3,0 | −0,8 ± 3,4 | **+3,5 ± 3,0** ✅ |
| V3b ajuste EV | +1,3 ± 2,9 | +2,2 ± 3,3 | **+5,0 ± 3,0** ✅ |
| V4 (V3 + T1) | −0,3 ± 2,9 | −0,2 ± 3,4 | **+3,4 ± 3,1** ✅ |

Frente a V3b: V4b (V3b + T1) = +0,3 ± 0,7 / +1,9 ± 2,7 / +0,0 ± 0,5, y λ máx 0,6 = −0,5 / +2,4 / −0,4. **Configuración entregada: V4b** (CFR + explotador V3b con λ máx 0,4 + torneo T1).

**Checklist del kit** (bench de 200 mesas con timeout real de 1000 ms, semilla 42): `check` OK sin avisos; **0 timeouts y 0 errores** en todos los pools; `ms máx` entre 5,1 y 10,6 ms (límite 300); 25/25 pruebas unitarias.

| Pool | Clasif% | Gana% |
|---|---|---|
| Heuristic×5 | 57,5 | 16,5 |
| Raiser×5 | 62,0 | 6,5 |
| Mixto | 65,0 | 10,0 |
| Heads-up | 71,5 | 71,5 |
| Final (`--final`, Heuristic×5) | 14,5 | 14,5 |

**Experimento posterior: abstracción v2 + más entrenamiento + purificación** (2026-11-19).

- **Abstracción v2:** 56 736 claves finas. Usa las 169 manos preflop exactas y añade "¿ya agredí en esta calle?", "último agresor de la calle anterior" y "tamaño del bote". Tiene respaldo a la clave gruesa v1 cuando la fina tiene menos de 50 visitas.
- **Entrenamiento:** 242,7 M repartos en 2 h. Hubo política fina para 14 312 claves.
- **Validación:** simulador frente a SDK, 0 diferencias.
- **Resultado** (1 000 mesas emparejadas, Δ frente a la V4b v1):

| Variante v2 | Heuristic×5 | Mixto | Heads-up |
|---|---|---|---|
| Sin purificar | −1,8 ± 2,8 | −1,1 ± 2,9 | **−3,1 ± 2,6** ❌ |
| Purificación 0,05 | −0,8 ± 2,8 | −1,8 ± 3,0 | **−3,1 ± 2,6** ❌ |
| Purificación 0,10 | +0,2 ± 2,8 | −2,6 ± 2,9 | **−3,2 ± 2,6** ❌ |

**Conclusión:** no mejora; en heads-up empeora de forma significativa. La explicación más probable es que, con 16 veces más claves, cada una recibe menos entrenamiento (solo el 25 % llegó a 50 visitas) y la política queda más ruidosa que la v1. Se **mantiene la V4b v1** como entrega. La v2 se conserva en `Agents/AgenteHibridoV2.cs` (el laboratorio entrena sobre ella con `entrenar-cfr`) como trabajo futuro: necesita bastante más entrenamiento.

### Ronda de mejoras del 2026-11-19

El comparador se rehízo para ser 100 % reproducible: el `RandomAgent` lleva semilla y BASE frente a sí mismo da Δ = 0,0. Tiene 7 mesas:

1. Heuristic×5
2. Mixto
3. Heads-up final
4. 3 jugadores
5. 4 jugadores
6. Perfiles (`Entrenamiento/Bots/ProfileBot.cs`)
7. Mesa final de 6

Cada resultado es de 1 000 mesas emparejadas: Δ = diferencia de Clasif% ± IC95 %.

**Entrega actual = V4b + T2 solo en la mesa final.**

| Mesa | Agente entregado | Azar |
|---|---|---|
| 3 jugadores (top 2) | 80,1 % | 66,7 % |
| 4 jugadores | 77,9 % | 75 % |
| Perfiles | 56,2 % | 50 % |
| Mesa final de 6 (gana el 1.º) | **17,2 %** | 16,7 % |

**Aporte de cada módulo** (quitándolo): sin explotador, −3,0 ± 2,0 en la mesa de 4 (significativo); sin T1, empates.

**Capa T2** (P(clasificar) con modelo de carrera normal):

| Mesa | Efecto de T2 |
|---|---|
| Mesa final de 6 | **+2,6 ± 1,5** ✅ |
| Mixto clasificatorio | +7,3 ± 2,4 |
| Heuristic×5 clasificatorio | **−4,2 ± 2,3** |
| Heads-up | Desactivada con menos de 3 jugadores |

Se activa **solo en la mesa final**. Comprobado: en las demás mesas el agente es idéntico (Δ = 0,0).

**Mejoras del explotador probadas** (quedan en el código apagadas: `UseShowdownInfo`, `UseReraiseEv`, `UseDriftCheck`):

| Variante | H×5 | Mixto | HU | 4 jug | Perfiles | Final 6 | Veredicto |
|---|---|---|---|---|---|---|---|
| Fuerza del showdown (faroles) | −1,5 | −0,8 | −1,5 | +0,1 | +1,6 | **−2,2 ± 1,5** ❌ | Descartada |
| EV con resubidas | +0,1 | −0,4 | +0,5 | +0,1 | +0,6 | +0,4 | Tendencia positiva, no significativa |
| Estabilidad del rival | +0,1 | −0,3 | 0,0 | −0,3 | 0,0 | 0,0 | Sin efecto (en 30 manos casi no detecta cambios) |
| Rangos finos + FTR preflop (AgenteHibridoV3, del compañero) | +0,7 | — | −1,2 | — | — | — | Empate |

**CFR v3 (abstracción intermedia):** 17 928 claves; v1 + "¿ya agredí en esta calle?" + "último agresor de la calle anterior". Se entrenó con **Linear CFR** durante 4 h: **386,8 M repartos**, 33,8 % de claves finas con política.

| H×5 | Mixto | HU | 4 jug | Perfiles | Final 6 (sin T2 en ambos) |
|---|---|---|---|---|---|
| +0,8 ± 2,4 | **−5,2 ± 2,5** ❌ | −2,2 ± 2,5 | **−3,0 ± 2,3** ❌ | +2,8 ± 2,7 | +1,1 ± 1,9 |

**Conclusión:** tampoco mejora, ni siquiera con un entrenamiento abundante por clave. La abstracción v1, más simple, generaliza mejor contra estos rivales. `AgenteHibridoV2.cs` contiene ahora la v3 con su política y `cfr_checkpoint.bin` su estado.

**Duelo entre versiones del CFR, sin bots** (`bash entrenar.sh duelo`, `duelo3`, `torneo3`). Las tres versiones llevan el mismo explotador y la misma capa de torneo:

- v1 = entrega (3 564 claves, 62 M manos)
- v2 = 56 736 claves, 242 M manos (`Agents/AgenteHibridoV2Old.cs`, recuperada del historial)
- v3 = 17 928 claves + Linear CFR, 387 M manos

| Prueba | v1 | v2 | v3 | Azar |
|---|---|---|---|---|
| Heads-up duplicado (2 000 partidas por pareja) | gana el 45,1 % contra v2 y el 45,8 % contra v3 | gana el 53,0 % contra v3 | — | 50 % |
| Mesa de 3 (3 000 mesas, top 2), Clasif% | **63,3 ± 1,7** | **69,2 ± 1,7** | 67,5 ± 1,7 | 66,7 % |
| Mesa de 6 (2 copias de cada una, 1 000 mesas), Clasif% | **47,3 ± 2,2** | **52,1 ± 2,2** | 50,5 ± 2,2 | 50 % |
| Mini-torneo (ronda de 3 + final HU, 3 000 torneos), % campeón | **29,3 ± 1,6** | **37,8 ± 1,7** | 32,8 ± 1,7 | 33,3 % |

**Conclusión:** entre ellas, **la v2 es la más fuerte y la v1 la más débil**, justo al revés que contra los bots del kit. Es la intransitividad típica del póker:
- una estrategia más cercana al equilibrio (más detalle y más entrenamiento) **gana a estrategias más toscas**;
- pero **no aprovecha tanto** a rivales con fallos fijos (Station, Raiser), que la v1 explota mejor.

La elección de la entrega depende de cómo sean los rivales del torneo. Se decide probando contra agentes reales de compañeros.

### Evaluación final: v1, v2 y v2+ (2026-11-19, noche)

**v2+** = la v2 entrenada más con **ES-MCCFR puro** (sin Linear CFR): de 242,7 M a **578,1 M repartos**, 2 h 30 min con 10 núcleos (`Agents/AgenteHibridoV2.cs`, hoy con T2). La estrategia promedio cambió solo un 5,6 % en la sesión, así que se está estabilizando. Copias por bloque fuera del repositorio (357 M, 473 M, 578 M).

**v2+ frente a v2** (las dos con T2; Δ Clasif% ± IC95 % emparejado):

| Mesa | H×5 | Mixto | HU final | 3 jug | 4 jug | Perfiles | Final 6 | Con costonito | Final con costonito |
|---|---|---|---|---|---|---|---|---|---|
| Δ | **+2,3 ± 1,8** ✅ | −0,4 | +0,4 | +1,1 | 0,0 | **+2,2 ± 1,7** ✅ | −0,3 | +0,8 | −1,8 ± 2,3 |

Heads-up v2 contra v2+: 50,0 % (empate). Mesa de 6 con 2 copias de cada versión: v1 47,6 %, v2 52,4 %, v2+ 50,0 %.

**Contra un agente real de compañeros (costonito)**: Monte Carlo + gestión del riesgo según el puesto + bandido UCB1 + tabla Q.

| Prueba | v1 | v2 | v2+ | costonito |
|---|---|---|---|---|
| Heads-up contra costonito (600 partidas) | 41,7 % | 39,2 % | 39,3 % | — |
| Heads-up contra Heuristic | — | 67,2 % | 67,7 % | 67,8 % |
| Mesa de 3 (con Heuristic): clasifica / gana | 68,1 / 42,7 | 69,8 / 41,0 | — | ≈74 / 31–34 |
| Mesa de 6 (v1, v2, costonito, 3 Heur): clasifica / gana | 52,0 / 15,8 | 55,3 / **19,3** | — | **60,2** / 14,2 |
| Mini-torneo (ronda de 3 + final): % campeón | 38,8 | 38,1 | 35,8 | 42,6–44,6 |

Lectura:
- costonito **clasifica más** porque protege mucho cuando ya va clasificado, y **gana mano a mano**.
- Nuestro agente **gana más mesas** y supera al Jefe igual que costonito.
- Las tres versiones nuestras están dentro del margen entre sí contra costonito.

**Decisión (regla del equipo: no peor en ninguna mesa; mejor resultado medio): entregar la v2+ con T2.** Motivos:
- Supera a la v1 entre agentes (duelos, mesas de 3 y 6, mini-torneo).
- Supera a la v2 en 2 mesas y no empeora en ninguna.

**Checklist del kit** (v2+ con T2, 200 mesas, timeout de 1 000 ms):

| Mesa | Clasif% |
|---|---|
| H×5 | 57,5 % |
| Raiser×5 | 61,5 % |
| Mixto | 62,5 % |
| HU | 67,0 % |
| Final | 18,0 % |

`check` OK, 0 timeouts, 0 errores, `ms máx` ≤ 8,2 ms.

### T1 graduado (gestión del riesgo según el puesto) sobre la v2+ (2026-11-19, noche)

**Motivación:** costonito clasificaba ~8 pp más que nosotros. Su clave es exigir más equity cuando va clasificado y menos cuando va fuera, cada vez más al avanzar la mesa. Se diseñó una regla **general**, que no depende de ningún rival concreto:

$$\text{margen} = \text{GradedMax}\cdot \text{fase}\cdot\tanh\!\Big(\frac{\text{fichas} - \text{fichas del corte}}{\sigma\sqrt{2\,\text{manos restantes}}}\Big)$$

- margen > 0: subir solo con equity > 0,5 + margen, y pagar solo con equity > pot odds + margen;
- margen < 0: retirarse solo si la equity no llega a pot odds + margen.

Se aplica solo en rondas clasificatorias; en la mesa final sigue T2.

| Mesa (1 000 mesas emparejadas; costonito 500) | v2+ sin graduado | Graduado 30 % | Graduado 15 % |
|---|---|---|---|
| Heuristic×5 | 61,5 % | **+3,0 ± 2,6** | +2,7 ± 2,5 |
| Mixto | 66,8 % | **+10,9 ± 2,8** | +11,4 ± 2,7 |
| 3 jugadores | 78,9 % | **+3,9 ± 2,0** | +3,8 ± 1,9 |
| 4 jugadores | 76,7 % | **+10,3 ± 2,5** | +8,7 ± 2,3 |
| Perfiles | 59,8 % | **+4,4 ± 2,6** | +4,9 ± 2,5 |
| costonito + 4 Heuristic | 57,8 % | **+5,8 ± 3,9** | +4,6 ± 3,6 |
| Mesas finales (HU y 6) | — | **0,0 exacto** (no actúa) | — |

**Contra costonito:**

| Prueba | v2+ sin graduado | v2+ graduado 30 % | costonito |
|---|---|---|---|
| Mesa de 3: clasifica / gana | 70,4 / 38,8 | **74,3 / 40,6** | 71,9 / 34,3 |
| Mesa de 6 (con v2+, 3 Heur): clasifica / gana | 52,3 / 15,0 | **59,7 / 19,0** | 60,0 / 15,2 |
| Mini-torneo: pasa la ronda / campeón | 69,9 / 35,8 | **74,6 / 39,1** | 72,2 / 43,9 |

**Decisión:** T1 graduado activado por defecto con un margen máximo del 30 %. Mejora en todas las mesas y no empeora en ninguna. Con él, **clasificamos igual o más que costonito y ganamos más mesas**. costonito solo sigue por delante en las finales mano a mano.

**Checklist** (v2+ con T1 graduado + T2, 200 mesas, timeout de 1 000 ms):

| Mesa | Clasif% |
|---|---|
| H×5 | 65,0 % |
| Raiser×5 | 67,5 % |
| Mixto | 77,0 % |
| 3 jug | 83,0 % |
| HU | 71,5 % |
| Final | 14,5 % |

0 timeouts, 0 errores, `ms máx` 31,3 ms.

**Tiempos** (bench oficial, timeout de 1 000 ms, CPU cargada): v2 tuvo 61,8 ms máx y v3 20,4 ms máx, ambos con 0 timeouts y 0 errores.

**Robustez bajo estrés** (CPU cargada por el entrenamiento, timeout real de 1 000 ms): 0 timeouts y 0 errores; `ms máx` 17,5 ms con Raiser×5.

**Limitaciones honestas** (para el informe):
- La abstracción es gruesa (3 564 claves), aunque las versiones más finas probadas no mejoraron.
- Los rangos son simples.
- El EV no modela resubidas (la variante que lo hace no es significativa).
- T2 solo ayuda de forma fiable en la mesa final.
- Todo se ha medido contra bots, no contra agentes de compañeros.

**Trabajo futuro:**
- Seguir entrenando la abstracción v1 (estaba aún cambiando a los 62 M).
- Probar contra agentes reales.
- Confirmar el EV con resubidas con más mesas.

### Torneo de 100 manos: ajuste fino del T1 graduado (2026-11-19, noche)

**Cambio de reglas:** las mesas del torneo pasan de 30 a **100 manos**. Todo se repitió a 100 manos (`--manos 100`). Δ = diferencia de Clasif% en puntos contra la configuración actual, con un IC del 95 %. Hubo 1 000 mesas emparejadas por pool y 300 en la de costonito.

**Configuración actual** (`GradedMax = 0,30`, fase lineal, σ = 150): H×5 71,5 %, Mixto 87,2 %, Perfiles 71,4 %, costonito + H×4 70,3 %.

**Quitar el T1 graduado a 100 manos** (`V2NOG`): H×5 −2,2 ± 2,7, Mixto **−15,0 ± 2,8**, Perfiles −3,2 ± 2,7, costonito −5,3 ± 5,6. ⇒ Con mesas más largas el graduado sigue siendo necesario.

**Rejilla 1 (más protección o protección más temprana):**

| Variante | Qué cambia | H×5 | Mixto | Perfiles | costonito |
|---|---|---|---|---|---|
| GP | margen ×(1 + 0,5·jugadores extra) | −0,4 | +0,8 | −0,6 | — |
| GS | fase^0,5 (protege antes) | −1,2 | −1,6 | −1,7 | — |
| GPS | GP + GS | −3,1 | −0,2 | −2,5 | −4,7 |
| G40PS | GPS con máximo del 40 % | −1,1 | +0,1 | −2,2 | −2,0 |
| G50PS | GPS con máximo del 50 % | −2,5 | −1,2 | −3,7 | — |
| GPSO | GPS con σ medida en línea | −1,6 | −0,4 | −0,8 | −3,3 |

**Rejilla 2 (menos protección o protección más tardía):**

| Variante | Qué cambia | H×5 | Mixto | Perfiles | costonito |
|---|---|---|---|---|---|
| G20 | máximo del 20 % | +0,5 | −0,4 | −0,7 | — |
| GL | fase² (protege más tarde) | +0,8 | −0,3 | +0,1 | −2,0 |
| G20L | G20 + GL | +0,5 | −2,5 | +0,4 | — |
| G40L | máximo del 40 % + fase² | 0,0 | −0,4 | +0,3 | −2,3 |
| GLO | GL con σ medida en línea | −0,4 | −2,6 | −0,9 | — |

Los IC de Δ son de unos ±2 pp en los pools de bots y de unos ±5 pp con costonito.

**Decisión:** ninguna de las 11 variantes mejora de forma significativa en ningún pool. Varias empeoran: GPS, G50PS, G20L y GLO en Mixto. Por la regla del equipo (no perder en ningún pool y tener la mejor media) **se mantiene la configuración actual**: `GradedMax = 0,30`, `GradedPhaseExp = 1`, `GradedSigma = 0` (σ = 150).

**Confirmación en mesas pequeñas** (100 manos, 1 000 mesas emparejadas):

| Mesa | Con graduado | Sin graduado | Δ |
|---|---|---|---|
| 3 jugadores (pasan 2) | 89,2 % | 88,5 % | +0,7 ± 1,8 |
| 4 jugadores (pasan 3) | **95,3 %** | 87,3 % | **+8,0 ± 2,0** |

**Checklist a 100 manos** (`bash run.sh bench ... --hands 100 --timeout 1000`):

| Mesa | Mesas | Clasif% (azar) | Gana% | ms máx |
|---|---|---|---|---|
| Heuristic×5 (pasan 3) | 200 | **68,5 %** (50 %) | 16,5 % | 7,5 |
| Final de 6 (`--final`, pasa 1) | 100 | **25,0 %** (16,7 %) | 25,0 % | 9,2 |

`check`: OK. Hubo **0 timeouts y 0 errores**. En las dos mesas el agente es el que más clasifica.

### Entrenamiento nocturno: 578 M → 1 610 M (2026-11-20) — descartado

Se continuó con ES-MCCFR puro durante 7 h 22 min, con 10 hilos, a unos 39 000 repartos/s: **1 610 273 035 repartos**. La estrategia cambió poco (δ = 0,065). Se comparó contra el agente de 578 M con el mismo T1 graduado y T2, a 100 manos (Δ = 1 610 M − 578 M):

| Mesa | 578 M | 1 610 M | Δ |
|---|---|---|---|
| Heuristic×5 | 71,5 % | 71,9 % | +0,4 ± 2,1 |
| Mixto | 87,2 % | 87,0 % | −0,2 ± 2,1 |
| HU final | 80,7 % | 78,5 % | **−2,2 ± 2,0** |
| 3 jugadores | 89,2 % | 88,0 % | −1,2 ± 1,8 |
| 4 jugadores | 95,3 % | 94,1 % | −1,2 ± 1,3 |
| Perfiles | 71,4 % | 71,2 % | −0,2 ± 2,3 |
| Final de 6 | 22,6 % | 23,8 % | +1,2 ± 1,8 |
| costonito + H×4 (500) | 69,4 % | 66,8 % | −2,6 ± 3,5 |
| Final con costonito (500) | 26,6 % | 27,4 % | +0,8 ± 3,1 |

La media ponderada es **−0,6 ± 0,7**.

**Contra costonito (100 manos):**

| Prueba | 578 M | 1 610 M | costonito |
|---|---|---|---|
| HU duplicado (1 000 partidas) | 50,7 % | 49,1 % | — |
| Duelo directo 1 610 M vs 578 M | — | 48,0 ± 3,1 % | — |
| Mesa de 6 (ambos + costonito + 3 H, 1 000): clasifica / gana | **64,1 / 23,6** | 63,5 / 21,7 | 55,8 / 10,8 |
| Mini-torneo (1 500): pasa la ronda / campeón | **79,6 / 46,7** | 78,7 / 44,3 | ~71,5 / ~41 |

**Decisión:** **se mantiene el agente de 578 M**. El de 1 610 M pierde de forma significativa en el HU final y no gana en ninguna mesa. Las explicaciones probables son tres:
- la política se sobreajusta a la abstracción: mejorar en el juego abstracto no implica mejorar en el real;
- el T1 graduado, T2 y el explotador se calibraron sobre la política de 578 M;
- converger al equilibrio no maximiza lo que se gana contra rivales débiles.

Las pérdidas se concentran en las mesas pequeñas (HU, 3 y 4 jugadores), donde la política CFR pesa más.

**Conclusión para el informe:** el entrenamiento ya está saturado para esta abstracción. Las mejoras grandes vinieron de las reglas de torneo: el T1 graduado da de +3 a +15 pp, frente a ≤ 1 pp por triplicar el entrenamiento. A 100 manos el agente **le gana a costonito** en mesa de 6 (+8 pp de clasificación) y en el mini-torneo (+6,7 pp de campeón), y empata con él en el HU.

La política de 1 610 M y sus checkpoints quedan archivados fuera del repo (`checkpoints/noche/`). Tras restaurar la de 578 M: `check` OK y bench Heuristic×5 a 100 manos con 70,0 % de clasificación, 0 timeouts y 0 errores.

## Verificaciones pendientes del PDF (§15) ya resueltas

| Pendiente | Resultado | Cómo se comprobó |
|---|---|---|
| Cadenas del comprobador (`File.`, `Process.`, `Parallel.`, …) | **Confirmado.** El workbench avisa si el archivo contiene cualquiera de estas subcadenas: `System.IO`, `File.`, `Directory.`, `System.Net`, `HttpClient`, `Socket`, `new Thread`, `Thread.Sleep`, `Task.Run`, `Parallel.`, `Process.`, `Environment.Exit`, `System.Reflection`, `Console.ReadLine` | `tools/AgentsPoker.Sim/AgentLoader.cs` líneas 42–46 y 163–164 |
| ¿Se pueden fijar las cartas para validar el simulador? | **Sí:** `PokerTable.StackDeckForNextHand(Card[])` | `lib/AgentsPoker.Core.xml` |
| Frecuencia real de all-in | Pools realistas: **0** en 90 000 manos. Peor caso (Raiser×3 + Station×3): **32,5 % de las mesas** (1,18 % de las manos), con botes laterales. ⇒ el simulador **debe** manejar stacks y all-in | `bash entrenar.sh medir-allin 3000` |
| 154 008 estados y 400 KB | Siguen siendo estimaciones. Se medirán en las Partes 3 y 7 | — |

## Comandos útiles

Antes de los demás comandos, hay que poner .NET en el PATH (en cada terminal nueva):

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
```

Desde `Kit_AgentsPoker_SDK/`:

| Comando | Para qué |
|---|---|
| `bash entrenar.sh pruebas` | Pruebas unitarias |
| `bash run.sh check Agents/AgenteHibrido.cs` | Compilar como la Arena |
| `bash run.sh match --agent AgenteHibrido --builtin 'Heuristic*3' --hands 5 --verbose` | Ver manos una a una |
| `bash run.sh bench --agent AgenteHibrido --builtin 'Heuristic*5' --matches 200 --seed 42` | Bench del checklist |
| `bash entrenar.sh 30` | Laboratorio: bb/100, latencias p50/p95/p99, Clasif% |
| `bash entrenar.sh medir-allin 3000` | Frecuencia de all-in |
| `bash entrenar.sh calibrar-preflop` | Regenerar la tabla preflop |
