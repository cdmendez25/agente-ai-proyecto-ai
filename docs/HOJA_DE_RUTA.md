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

**Limitaciones honestas** (para el informe): la abstracción es gruesa (3 564 claves); los rangos son simples (filtro preflop por VPIP/PFR y umbral FTB); el EV no modela resubidas; T1 no mostró una ganancia significativa; en la mesa final contra 5 Heuristic el agente gana ≈14,5 % (el reparto uniforme sería 16,7 %). Quedan como trabajo futuro: abstracción fina, más entrenamiento, rangos por combinación y T2.

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
