# Diseño técnico del agente híbrido de Limit Texas Hold'em

**Proyecto:** Fundamentos de Inteligencia Artificial. Agente para AgentsPoker SDK v0.4.0
**Arquitectura:** ES-MCCFR (estrategia base) + explotador (modelo de rivales, equity Monte Carlo y EV) + orquestador adaptativo
**Destinatarios:** Persona 2 (implementación en C#) y Persona 3 (pruebas y optimización)

> **Convenciones de este documento**
>
> - **[SDK]**: hecho confirmado leyendo la documentación o la API real del kit (`lib/AgentsPoker.Core.xml`, `GUIA_AGENTES.md`, `REGLAS.md`, código del workbench).
> - **[DISEÑO]**: decisión propuesta en este documento. Es implementable, pero no es una propiedad del SDK.
> - **[PRUEBA]**: valor o hipótesis que **debe medirse**. Todo número marcado así es un punto de partida heurístico, no un resultado demostrado.
> - El nombre `AgenteHibrido` es un marcador. Se reemplaza por el nombre registrado (clase = archivo = nombre del formulario).
> - Rutas relativas a `Kit_AgentsPoker_SDK/` (en el repositorio).

---

## Índice

0. [Diagnóstico de compatibilidad y viabilidad](#0-diagnóstico-de-compatibilidad-y-viabilidad)
1. [Entregable A: arquitectura general](#1-entregable-a-arquitectura-general)
2. [Entregable B: especificación ES-MCCFR](#2-entregable-b-especificación-es-mccfr)
3. [Entregable C: especificación del explotador](#3-entregable-c-especificación-del-explotador)
4. [Entregable D: especificación del orquestador](#4-entregable-d-especificación-del-orquestador)
5. [Entregable E: diseño de software en C#](#5-entregable-e-diseño-de-software-en-c)
6. [Entregable F: plan de implementación](#6-entregable-f-plan-de-implementación-persona-2)
7. [Entregable G: plan de pruebas](#7-entregable-g-plan-de-pruebas-persona-3)
8. [Análisis crítico de viabilidad](#8-análisis-crítico-de-viabilidad)
9. [Ejemplos completos de decisión](#9-ejemplos-completos-de-decisión)
10. [Apéndice: tabla de parámetros](#10-apéndice-tabla-de-parámetros)

---

## 0. Diagnóstico de compatibilidad y viabilidad

### 0.1 Material revisado

| Archivo | Contenido relevante |
|---|---|
| `INSTRUCTIVO_ENTREGAS.md` | Dos entregas: informe PDF individual (máximo 5 páginas) y un `.cs` por estudiante. Checklist: `check` OK, `Timeouts 0`, `Errores 0`, `ms máx < 300`. |
| `REGLAS_POKER.md`, `Kit/REGLAS.md` | Reglas exactas del motor: ciegas, rejilla de apuestas, conversiones, all-in, botes laterales, clasificación, bracket. |
| `Kit/GUIA_AGENTES.md` | API completa, `PlayerObservation`, `HandSummary`, `MatchInfo`, reglas de entrega, técnicas. |
| `Kit/LEEME.md`, `PREGUNTAS_FRECUENTES.md` | Comandos (`run.sh`, `entrenar.sh`), APIs prohibidas, usings permitidos. |
| `Kit/Agents/MiAgente.cs`, `Rival.cs` | Plantilla verificada (Monte Carlo + `OnHandEnd`) y agente simple. |
| `Kit/Entrenamiento/Program.cs` | Laboratorio: `PokerTable`, `TableMatch`, `GameConfig.Training`, `MatchInfo.Standalone`, medición en bb/100 y Clasif% con margen al 95 %. |
| `Kit/tools/AgentsPoker.Sim/*.cs` | Workbench: compilación Roslyn con C# 9 y netstandard 2.1, detector de APIs sospechosas, `bench`, `tournament`. |
| `Kit/lib/AgentsPoker.Core.xml` | Documentación de todos los miembros públicos del motor. |
| `ELEMENTAL_POKER_V0.2.zip` | Arena de Unity para Windows. Su `AgentsPoker.Core.dll` tiene **el mismo SHA-256** que `Kit/lib/AgentsPoker.Core.dll` (`5d9c644b…cd39`), así que el motor del kit es el del torneo. |

### 0.2 Hechos confirmados del SDK [SDK]

**Interfaces que hay que implementar**

```csharp
public interface IPokerAgent {
    void Initialize(string agentName);              // no cronometrado; excepciones capturadas
    ActionType GetAction(PlayerObservation o);      // 1000 ms reales en torneo
}
public interface IHandObserver { void OnHandEnd(HandSummary summary); } // opcional, 1000 ms
public enum ActionType { FOLD = 0, CALL = 1, RAISE = 2 }
```

**Lo que el agente observa en cada decisión (`PlayerObservation`):**

- **Mano:** `HandNumber`, `Seat`, `ButtonSeat`, `SmallBlindSeat`, `BigBlindSeat`, `HoleCards`, `Board` (0/3/4/5), `Street`.
- **Apuestas:** `Pot` (todo lo comprometido en la mano, incluida la calle actual), `CurrentBet`, `ToCall`, `RaiseCost`, `BetSize` (20 o 40), `BetsThisStreet` (en preflop la BB cuenta como 1), `MaxBetsPerStreet` (4), `CanRaise`, `LegalActions`, `IsLegal(a)`.
- **Yo:** `MyStack`, `MyCommittedThisStreet`, `MyCommittedThisHand`, `Me`.
- **Mesa:** `Players` (lista indexada por asiento de `PublicPlayerState`: `Seat`, `Name`, `Stack`, `CommittedThisStreet`, `CommittedThisHand`, `IsInHand`, `HasFolded`, `IsAllIn`, `IsButton`, `IsActive`) y `ActionHistory` (acciones públicas de la mano: `Seat`, `PlayerName`, `Street`, `Kind`, `Amount`, `CommittedThisStreet`, `IsAllIn`, `IsIncompleteRaise`, `Coercion`). Puede venir **vacío** si `GameConfig.IncludeHistoryInObservation` está desactivado.
- **Atajos:** `IsFacingBet`, `ActivePlayerCount` (incluye al agente), `PotOdds = ToCall / (Pot + ToCall)`.
- **Torneo (`o.Match`, nunca `null`):** `IsFinalTable`, `IsTournament`, `Round`, `TotalRounds`, `TableNumber`, `TablesInRound`, `HandsToPlay`, `HandsPlayed`, `HandsRemaining` (incluye la mano actual), `IsLastHand`, `IsInLastHands(n)`, `QualifyingSpots`, `PlayersAtStart`, `HasHandLimit`.

**Al final de cada mano (`HandSummary`):** `HandNumber`, `MySeat`, `ButtonSeat`, `SmallBlindSeat`, `BigBlindSeat`, `PlayerNames[seat]`, `Board`, `WentToShowdown`, `RevealedCards[seat]` / `CardsOf(seat)` (solo jugadores que llegaron al showdown), `HandValues[seat]`, `Actions` / `ActionsOn(street)` (ciegas incluidas), `Pots` (`Amount`, `EligibleSeats`, `WinnerSeats`, `IsUncontested`), `Winners`, `Net`, `StacksBefore`, `StacksAfter`, `Match`. El motor llama `OnHandEnd` a **todos** los agentes sentados, incluidos los que foldearon o no recibieron cartas.

**Utilidades reutilizables:**

- `HandEvaluator.Evaluate(Card, Card, IReadOnlyList<Card>)`: 2 cartas propias + 3 a 5 del board, sin asignar memoria.
- `HandEvaluator.Evaluate(Card, Card, Card[], int)`: versión rápida para la ruta del motor.
- `EvaluateSpan(ReadOnlySpan<Card>)`.
- `GetCategory(int)` devuelve un `HandCategory` de 0 (`HighCard`) a 8 (`StraightFlush`).
- `ToEquivalenceClass(int)`: de 1 a 7462, donde menor es mejor.
- `Describe(int)`.
- `Card(int index)`, con `Index = (Rank-2)*4 + Suit` en el rango 0..51, y `Card.Rank` en 2..14.
- `Card.DeckSize = 52`.
- `Deck.Shuffle(Random)`.

**Utilidades del laboratorio (no se entregan):**

- `PokerTable(GameConfig)`, `Seat`, `PlayHand()` (devuelve un `HandResult` con todas las cartas), `SetStack`, `ResetStacks` y `StackDeckForNextHand(Card[])` para manos con cartas fijas.
- `TableMatch(PokerTable, MatchInfo)` con `PlayNext`, `PlayAll` y `GetStandings` (`Rank`, `Qualifies`).
- `MatchInfo.Standalone(handsToPlay, qualifyingSpots, isFinalTable, playersAtStart)` y `MatchInfo.Tournament(round, totalRounds, isFinalTable, handsToPlay, qualifyingSpots, tableNumber, tablesInRound, playersAtStart)`.
- `GameConfig.Training(seed)` sin timeout y `GameConfig.Tournament(seed)` con 1000 ms.
- Builtins: `FoldAgent`, `CheckFoldAgent`, `CallingStationAgent`, `AlwaysRaiseAgent`, `RandomAgent`, `SimpleHeuristicAgent`. Este último es el **Jefe Final "Archimago"**.

**Reglas del juego relevantes para el diseño:**

| Concepto | Valor [SDK] |
|---|---|
| Ciegas | SB 10 / BB 20 |
| Incremento | 20 en preflop y flop, 40 en turn y river |
| Tope | 4 apuestas por calle (1 bet + 3 raises; en preflop la BB es la #1) |
| Stack | 10 000, se reinicia en cada mesa |
| Mesa | 2 a 6 jugadores, 30 manos; termina antes si quedan menos de 2 con fichas |
| Clasificación | Por stack final; desempate por historial de stacks y luego por asiento más bajo; top `QualifyingSpots` (típico 3; en la final 1) |
| Torneo | `ceil(n/6)` mesas balanceadas; clasificados intercalados por puesto; mesa final con 6 o menos; puede jugar el Jefe (`Heuristic`) |
| Instancias | **Nueva instancia del agente en cada mesa**: lo aprendido solo dura esa mesa |
| Timeout/excepción | Se aplica CHECK si es gratis y FOLD si no. Tras 1000 ms el hilo del agente sigue corriendo y consume CPU |
| FOLD con `ToCall == 0` | **Fold real** (nunca devolverlo) |

### 0.3 Verificación de las restricciones del proyecto

| Restricción del enunciado | Estado | Comentario |
|---|---|---|
| C# 9 | ✅ Confirmada | El workbench compila con `LanguageVersion.CSharp9`. No valen `namespace X;`, `record struct`, `[1,2,3]`, `Random.Shared`, `MaxBy`/`MinBy` ni `PriorityQueue`. |
| .NET Standard 2.1 | ✅ Confirmada | Se compila contra `ref/netstandard.dll`. `Span<T>`, `Convert.FromBase64String` y `Math.Clamp` existen en netstandard 2.1. |
| Un solo `.cs`, una clase pública `IPokerAgent` | ✅ | La Arena usa la **primera** clase agente del archivo. Las auxiliares van anidadas. |
| `IHandObserver` opcional | ✅ | Se recomienda implementarla. |
| 1000 ms por `GetAction` y por `OnHandEnd` | ✅ | El checklist pide `ms máx < 300` en el bench del PC. La Arena (Mono) es más lenta. |
| Entrenamiento fuera del torneo | ✅ | En `Entrenamiento/`, que no se entrega. |
| Sin archivos de pesos, DLL ni servicios | ✅ | Prohibido: archivos, red, hilos/`Task`, `Thread.Sleep`, reflexión, **estado `static` mutable**. Permitido: `static readonly` con datos que nunca cambian. |
| Usings | ✅ | Solo `AgentsPoker.Core`, `System`, `System.Collections.Generic`, `System.Linq`, `System.Diagnostics`. |
| 30 manos, top N, final al 1.º | ✅ | Expuesto vía `o.Match`. |
| **Restricción adicional encontrada** | ⚠️ | El detector de APIs sospechosas del workbench (`AgentLoader.Suspicious`) busca **subcadenas literales** en el archivo, comentarios incluidos: `System.IO`, `File.`, `Directory.`, `System.Net`, `HttpClient`, `Socket`, `new Thread`, `Thread.Sleep`, `Task.Run`, `Parallel.`, `Process.`, `Environment.Exit`, `System.Reflection` y `Console.ReadLine`. **No uses identificadores como `XFile.`, `XProcess.` ni `Parallel.` en el `.cs` del agente**, aunque sean tuyos. Sí se pueden usar en el laboratorio. |

### 0.4 Funcionalidades que el SDK **no** ofrece y alternativa propuesta

| Necesidad del diseño | ¿Lo ofrece el SDK? | Alternativa [DISEÑO] |
|---|---|---|
| Recorrer el árbol de juego con ramificación (clonar o deshacer un estado) para ES-MCCFR | ❌ `PokerTable` solo juega manos completas llamando a los agentes; no hay clonación ni deshacer | **Simulador propio `LhGame`** en el laboratorio, con apply/undo, que replica las reglas. Se valida contra `PokerTable` con `StackDeckForNextHand` (ver F4). |
| Persistir lo aprendido entre mesas | ❌ Instancia nueva por mesa; `static` mutable prohibido | Los **priors poblacionales** se calibran offline y se pegan como `const`/`static readonly`. Cada mesa empieza con priors y aprende durante 30 manos. |
| Cartas de quien foldea o gana sin showdown | ❌ Nunca se revelan | Sin estadísticas de showdown en esos casos. Los rangos se infieren de las acciones. |
| Estimación de equity | ❌ Solo `HandEvaluator` | Monte Carlo propio contra rangos (sección 3.4). |
| Tiempo restante del presupuesto | ❌ | `Stopwatch` propio por decisión. |
| Identificador estable del rival | Parcial: solo el **nombre** | Clave por `Name` / `PlayerNames[seat]`. El asiento cambia. |
| Historial de acciones garantizado | Parcial: `ActionHistory` puede venir vacío | Ruta de respaldo: los rasgos que dependen del historial toman un valor neutro y el explotador se desactiva (sección 5.5). **[PRUEBA]** Comprobar en el bench que `o.ActionHistory.Count > 0`. |
| Ejecutar en paralelo dentro del agente | ❌ Prohibido | Todo es secuencial en el agente. El paralelismo solo se usa en el entrenador. |

### 0.5 Veredicto y modificaciones propuestas a la arquitectura

**La arquitectura propuesta es viable con seis ajustes justificados.** Ninguno reemplaza a ES-MCCFR.

1. **ES-MCCFR se entrena con un simulador propio** (no con `PokerTable`), porque External Sampling necesita explorar *todas* las acciones del jugador actualizado desde el mismo estado. ~~El simulador ignora all-in y botes laterales porque serían prácticamente imposibles.~~ **Corregido tras medirlo** (`bash entrenar.sh medir-allin 3000`, 2026-11-18): el máximo por mano sí es **480 fichas**, pero eso no impide el all-in. Un jugador puede perder esa cantidad muchas manos seguidas. Medido en 3 000 mesas por escenario: **0 all-in** con Heuristic×6 y con un pool mixto realista; **all-in en el 32,5 % de las mesas** (1,18 % de las manos, con botes laterales) en el peor caso, Raiser×3 + Station×3. **Decisión:** el simulador debe llevar los stacks y tratar el all-in de forma coherente (como mínimo, recortar lo que se paga al stack y repartir botes laterales). En ejecución, el motor real resuelve cualquier all-in porque el agente solo emite la intención.
2. **La abstracción se implementa una sola vez**, como clases anidadas `public` dentro de la clase del agente. El proyecto `Entrenamiento` ya referencia `Agentes.csproj` [SDK], así que el entrenador llama *exactamente* al mismo código de codificación de estados que el agente. Esto elimina el mayor riesgo técnico: que la clave de entrenamiento y la de juego no coincidan.
3. **Se añade un módulo `HeuristicPolicy`**, barato y sin estado, como red de seguridad cuando no hay política CFR, falta tiempo o salta una excepción.
4. **El orquestador mantiene la mezcla lineal** propuesta (V3), con λ dinámico y gating por límite inferior de confianza. Además se especifica una **variante regularizada por KL** (V3b) como alternativa con mejor fundamento, para compararlas experimentalmente.
5. **El contexto de torneo se aplica como capa final de riesgo**, fuera de ES-MCCFR. CFR optimiza fichas esperadas y el torneo premia el puesto.
6. **El laboratorio incluye bots de perfil parametrizable** (`ProfileBot`) para evaluar el explotador frente a rivales tight, loose, pasivos, agresivos o calling station de forma controlada.

### 0.6 Observaciones sobre el entorno local

- Este Mac **no tiene instalado `dotnet`**. Para compilar, entrenar y probar hace falta el **.NET SDK 9 o 10** [SDK]. Sin él no se pueden ejecutar `bash run.sh …` ni `bash entrenar.sh …`.
- `ELEMENTAL_POKER_V0.2` es un ejecutable de Windows (Unity + Mono). Sirve para la **prueba final de tiempos en Mono** en un PC con Windows (ver G-6). No es necesario para diseñar ni entrenar.

---

## 1. Entregable A: arquitectura general

### 1.1 Componentes

| # | Componente | Rol | Dónde vive |
|---|---|---|---|
| 1 | `CardUtil` + `HandStrengthCalc` | Índice canónico preflop (169), fuerza inmediata (HS), clase de proyecto (draws) | Agente (anidado), reutilizado por el entrenador |
| 2 | `Abstraction` + `FeatureExtractor` | Rasgos → clave entera de estado abstracto (0..154 007) | Agente, reutilizado por el entrenador |
| 3 | `CfrPolicy` + `PolicyData` | Política promedio ES-MCCFR comprimida, consulta con *backoff* | Agente (datos generados por el entrenador) |
| 4 | `OpponentModel` | Estadísticas bayesianas por rival (VPIP, PFR, AFq, FTB, …) | Agente (`OnHandEnd`) |
| 5 | `RangeEstimator` | Pesos sobre las 1326 combinaciones de cada rival activo | Agente |
| 6 | `MonteCarloEvaluator` | Equity contra rangos y equity contra el rango que continúa | Agente |
| 7 | `ExploitPolicy` | EV por acción, desviación estándar del EV, π_EXP | Agente |
| 8 | `TournamentContext` | Modo de riesgo y P(clasificar) por acción | Agente |
| 9 | `PolicyOrchestrator` | Calcula λ, combina políticas y aplica el ajuste de torneo | Agente |
| 10 | `ActionValidator` + `HeuristicPolicy` | Legalidad, respaldo y política de seguridad | Agente |
| 11 | `LhGame`, `EsMccfrTrainer`, `PolicyExporter`, `ProfileBot` | Simulador, entrenamiento, exportación y bots de prueba | Solo laboratorio |

### 1.2 Diagrama de arquitectura

```
                                   ┌───────────────────────── LABORATORIO (no se entrega) ─────────────────────────┐
                                   │  LhGame (simulador)  ──►  EsMccfrTrainer  ──►  PolicyExporter ──► PolicyData  │
                                   │        ▲                     │  usa                         (string base64     │
                                   │        └── valida contra PokerTable (StackDeckForNextHand)   pegado en el .cs)│
                                   │  ProfileBot, scripts de bench, calibración de priors y umbrales                 │
                                   └──────────────────────────────┬──────────────────────────────────────────────────┘
                                                                  │ mismo código (clases anidadas public)
┌──────────────────────────────────── AgenteHibrido.cs (único archivo entregado) ────────────────────────────────────┐
│                                                                                                                     │
│  PlayerObservation ──► FeatureExtractor ──► Abstraction.Encode ──► CfrPolicy.Lookup ──► π_CFR ───────┐             │
│        │                   │ usa HandStrengthCalc                                                    │             │
│        │                   ▼                                                                         ▼             │
│        │             (HS, draws, rasgos)                                                  ┌────────────────────┐   │
│        ├──► RangeEstimator ◄── OpponentModel ◄── OnHandEnd(HandSummary)                   │ PolicyOrchestrator │   │
│        │        │ rangos                     │ confianza                                  │  λ = f(conf, LCB,  │   │
│        │        ▼                            └───────────────────────────────────────────►│   contexto)        │──►│ ActionValidator ──► ActionType
│        │   MonteCarloEvaluator ──► (E, E_c, SE) ──► ExploitPolicy ──► EV(a), σ_EV, π_EXP ─►│  π = mezcla + ajuste│   │
│        │                                                                                  └────────────────────┘   │
│        └──► TournamentContext (o.Match, stacks) ──► modo, P(clasificar | acción) ──────────────────▲               │
│                                                                                                                     │
│  HeuristicPolicy: respaldo si no hay política CFR, si se agota el presupuesto o si hay una excepción                │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 1.3 Flujo completo de una decisión (`GetAction`)

```
t=0   reloj = Stopwatch.StartNew()
 │
 ├─ 1. ActionValidator.QuickChecks(o)            ← si solo hay una acción razonable (p. ej. CALL forzado), devolverla
 ├─ 2. f = FeatureExtractor.FromObservation(o)   ← HS por enumeración (≈1 000 evaluaciones), rasgos de apuestas
 ├─ 3. key = Abstraction.Encode(f)
 ├─ 4. π_CFR = CfrPolicy.Lookup(key) ─┬─ encontrada → seguir
 │                                    └─ no encontrada → backoff → HeuristicPolicy
 ├─ 5. ctx = TournamentContext.Evaluate(o)        ← modo LOCK/SAFE/NORMAL/CHASE, κ_ctx
 ├─ 6. ¿Ejecutar el explotador?  (confianza > c_min  ∧  t < T_skip  ∧  no trivial)
 │      ├─ sí: ranges = RangeEstimator.Build(o, model)
 │      │      eq = MonteCarloEvaluator.Run(o, ranges, presupuesto)
 │      │      exp = ExploitPolicy.Evaluate(o, eq, model) → EV(a), σ_EV(a), π_EXP
 │      └─ no: λ = 0
 ├─ 7. λ = Orchestrator.CalculateLambda(conf, ventaja, ctx)
 ├─ 8. π = Orchestrator.Combine(π_CFR, π_EXP, λ) → TournamentAdjust(π, ctx, EV)
 ├─ 9. a = ActionValidator.Sample(π, o)          ← máscara de legalidad y FOLD prohibido si ToCall == 0
 └─ 10. return a          (todo dentro de try/catch → SafeFallback)
```

**Flujo de `OnHandEnd`:** `OpponentModel.ObserveHand(summary)` actualiza contadores en O(número de acciones), sin simulaciones. Objetivo: < 2 ms.

---

## 2. Entregable B: especificación ES-MCCFR

### 2.1 Representación del juego (juego extensivo de información imperfecta)

| Elemento | Definición formal | Correspondencia con el SDK / simulador |
|---|---|---|
| Jugadores | $N = \{0,\dots,n-1\}$, con $n \in \{2,\dots,6\}$, más el azar $c$ | Asientos de `PokerTable`; en `LhGame`, índices 0..n-1 |
| Historia $h$ | Secuencia de acciones de azar (cartas) y de jugadores | `LhGame`: cartas repartidas + lista de acciones |
| Función de turno $P(h)$ | Jugador que actúa o $c$ | Orden de Hold'em: en preflop empieza el jugador a la izquierda de la BB (en heads-up, el botón); después del flop, el primer activo a la izquierda del botón (en heads-up, la BB) [SDK] |
| Acciones $A(h)$ | $\{F, C, R\}$; sin $F$ si no se debe nada; sin $R$ si `BetsThisStreet = 4` | `ActionType.FOLD / CALL / RAISE` |
| Eventos aleatorios | Reparto de 2 cartas a cada jugador y de 3 + 1 + 1 cartas al board | `Deck`; en `LhGame`, barajado al inicio de la iteración |
| Terminales $Z$ | (a) queda un solo jugador activo; (b) termina la apuesta del river → showdown | `HandResult` / `HandSummary` |
| Utilidad $u_i(z)$ | Fichas ganadas − fichas comprometidas por $i$, dividido entre la BB (20) | `HandResult.Net[i] / 20` |
| Información privada | Las 2 cartas propias | `o.HoleCards` |
| Información pública | Board, acciones, botes, stacks, asientos | El resto de `PlayerObservation` |
| Conjunto de información $I$ | Historias indistinguibles para $P(h)$: mismas cartas propias y misma secuencia pública | Lo aproxima la **clave abstracta** (2.2) |

El juego es de **suma cero** en fichas: $\sum_i u_i(z) = 0$. Esto lo confirma el SDK: `Net` suma 0. Con $n > 2$ **no** es un juego de dos jugadores de suma cero, y las garantías de convergencia a Nash no aplican (2.4.4).

### 2.2 Abstracción de estados

Cada conjunto de información real $I$ se proyecta a una clave $k = \phi(I) \in \{0,\dots,K-1\}$. Todas las historias con la misma clave comparten regrets y estrategia. La abstracción tiene dos partes: **cartas** (bucket) y **apuestas/contexto**.

#### 2.2.1 Variables

| Var. | Nombre | Cálculo exacto | Valores |
|---|---|---|---|
| $s$ | Calle | `o.Street` (Preflop=0, Flop=1, Turn=2, River=3) | 4 |
| $b$ | Bucket de cartas | Preflop: índice canónico de 169 clases. Postflop: bin de HS × clase de draw (ver 2.2.2) | 169 / 24 / 16 / 10 |
| $p$ | Posición relativa | Número de oponentes **activos y no all-in** que actúan **después** de mí en el orden de esta calle, recortado a 2 | {0, 1, 2+} → 3 |
| $m$ | Oponentes activos | `o.ActivePlayerCount − 1`, recortado: {1, 2, 3+} | 3 |
| $\beta$ | Apuestas en la calle | `o.BetsThisStreet`. Preflop 1..4 → índice 0..3; postflop 0..4 | 4 / 5 |
| $f$ | Enfrentando apuesta | `o.ToCall > 0` | 2 |
| $g$ | Ya agredí en esta calle | Existe en `ActionHistory` una acción con `Seat == o.Seat`, `Street == o.Street` y `Kind ∈ {Bet, Raise}` | 2 |
| $q$ | Agresor de la calle anterior | Última `Bet`/`Raise` de la calle anterior (ciegas excluidas): nadie=0, yo=1, otro=2. Preflop: siempre 0 | 1 / 3 |
| $\rho$ | Tamaño del bote | $P_u = $ `o.Pot / o.BetSize`; bins: $[0,4)$, $[4,8)$, $[8,\infty)$ | 3 |

**Variables descartadas, con justificación:**

- **Stack.** Excluido. Nadie puede poner más de 480 fichas por mano y el stack inicial es 10 000, así que la profundidad efectiva es siempre "profunda" y no cambia la decisión estratégica en Limit. El contexto de stack/clasificación se trata en el orquestador (sección 4.4).
- **"Posibilidad de subir".** Es redundante. En el simulador equivale a $\beta < 4$; en ejecución, la máscara de acciones usa `o.CanRaise`, que además cubre los casos raros de all-in.
- **Coste de pagar.** Redundante en Limit: `ToCall` ∈ {0, BetSize, 2·BetSize, …} queda determinado por $\beta$, $f$ y $g$ junto con la calle. La única excepción son las ciegas preflop: SB debe 10 frente a 20 y eso lo capta el bin de $p$/$\beta$.

#### 2.2.2 Bucket de cartas

**Preflop: 169 clases (abstracción sin pérdida respecto a los palos).** Sea $r_1 \ge r_2$ con rangos 2..14:

```
si r1 == r2:            b = r1 − 2                                  // 0..12   (13 parejas)
si no:                  idx78 = (r1−2)(r1−3)/2 + (r2−2)              // 0..77
                        b = (mismo palo) ? 13 + idx78 : 91 + idx78  // 13..90 suited, 91..168 offsuit
```

Ejemplo: A♠J♦ → $r_1=14$, $r_2=11$, $idx78 = 66+9 = 75$, offsuit, así que $b = 166$.

**Postflop: fuerza inmediata (HS) por enumeración exacta.** Para mano $H$ y board $B$ (3, 4 o 5 cartas):

$$HS(H,B) = \frac{\#\{o : v(H,B) > v(o,B)\} + \tfrac{1}{2}\#\{o : v(H,B) = v(o,B)\}}{\#\{o\}}$$

donde $o$ recorre **todas** las combinaciones de 2 cartas del mazo restante (1081 en flop, 1035 en turn, 990 en river) y $v$ es `HandEvaluator.Evaluate`. Es exacta, determinista y cuesta unas 1 000 evaluaciones.

**Clase de draw** (solo flop y turn), calculada sobre las 7 cartas o menos con máscaras de rango y palo:

- `FD` (flush draw): exactamente 4 cartas de un palo entre mano y board, **al menos una de la mano**, y sin color hecho.
- `SD_outs`: número de rangos $r$ que, añadidos, forman escalera usando al menos una carta propia. Formalmente: `Straight(mano ∪ board ∪ {r}) ∧ ¬Straight(board ∪ {r})`. Un OESD o un doble gutshot da 2 o más; un gutshot da 1.
- Clase de draw: `0 = ninguno`, `1 = débil` (`SD_outs == 1` sin `FD`), `2 = fuerte` (`FD` o `SD_outs ≥ 2`). En turn se fusionan 0 y 1, de modo que queda {0, 2} → 2 valores.

**Bins de HS [PRUEBA: valores iniciales; recalibrar por cuantiles en el laboratorio]:**

| Calle | Umbrales superiores de los bins de HS | Nº bins HS | Draw | Buckets $b$ |
|---|---|---|---|---|
| Flop | .30 .45 .55 .65 .75 .85 .93 1.0 | 8 | 3 | **24** = bin·3 + draw |
| Turn | .30 .45 .55 .65 .75 .85 .93 1.0 | 8 | 2 | **16** = bin·2 + (draw>0) |
| River | .20 .35 .50 .60 .70 .80 .88 .94 .98 1.0 | 10 | — | **10** |

Los bins son más finos arriba porque las decisiones de valor (apostar, subir, pagar subidas) dependen de diferencias pequeñas en la zona alta.

#### 2.2.3 Cálculo de la posición $p$

```
orden_calle(s):  si s == Preflop: empezar en BigBlindSeat + 1;  si no: empezar en ButtonSeat + 1
                 recorrer los asientos en sentido horario (módulo Players.Count) saltando !IsInHand
p = #{ j ≠ yo : Players[j].IsActive ∧ ¬Players[j].IsAllIn ∧ orden(j) > orden(yo) }   → min(p, 2)
```

Esto reproduce las reglas heads-up del SDK sin casos especiales. En heads-up, BigBlind+1 es el botón (que habla primero en preflop) y Button+1 es la BB (que habla primero después del flop).

#### 2.2.4 Codificación de la clave (radix mixto)

```
Radios por calle:            b      p   m   β   f   g   q   ρ
  Preflop (s=0):           169      3   3   4   2   2   1   3     → 169 × 432  = 73 008
  Flop    (s=1):            24      3   3   5   2   2   3   3     →  24 × 1620 = 38 880
  Turn    (s=2):            16      3   3   5   2   2   3   3     →  16 × 1620 = 25 920
  River   (s=3):            10      3   3   5   2   2   3   3     →  10 × 1620 = 16 200
Offsets: [0, 73 008, 111 888, 137 808]        K = 154 008 claves
```

```
Encode(f):
  x = f.b
  x = x*3 + f.p
  x = x*3 + f.m
  x = x*NB[s] + f.betaIdx           // NB = {4,5,5,5}; betaIdx = β−1 en preflop, β después
  x = x*2 + f.f
  x = x*2 + f.g
  x = x*NQ[s] + f.q                 // NQ = {1,3,3,3}
  x = x*3 + f.rho
  return OFFSET[s] + x
```

Además se define una **clave gruesa** para el *backoff*: $(s, b, \beta, f)$ → 169·4·2 + 24·5·2 + 16·5·2 + 10·5·2 = 1352 + 240 + 160 + 100 = **1 852** claves.

#### 2.2.5 Tamaño y memoria

- $K = 154\,008$ claves es una **cota superior**. Muchas combinaciones son inalcanzables: por ejemplo, después del flop $\beta \ge 1$ con $f = 0$ no puede darse en mi turno, y $g = 1$ con $\beta = 1$ y $f = 1$ es imposible. **[PRUEBA]** Se espera visitar entre el 40 y el 60 % (el entrenador lo reporta).
- **Entrenamiento:** regrets en `double[K·3]` = 3,70 MB, sumas de estrategia en `double[K·3]` = 3,70 MB y visitas en `int[K]` = 0,62 MB. Total ≈ **8 MB**.
- **Ejecución:** 2 bytes por clave (sección 2.6), es decir 308 016 bytes, que en base64 son **≈ 401 KB de texto** dentro del `.cs`. Se suman 3,7 KB de la tabla gruesa.

#### 2.2.6 Información que se pierde

1. Orden exacto de las acciones de la calle: quién subió y quién pagó. Solo se conservan el conteo $\beta$, si me enfrento a apuesta ($f$) y si agredí ($g$).
2. Historial de calles anteriores salvo el último agresor y el tamaño del bote (el *perfect recall* se rompe porque la abstracción es de memoria imperfecta).
3. Textura del board más allá de HS y draws: board emparejado, monocolor, etc.
4. Diferencias de HS dentro del mismo bin. Además, HS se mide contra **una** mano aleatoria y no contra un rango; el número de rivales lo compensa en parte $m$.
5. Identidad y tamaño de stack de los rivales, y contexto de torneo.
6. Más de 3 rivales activos se agrupan en "3+", y más de 2 por actuar en "2+".

### 2.3 Fundamento matemático

#### 2.3.1 Notación

- $\sigma = (\sigma_0, \dots, \sigma_{n-1})$: perfil de estrategias. $\sigma_i(I, a)$ es la probabilidad de la acción $a$ en el conjunto $I$.
- $\pi^\sigma(h)$: probabilidad de alcanzar $h$. Se factoriza como $\pi^\sigma(h) = \pi_i^\sigma(h)\,\pi_{-i}^\sigma(h)$, donde $\pi_{-i}$ incluye al azar.
- $\pi^\sigma(h, z)$: probabilidad de ir de $h$ a $z$.
- $Z_I$: terminales alcanzables desde $I$; $z[I]$ es el prefijo de $z$ en $I$.

#### 2.3.2 CFR (Counterfactual Regret Minimization, Zinkevich et al. 2007)

**Valor contrafactual** del jugador $i$ en $I$:

$$v_i(\sigma, I) = \sum_{z \in Z_I} \pi_{-i}^\sigma(z[I])\, \pi^\sigma(z[I], z)\, u_i(z)$$

**Regret instantáneo** y **acumulado**:

$$r^t(I,a) = v_i(\sigma^t_{I\to a}, I) - v_i(\sigma^t, I), \qquad R^T(I,a) = \sum_{t=1}^{T} r^t(I,a)$$

donde $\sigma_{I\to a}$ es $\sigma$ salvo que en $I$ se juega $a$ con probabilidad 1.

**Regret matching** (estrategia actual):

$$\sigma^{T+1}(I,a) = \begin{cases} \dfrac{R^{T,+}(I,a)}{\sum_{b \in A(I)} R^{T,+}(I,b)} & \text{si } \sum_b R^{T,+}(I,b) > 0 \\[2mm] \dfrac{1}{|A(I)|} & \text{en otro caso} \end{cases}, \qquad x^+ = \max(x, 0)$$

**Estrategia promedio** (la que converge y la que se exporta):

$$\bar\sigma^T(I,a) = \frac{\sum_{t=1}^T \pi_i^{\sigma^t}(I)\,\sigma^t(I,a)}{\sum_{t=1}^T \pi_i^{\sigma^t}(I)}$$

**CFR "vainilla"** recorre **todo** el árbol en cada iteración: todas las cartas posibles y todas las acciones de todos los jugadores. En Hold'em eso es inviable incluso con abstracción, porque cada iteración enumera todos los repartos.

#### 2.3.3 MCCFR (Monte Carlo CFR, Lanctot et al. 2009)

Se particionan los terminales en bloques $Q_1, \dots, Q_r$ y en cada iteración se muestrea un bloque $Q_j$ con probabilidad $q_j$. Sea $q(z) = \sum_{j: z \in Q_j} q_j$. El **valor contrafactual muestreado** es

$$\tilde v_i(\sigma, I \mid j) = \sum_{z \in Q_j \cap Z_I} \frac{1}{q(z)}\, \pi_{-i}^\sigma(z[I])\, \pi^\sigma(z[I], z)\, u_i(z)$$

y es un **estimador insesgado**: $\mathbb{E}_j[\tilde v_i(\sigma, I \mid j)] = v_i(\sigma, I)$. Los regrets muestreados $\tilde r(I,a) = \tilde v_i(\sigma_{I\to a}, I) - \tilde v_i(\sigma, I)$ también lo son, de modo que el regret promedio converge en esperanza.

#### 2.3.4 External Sampling MCCFR (la variante elegida)

En cada iteración se elige un **jugador actualizado** (*traverser*) $i$ y se recorre el árbol así:

- **Nodos de azar:** se muestrea **un** resultado según su probabilidad. En la práctica se baraja una sola vez el mazo al inicio de la iteración, lo que equivale porque el azar no depende de las acciones.
- **Nodos de oponentes $j \ne i$:** se muestrea **una** acción $a \sim \sigma_j^t(I)$.
- **Nodos de $i$:** se **enumeran todas** las acciones legales.

Con este esquema $q(z) = \pi_{-i}^\sigma(z)$ (producto de las probabilidades de azar y de oponentes a lo largo de $z$), y el factor de importancia se cancela:

$$\tilde v_i(\sigma, I) = \sum_{z \in Q \cap Z_I} \frac{\pi_{-i}^\sigma(z[I])\, \pi_{i}^\sigma(z[I], z)\, \pi_{-i}^\sigma(z[I], z)}{\pi_{-i}^\sigma(z[I])\, \pi_{-i}^\sigma(z[I], z)}\, u_i(z) = \sum_{z \in Q \cap Z_I} \pi_{i}^\sigma(z[I], z)\, u_i(z)$$

Por eso el recorrido recursivo **no multiplica por probabilidades de alcance**. En un nodo de $i$ devuelve $\sum_a \sigma(a)\,\tilde v(a)$; en un nodo de otro jugador devuelve el valor del único hijo muestreado.

**Actualización de regrets** (solo en nodos del traverser):

$$R(I,a) \mathrel{+}= \tilde v(I \cdot a) - \sum_{b} \sigma^t(I,b)\, \tilde v(I \cdot b)$$

**Actualización de la estrategia promedio** (*stochastically-weighted averaging*, Lanctot 2009, sección 4): en los nodos de un oponente $j \ne i$ visitados durante el recorrido de $i$:

$$S(I,a) \mathrel{+}= w_t \cdot \sigma_j^t(I,a)$$

con $w_t = 1$ en la versión estándar o $w_t = t$ en la versión lineal. En dos jugadores, la probabilidad de visitar $I$ es $\pi_j(I)\,\pi_c(I)$, así que el promedio queda correctamente ponderado por $\pi_j$ en esperanza (el factor de azar no depende de la estrategia). Con $n > 2$ entra además el alcance de los demás oponentes: es la aproximación habitual en MCCFR multijugador y queda documentada como limitación.

#### 2.3.5 CFR vs MCCFR vs ES-MCCFR (tabla de control para revisar la implementación)

| Aspecto | CFR | MCCFR (genérico) | **ES-MCCFR (este proyecto)** | Outcome Sampling (no usado) |
|---|---|---|---|---|
| Azar | Enumera todo | Muestrea según el esquema | **Muestrea 1 resultado por iteración** | Muestrea 1 |
| Acciones de oponentes | Enumera todas | Según el esquema | **Muestrea 1 según $\sigma_j$** | Muestrea 1 |
| Acciones del traverser | Enumera todas | Según el esquema | **Enumera todas** | Muestrea 1 (con exploración ε) |
| Peso en el regret | $\pi_{-i}$ explícito | $1/q(z)$ | **Se cancela: ninguno** | $1/q(z)$ (alta varianza) |
| Actualiza $S$ | Con $\pi_i$ del traverser | Según el esquema | **En nodos de oponentes, $+\sigma_j$** | En el traverser, con peso |
| Coste por iteración | Árbol completo | Variable | Exponencial **solo** en las decisiones propias | Lineal en la profundidad |

> **Checklist de "esto es realmente ES":**
> 1. Solo hay **un** barajado por iteración.
> 2. En los nodos del traverser hay un bucle `foreach (a in legales)`.
> 3. En los nodos de los demás hay exactamente **una** llamada recursiva con una acción muestreada.
> 4. Los regrets se actualizan **solo** en nodos del traverser.
> 5. $S$ se actualiza **solo** en nodos que no son del traverser.
> 6. No aparece ningún factor $1/q$.

#### 2.3.6 Variantes opcionales de aceleración [PRUEBA]

- **Linear MCCFR / descuento:** cada $D$ iteraciones (por ejemplo, $D = 10^6$) durante la primera fase, multiplicar $R$ y $S$ por $\frac{k}{k+1}$, con $k$ igual al número de descuentos realizados. Reduce el peso de las estrategias iniciales, que son malas.
- **Poda por regret negativo:** con probabilidad 0,95, omitir en el traverser las acciones con $R(I,a) < C$ (por ejemplo, $C = -300$ bb) después de un calentamiento. Solo debe activarse si el rendimiento lo exige.

#### 2.3.7 Condiciones de finalización

El entrenamiento termina cuando se cumple cualquiera de estas condiciones:

1. Se agota el presupuesto: $T_{max}$ iteraciones o $H_{max}$ horas.
2. **Estabilidad de la política:** cada $\Delta T$ iteraciones (por ejemplo, $5\cdot10^6$) se calcula
   $\delta = \frac{\sum_k n_k \,\|\bar\sigma_{T}(k) - \bar\sigma_{T-\Delta T}(k)\|_1}{\sum_k n_k}$, ponderado por visitas $n_k$. Se detiene si $\delta < 0{,}01$ en dos *checkpoints* seguidos. **[PRUEBA]**
3. **Meseta empírica:** el Clasif% del agente V1 frente al pool de referencia (G-2) no mejora más allá de su margen ±95 % en dos *checkpoints* consecutivos.

No se usa la explotabilidad como criterio porque no es calculable en 6-max. En heads-up sobre la abstracción sería posible, pero cuesta demasiado para este proyecto.

### 2.4 Entrenamiento por self-play

#### 2.4.1 Simulador `LhGame` (laboratorio)

**Estado mínimo (clase con arrays preasignados y *apply/undo* para no copiar):**

```
n, button, sb, bb                     // asientos de la mesa simulada
hole[n][2], board[5]                  // repartidos al inicio (azar presampleado)
handValue[n]                          // HandEvaluator del river, precalculado al repartir
bucket[n][4]                          // bucket por calle, calculado de forma perezosa con caché por iteración
street, betsThisStreet, currentBet
committedStreet[n], committedHand[n], folded[n], hasActed[n]
lastAggressor[street]                 // asiento del último Bet/Raise de cada calle (−1 = nadie)
iAggressed[n] (de la calle actual)
toAct
```

**Reglas** (idénticas a [SDK] salvo all-in y botes laterales, que no se modelan):

- Ciegas: SB = botón+1 y BB = botón+2. En heads-up, SB = botón.
- `betsThisStreet = 1` en preflop por la BB.
- `RAISE` lleva el compromiso a `currentBet + inc` (con inc 20 o 40), está permitido si `betsThisStreet < 4` y reinicia `hasActed` del resto.
- La calle termina cuando todos los activos `hasActed` e igualaron `currentBet`.
- La BB conserva su opción en preflop.
- Terminal por fold cuando queda un solo activo: gana el bote.
- Showdown: reparto a partes iguales entre los mejores `handValue`. En entrenamiento se usan utilidades fraccionarias en lugar de fichas impares.
- Utilidad: $u_i = (\text{ganado}_i - \text{committedHand}_i)/20$.

**Precálculo de buckets por iteración (clave de rendimiento):** para cada calle que se alcance, se evalúan **una sola vez** todas las combinaciones de oponente posibles sobre ese board (≈ 1 100 evaluaciones) y se ordenan sus valores. El HS de cada jugador se obtiene después por comparación (o búsqueda binaria), sin reevaluar. Coste: unas 3 300 evaluaciones por iteración en el peor caso, pero perezoso, porque muchas manos terminan en preflop.

#### 2.4.2 Procedimiento

1. **Inicialización:** `R[K·3] = 0`, `S[K·3] = 0`, `Visits[K] = 0`, generador de números aleatorios por hilo con semilla fija (reproducibilidad).
2. **Generación de una partida simulada:**
   - Muestrear el tamaño de mesa $n$ con pesos de torneo **[PRUEBA]** $P(6)=0{,}45$, $P(5)=0{,}20$, $P(4)=0{,}15$, $P(3)=0{,}10$, $P(2)=0{,}10$.
   - Muestrear el botón al azar.
   - Barajar con `Deck.Shuffle(rng)` y repartir el hole de cada jugador y las 5 cartas del board.
   - Precalcular `handValue`.
3. **Selección del traverser:** $i = t \bmod n$ (rotación). Así cada iteración actualiza una posición distinta y todas las posiciones se cubren.
4. **Recorrido** `ExternalSamplingTraversal(game, i)` (2.7).
5. **Utilidades** en terminales, en bb.
6. **Actualización de regrets** en nodos de $i$.
7. **Acumulación de estrategia** en nodos de $j \ne i$.
8. **Repetición:** $T$ iteraciones, paralelizadas en $W$ hilos del laboratorio (estilo Hogwild: escrituras concurrentes sin bloqueo sobre `double[]`. Las colisiones son raras e introducen un ruido aceptable). Alternativa determinista: un acumulador por hilo y fusión en cada *checkpoint*.
9. **Evaluación** en cada *checkpoint*:
   - exportar la política temporal;
   - ejecutar `bench` de V1 contra el pool de referencia;
   - registrar $\delta$, porcentaje de claves visitadas, iteraciones por segundo y regret positivo medio $\bar R^+ = \frac{1}{|K_v|}\sum_k \max_a R^+(k,a)/T$.
10. **Exportación:** `PolicyExporter` (2.6).

#### 2.4.3 Adaptación a 2–6 jugadores

- Una **única tabla de estrategia compartida** por todos los asientos y tamaños de mesa (self-play simétrico). El tamaño entra en la clave a través de $m$ (oponentes activos) y $p$ (oponentes por actuar).
- El tamaño de mesa se muestrea **por iteración**, de modo que la misma clave se entrena con mesas de tamaños distintos que comparten los rasgos abstractos.
- Las mesas de 2 jugadores aplican las reglas heads-up (posiciones invertidas después del flop).

#### 2.4.4 Advertencia teórica (multijugador)

- En juegos de 2 jugadores de suma cero, $\bar\sigma^T$ converge a un equilibrio de Nash con regret promedio $O(1/\sqrt{T})$ (con alta probabilidad en MCCFR).
- Con $n \ge 3$, la minimización de regret solo garantiza que la **distribución empírica conjunta** converge a un **equilibrio correlacionado grueso (CCE)**. Las estrategias marginales promedio **no** tienen por qué formar un Nash, y aunque lo hicieran, un Nash en multijugador **no garantiza no perder** contra rivales concretos (Pluribus funcionó bien en la práctica, pero eso es evidencia empírica, no un teorema).
- Además, la abstracción con memoria imperfecta rompe las garantías incluso en heads-up.
- **Consecuencia de diseño:** la calidad de V1 se valida **solo empíricamente** (G-2). La política CFR se trata como una línea base robusta y difícil de explotar, no como "óptima".

### 2.5 Estructuras de datos

| Estructura | Tipo | Dimensión | Memoria | Uso |
|---|---|---|---|---|
| `Regret` | `double[]` | $K \cdot 3$, índice `k*3 + a` | 3,70 MB | $R(k,a)$, entrenamiento |
| `StratSum` | `double[]` | $K \cdot 3$ | 3,70 MB | $S(k,a)$, entrenamiento. `double` porque con $10^8$ sumas un `float` pierde precisión |
| `Visits` | `int[]` | $K$ | 0,62 MB | Número de actualizaciones de $S$ en $k$ (confianza) |
| Estrategia actual | `Span<double>` temporal de 3 | — | — | Se recalcula con regret matching en cada visita; **no se almacena** |
| `CoarseStratSum` | `double[]` | $1852 \cdot 3$ | 44 KB | Se agrega al exportar: $S_c(k_c,a) = \sum_{k \to k_c} S(k,a)$ |
| `PolicyData.Fine` | `string[]` (base64) → `byte[]` | $2K$ bytes | 308 KB | Política promedio cuantizada, en ejecución |
| `PolicyData.Coarse` | `string[]` → `byte[]` | $2 \cdot 1852$ bytes | 3,7 KB | Backoff, en ejecución |
| *Checkpoint* | Archivo binario (solo laboratorio) | `R`, `S`, `Visits`, `T` | ≈ 8 MB | Reanudar el entrenamiento |

### 2.6 Política entrenada

**Estrategia promedio:**

$$\bar\pi(k,a) = \frac{S(k,a)}{\sum_b S(k,b)} \quad (\text{solo acciones legales en } k;\ \text{si } \sum_b S = 0 \Rightarrow k \text{ no visitada})$$

**Cuantización (2 bytes por clave):**

```
si Visits[k] < V_min  (V_min = 50 [PRUEBA]):   byte0 = 255, byte1 = 255      // marcador "sin política"
si no:  q_F = round(255·π̄(F)),  q_C = round(255·π̄(C))
        corregir: si q_F + q_C > 255 → reducir el mayor hasta que q_F + q_C ≤ 255
        π_R = (255 − q_F − q_C)/255
```

El par (255, 255) es imposible para una distribución válida, así que sirve como centinela.

**Integración en el `.cs`:**

```csharp
private static class PolicyData   // anidada; generada por PolicyExporter entre marcadores
{
    // <POLICY-BEGIN v=3 K=154008 iters=… hash=…>
    internal static readonly string[] Fine = {
        "AAEC…(4096 caracteres)…",
        // ~100 líneas
    };
    internal static readonly string[] Coarse = { "…" };
    // <POLICY-END>
}
private static readonly byte[] FineBytes   = Convert.FromBase64String(string.Concat(PolicyData.Fine));
private static readonly byte[] CoarseBytes = Convert.FromBase64String(string.Concat(PolicyData.Coarse));
```

- Es `static readonly` y nunca se modifica, así que está **permitido** [SDK]. El decodificado de unos 400 KB tarda alrededor de 1 ms y ocurre una sola vez por proceso.
- Las líneas de 4 096 caracteres evitan problemas del editor. Un literal enorme en una sola línea compila, pero dificulta el trabajo.
- `PolicyExporter` reescribe el bloque entre los marcadores `// <POLICY-BEGIN` y `// <POLICY-END>` del archivo del agente. Es un proceso del laboratorio, que sí puede usar E/S de archivos. La línea `BEGIN` lleva la **versión de la abstracción** (`v=`) y un **hash** de las constantes de abstracción. En `Initialize`, el agente verifica que coinciden con sus constantes; si no, **desactiva CFR** (protección contra un desajuste entre entrenamiento y juego).

**Consulta en `GetAction` y backoff:**

```
Lookup(key, coarseKey):
  (q0,q1) = FineBytes[2k], FineBytes[2k+1]
  si (q0,q1) ≠ (255,255): return Dequant(q0,q1)                         // nivel 1: clave fina
  (q0,q1) = CoarseBytes[2kc], CoarseBytes[2kc+1]
  si (q0,q1) ≠ (255,255): return Dequant(q0,q1)                         // nivel 2: clave gruesa
  return null                                                           // nivel 3: HeuristicPolicy
```

Después del lookup se aplica la **máscara de legalidad**: $\pi(F)=0$ si `ToCall == 0` y $\pi(R)=0$ si `!CanRaise`. Luego se renormaliza.

**Purificación opcional** [PRUEBA]: poner a 0 las probabilidades menores que $\tau_p$ (0,05 o 0,10) y renormalizar. Reduce el ruido de acciones con poco entrenamiento y en la literatura suele mejorar el rendimiento contra rivales reales, a costa de ser algo más explotable.

**Selección probabilística:** con `u = rng.NextDouble()`, se elige la primera acción $a$ con $\sum_{b \le a}\pi(b) > u$. `rng` es un `Random` **de instancia** (no estático). La semilla es fija para depurar y variable (`Environment.TickCount`) para el torneo, para no ser predecible.

### 2.7 Pseudocódigo ES-MCCFR

```text
// ---------------- RegretMatching ----------------
function RegretMatching(k, legalMask, out sigma[3]):
    sumPos = 0
    for a in 0..2:
        sigma[a] = (legalMask[a] and R[3k+a] > 0) ? R[3k+a] : 0
        sumPos += sigma[a]
    if sumPos > 0:
        for a: sigma[a] /= sumPos
    else:
        nLegal = count(legalMask)
        for a: sigma[a] = legalMask[a] ? 1/nLegal : 0

// ---------------- ExternalSamplingTraversal ----------------
// game: LhGame con apply/undo; i: traverser; t: iteración; rng: aleatorio del hilo
function ExternalSamplingTraversal(game, i, t, rng) -> double:
    if game.IsTerminal():
        return game.Utility(i)                              // en bb
    p = game.ToAct
    f = FeatureExtractor.FromSim(game, p)                   // misma estructura Features que en ejecución
    k = Abstraction.Encode(f)
    legal = game.LegalMask()                                // F solo si toCall>0; R solo si bets<4
    RegretMatching(k, legal, sigma)

    if p == i:
        v[0..2] = 0;  vNode = 0
        for a in 0..2 where legal[a]:
            u = game.Apply(a)
            v[a] = ExternalSamplingTraversal(game, i, t, rng)
            game.Undo(u)
            vNode += sigma[a] * v[a]
        UpdateRegrets(k, legal, v, vNode)
        return vNode
    else:
        UpdateAverageStrategy(k, sigma, legal, t)
        a = SampleAction(sigma, rng)
        u = game.Apply(a)
        val = ExternalSamplingTraversal(game, i, t, rng)
        game.Undo(u)
        return val

// ---------------- UpdateRegrets ----------------
function UpdateRegrets(k, legal, v[3], vNode):
    for a in 0..2 where legal[a]:
        R[3k+a] += v[a] - vNode
        // opcional (Regret floor): R[3k+a] = max(R[3k+a], R_FLOOR)

// ---------------- UpdateAverageStrategy ----------------
function UpdateAverageStrategy(k, sigma, legal, t):
    w = LINEAR ? t : 1
    for a in 0..2 where legal[a]:
        S[3k+a] += w * sigma[a]
    Visits[k] += 1

// ---------------- Train ----------------
function Train(T_max, checkpointEvery, W hilos):
    init R, S, Visits = 0
    parallel for worker w in 0..W-1:
        rng = new Random(SEED + w)
        game = new LhGame()
        for t = w; t < T_max; t += W:
            n = SampleTableSize(rng)
            game.NewDeal(n, button = rng.Next(n), rng)      // baraja y precalcula handValue
            i = t mod n
            ExternalSamplingTraversal(game, i, t, rng)
            if DISCOUNT and t mod D == 0 and t ≤ T_disc:  Scale(R, S, k/(k+1))   // un solo hilo coordina
    // checkpoints cada checkpointEvery iteraciones: SaveBinary, exportar política temporal, métricas δ, %visitadas, it/s

// ---------------- GetPolicy (exportación) ----------------
function GetPolicy(k) -> (q0,q1):
    if Visits[k] < V_min: return (255,255)
    tot = S[3k]+S[3k+1]+S[3k+2]
    if tot <= 0: return (255,255)
    pF = S[3k]/tot;  pC = S[3k+1]/tot
    q0 = round(255 pF); q1 = round(255 pC)
    while q0+q1 > 255: if q0 ≥ q1: q0-- else q1--
    return (q0,q1)
```

`FeatureExtractor.FromSim` y `FeatureExtractor.FromObservation` deben producir **la misma** estructura `Features`. Ambos usan `HandStrengthCalc` y `Abstraction` desde el archivo del agente. El test F4.3 compara las dos funciones sobre manos idénticas.

---

## 3. Entregable C: especificación del explotador

**Objetivo:** que este módulo **no** replique a ES-MCCFR. CFR da una estrategia que no depende del rival. El explotador estima *qué hace este rival concreto en esta mesa* y calcula el EV de cada acción bajo ese modelo. Solo debe desviar la decisión cuando el modelo tiene evidencia suficiente (sección 4).

### 3.1 Modelo de rivales: métricas

Cada rival $j$ se identifica por **nombre** (`Players[seat].Name` / `PlayerNames[seat]`) [SDK]. Cada métrica es una **frecuencia de Bernoulli** con contadores (oportunidades $n$, eventos $x$) y prior Beta.

| Métrica | Oportunidad válida (cuenta $n$) | Evento (cuenta $x$) | Prior $p_0$ [PRUEBA] | $k_0$ |
|---|---|---|---|---|
| **VPIP** | Mano en la que $j$ recibió cartas **y tuvo al menos una decisión preflop** (si todos foldean hasta la BB, la BB no tiene decisión y no cuenta) | Alguna `Call`, `Bet` o `Raise` preflop (las ciegas no cuentan) | 0,35 | 10 |
| **PFR** | Igual que VPIP | Alguna `Raise` preflop | 0,15 | 10 |
| **FTR_pre** (fold ante subida preflop) | Primera acción de $j$ en la mano en la que el compromiso máximo ≥ 2 apuestas (≥ 40) y $j$ debe pagar | Esa acción es `Fold` | 0,45 | 8 |
| **AFq_post** (frecuencia de agresión) | Cada acción postflop de $j$ que sea `Bet`, `Raise`, `Call` o `Fold` (los checks no cuentan, como en la definición estándar) | `Bet` o `Raise` | 0,35 | 10 |
| **FTB_post** (fold ante apuesta) | Primera vez **por calle** postflop que $j$ actúa debiendo fichas | `Fold` | 0,40 | 8 |
| **RFB_post** (subida ante apuesta) | Igual que FTB_post | `Raise` | 0,12 | 8 |
| **WTSD** | $j$ seguía activo cuando se repartió el flop | `RevealedCards[j] != null` | 0,35 | 6 |
| **BluffR** (farol en river) | $j$ hizo `Bet` o `Raise` en el river **y** sus cartas se revelaron | $HS_{river}(j) < 0{,}5$ | 0,25 | 6 |
| **CallHS** (media, no Bernoulli) | $j$ pagó una apuesta en el river y se reveló | $HS_{river}$ de su mano | 0,65 (media) | 4 |

**Cómo saber si $j$ "debía fichas"** cuando actuó (a partir de `HandSummary.Actions`, que incluye las ciegas):

```
por calle s: maxCommit = 0; committed[seat] = 0      (en preflop las ciegas fijan los compromisos iniciales)
para cada acción a de la calle s, en orden:
    antes = a.CommittedThisStreet − a.Amount          // compromiso de a.Seat antes de esta acción
    debiaFichas = maxCommit > antes
    … contar oportunidades y eventos de a.Seat con 'debiaFichas' …
    maxCommit = max(maxCommit, a.CommittedThisStreet)
```

**Estimador bayesiano (Beta-Binomial):**

$$\alpha = x + p_0 k_0, \quad \beta = (n - x) + (1-p_0) k_0, \quad \hat p = \frac{\alpha}{\alpha + \beta}, \quad \mathrm{Var}(p) = \frac{\alpha\beta}{(\alpha+\beta)^2(\alpha+\beta+1)}$$

Con pocas manos, $\hat p$ se queda cerca de $p_0$. Por ejemplo, con $n = 3$ y $k_0 = 10$, el dato pesa solo el 23 %. Esto evita conclusiones precipitadas.

**Priors poblacionales.** Los valores de la tabla son conjeturas iniciales. **[PRUEBA]** La Persona 3 debe calibrarlos con un script del laboratorio que juegue miles de mesas contra el pool esperado (builtins y agentes de compañeros disponibles) y calcule las frecuencias agregadas. Luego se pegan como `const`. La fuerza $k_0$ se puede ajustar maximizando la verosimilitud predictiva (validación: predecir la segunda mitad de la mesa con la primera).

**Etiquetas de perfil** (solo para registro, informe y gating; el EV usa las frecuencias continuas). Se asignan cuando $\sqrt{\mathrm{Var}} < 0{,}12$:

| Perfil | Regla |
|---|---|
| Conservador (tight) | VPIP < 0,22 |
| Participa demasiado (loose) | VPIP > 0,45 |
| Pasivo | AFq < 0,25 |
| Agresivo | AFq > 0,50 |
| Se retira ante apuestas (folder) | FTB > 0,60 |
| Llega al showdown (calling station) | FTB < 0,20 ∧ WTSD > 0,45 |
| Maníaco | AFq > 0,60 ∧ VPIP > 0,60 |

### 3.2 Actualización del modelo (`OnHandEnd`)

[SDK] `OnHandEnd(HandSummary)` se llama al final de **cada** mano para todos los agentes sentados. Contiene acciones completas, cartas de showdown, `StacksBefore` y `Net`. Es suficiente para todas las métricas anteriores.

- **Por rival se guarda:** contadores $(n, x)$ de cada métrica, número de manos observadas, neto acumulado, lista corta de showdowns (cartas, board y línea de acciones; como mucho 30) y una serie de agresión por mano para el test de consistencia (4.2).
- **Rivales a omitir:** el propio asiento (`MySeat`) y los asientos con `StacksBefore[seat] == 0` (no recibieron cartas).
- **Showdowns:** si `CardsOf(seat) != null`, se calcula $HS$ en el board final con `HandStrengthCalc` (unas 1 000 evaluaciones por jugador revelado; como mucho 5 jugadores, es decir, < 1 ms). Con eso se actualizan BluffR y CallHS.
- **Información faltante:**
  - Si $j$ foldea o gana sin showdown, no hay datos de cartas. **No** se infiere nada sobre sus cartas.
  - Sesgo de supervivencia: las manos reveladas son más fuertes que la media. Por eso BluffR y CallHS se condicionan a la acción (bet o call en river) y se reducen fuertemente hacia el prior ($k_0$ alto).
- **Reinicio:** automático, porque cada mesa crea una instancia nueva [SDK]. Por defensa, se reinicia también si `summary.Match.Round` o `TableNumber` cambian, o si `HandNumber` disminuye.
- **Coste:** O(|acciones|) más el HS de los revelados. Objetivo < 2 ms. Sin simulaciones.

### 3.3 Estimación de rangos

**Representación:** para cada rival activo $j$, un vector `float w_j[1326]` sobre combinaciones $c = (c_1 < c_2)$ con índice `idx = c2*(c2−1)/2 + c1` (cartas 0..51).

**Tabla de fuerza preflop** `PreflopPct[169]` (generada en el laboratorio, `static readonly`). Se calcula la equity de cada clase contra una mano aleatoria con $10^6$ simulaciones, se ordenan las clases y se asigna el **percentil acumulado de combinaciones**: AA = 0,0045 (6/1326), …, 72o ≈ 1,0. Para una combinación $c$, $pct(c)$ = `PreflopPct[clase(c)]`. Menor significa mejor.

**Paso 0 (rango inicial y eliminación por cartas conocidas):** $w_j(c) = 1$ si $c$ no contiene ninguna de mis cartas ni del board; si no, $w_j(c) = 0$.

**Paso 1 (preflop, según la acción de $j$ en esta mano y con $\sigma(x) = 1/(1+e^{-x})$):**

- Suavidad: $\tau_j = \tau_0 + \tau_1 (1 - c_j)$, con $\tau_0 = 0{,}05$, $\tau_1 = 0{,}10$ y $c_j$ la confianza del rival (4.2). Con poca información las curvas se aplanan.
- Ajuste por posición [PRUEBA]: $v_j^{pos} = \widehat{VPIP}_j \cdot (0{,}8 + 0{,}4 \cdot pos_j)$, con $pos_j \in [0,1]$ (0 = primero en hablar, 1 = botón). Análogo para PFR.
- Suelo de error o farol: $\varepsilon = 0{,}03$.

| Acción preflop de $j$ | Verosimilitud $L(c)$ |
|---|---|
| Primera subida (open-raise) | $\varepsilon + (1-\varepsilon)\,\sigma\big((r_j - pct(c))/\tau_j\big)$, con $r_j = \widehat{PFR}^{pos}_j$ |
| Resubida (3-bet o más: subió enfrentando ≥ 2 apuestas) | Igual, con $r_j \leftarrow 0{,}4\,r_j$ [PRUEBA] |
| Pagó sin subir | $\varepsilon + (1-\varepsilon)\,\sigma\big((v_j - pct)/\tau_j\big)\cdot\big(1 - 0{,}6\,\sigma((r_j - pct)/\tau_j)\big)$ |
| BB pasó su opción | $1 - 0{,}7\,\sigma\big((r_j - pct)/\tau_j\big)$ |
| Foldeó | Deja de estar activo; no se modela |

**Paso 2 (postflop, en cada calle con board $B$ y por cada acción de $j$ en orden):**

1. Se calcula **una vez por calle** el valor `val[c] = Evaluate(c, B)` de las combinaciones vivas (como mucho 1326 evaluaciones), compartido entre rivales.
2. Para cada $j$ se calcula su **percentil relativo dentro de su propio rango ponderado**: $s_j(c) = \sum_{c'} w_j(c')\,[val(c') < val(c)] / \sum w_j$ (empates a la mitad). Se obtiene ordenando las combinaciones por `val` una sola vez y barriendo pesos acumulados.
3. **Bonus de proyecto:** si $c$ tiene draw fuerte (flush draw u OESD, en flop o turn), $s^{eff}_j(c) = \max(s_j(c), 0{,}6)$ para las acciones agresivas (semi-faroles).
4. Umbrales derivados del modelo: $\theta^{bet}_j = 1 - \widehat{AFq}_j$, $\theta^{fold}_j = \widehat{FTB}_j$ y $\theta^{raise}_j = 1 - \widehat{RFB}_j$.

| Acción postflop de $j$ | $L(c)$ |
|---|---|
| `Bet` (nadie había apostado) | $\varepsilon + (1-\varepsilon)\,\sigma\big((s^{eff} - \theta^{bet})/\tau_j\big)$ |
| `Raise` | $\varepsilon + (1-\varepsilon)\,\sigma\big((s^{eff} - \theta^{raise})/\tau_j\big)$ |
| `Call` | $\varepsilon + (1-\varepsilon)\,\sigma\big((s - \theta^{fold})/\tau_j\big)\cdot\big(1 - 0{,}5\,\sigma((s - \theta^{raise})/\tau_j)\big)$ |
| `Check` | $1 - 0{,}7\,\sigma\big((s^{eff} - \theta^{bet})/\tau_j\big)$ (suelo 0,3 por slowplay) |

Actualización: $w_j(c) \leftarrow w_j(c) \cdot L(c)$, renormalizando al final de cada calle.

**Incertidumbre con pocas observaciones:** (i) las medias posteriores se quedan cerca de los priors, así que los rangos se parecen a los de un rival "promedio"; (ii) $\tau_j$ crece y aplana las curvas; (iii) el suelo $\varepsilon$ impide descartar combinaciones; (iv) la entropía normalizada del rango $H_j = -\sum \bar w \log \bar w / \log N_{vivas}$ entra en la confianza (4.2).

**Limitación aceptada:** los rangos de distintos rivales se tratan como independientes. La interacción por cartas compartidas se resuelve en el muestreo (rechazo).

**Muestreo según pesos:** para cada $j$, sumas prefijas `W_j[1326]`. Se toma $u \sim U[0, W_j^{tot})$ y se busca por bisección (unos 11 pasos). Si la combinación choca con cartas ya usadas en la simulación, se reintenta hasta 8 veces; si falla, se toma una combinación uniforme del mazo restante.

### 3.4 Equity mediante Monte Carlo (contra rangos)

**Salida:**

- $E$: equity contra los rangos actuales de todos los activos, como si todos llegaran al showdown. Es informativa.
- $E_C$: equity si **pago**. Los rivales que aún deben responder ($J_b$) continúan o no según su modelo.
- $E_c$: equity si **subo y me pagan**. Todos los rivales deben responder; se condiciona a que al menos uno continúe.
- $SE$ y $N$.

**Probabilidad de continuación:** para el rival $j$ con combinación muestreada $c$, $\kappa_j(c) = \sigma\big((s_j(c) - \widehat{FTB}_j)/\tau_j\big)$, que es la probabilidad de que continúe con $c$ ante una apuesta.

En cada simulación se sortea, con **un uniforme por rival**, $cont_j \sim \mathrm{Bernoulli}(\kappa_j(c_j))$. Con el mismo sorteo se calculan las tres equities:

- **$E$:** compiten todos.
- **$E_C$:** compiten los rivales de $J \setminus J_b$ (ya actuaron) más los de $J_b$ con $cont_j = 1$.
- **$E_c$:** compiten los rivales con $cont_j = 1$; las simulaciones en las que no continúa ninguno se descartan, porque ese caso ya lo cubre $F$ en el EV.

Así no se mezcla el escenario "foldean todos" con el de "me pagan".

**Casos exactos (más baratos y sin varianza):**

- River contra 1 rival: enumerar sus combinaciones con peso, como mucho 990 evaluaciones.
- Turn contra 1 rival: enumerar la combinación × 44 cartas de river (≈ 45 000 evaluaciones, unos 3 ms en .NET **[PRUEBA]**). Si el tiempo medido en Mono lo permite, se usa enumeración; si no, Monte Carlo.

**Evaluador:** se reutiliza `HandEvaluator.Evaluate(Card, Card, Card[], int)` [SDK], con un `Card[5]` preasignado para el board, sin asignar memoria por simulación.

**Número de simulaciones:** se ajusta por tiempo y por convergencia. Se para cuando:

- $N \ge N_{max}$ (por defecto 4 000), **o**
- el reloj supera el presupuesto MC (por defecto 60 ms), **o**
- $N \ge 400$ y $SE = \sqrt{E(1-E)/N} < 0{,}008$.

**Coste estimado por simulación:** $k$ muestreos, entre 0 y 5 cartas de board y $k+1$ evaluaciones, del orden de 1 µs en .NET → 4 000 simulaciones ≈ 4 ms. **[PRUEBA]** Medir con `bash run.sh speed 1000000`, que reporta los ns por mano de 7 cartas del evaluador, y multiplicar por 3–5 para Mono.

**Botes divididos:** si $t$ jugadores empatan con el mejor valor y yo estoy entre ellos, mi parte es $1/t$.

### 3.5 Valor esperado por acción

**Notación** (todo en fichas; la referencia es $EV(F) = 0$ porque lo ya puesto en el bote es coste hundido):

| Símbolo | Definición | Fuente |
|---|---|---|
| $P$ | `o.Pot` (incluye lo mío) | [SDK] |
| $C$ | `o.ToCall` | [SDK] |
| $R$ | `o.RaiseCost` | [SDK] |
| $B$ | `o.BetSize` | [SDK] |
| $J$, $J_b$ | Rivales activos; los que actúan después de mí en esta calle | `Players`, orden 2.2.3 |
| $R_j$ | Lo que $j$ tendría que pagar si subo: $\min(\text{MyCommittedThisStreet} + R - \text{Players}[j].\text{CommittedThisStreet},\ \text{Stack}_j)$. El `min` aproxima los botes laterales | [SDK] + [DISEÑO] |
| $f_j$ | P(fold de $j$ ante mi apuesta): $\widehat{FTB}_j$ postflop o $\widehat{FTR}_j$ preflop. Se multiplica por 0,5 si $j$ ya subió en esta calle. Si $j$ enfrenta ≥ 2 apuestas en frío, $f_j^{cold} = 1 - 0{,}7(1-f_j)$ [PRUEBA] | Modelo |
| $F$ | $\prod_{j \in J} f_j$ (todos foldean) | Modelo |
| $r_j$, $\rho$ | P(resubida de $j$): postflop $\widehat{RFB}_j$; preflop $0{,}4\,\widehat{PFR}_j$ [PRUEBA]. $\rho = 1 - \prod_j (1 - r_j)$, y $\rho = 0$ si tras mi subida la calle queda topada ($\beta + 1 = 4$) | Modelo |
| $\eta$ | Realización de equity: $\mathrm{clamp}(1 + 0{,}08\cdot(ip\ ?\ 1 : -1) - 0{,}05(m-1),\ 0{,}75,\ 1{,}10)$, donde $ip = 1$ si soy el activo que habla último **después del flop** (el más cercano al botón por la derecha) [PRUEBA] | [DISEÑO] |
| $Fut(E, k)$ | Valor de las calles futuras: $\chi \cdot B_{fut} \cdot ((k+1)E - 1)$, con $B_{fut}$ la suma de incrementos de las calles restantes (preflop 20+40+40 = 100, flop 80, turn 40, river 0) y $\chi = 0{,}5$ [PRUEBA] la fracción esperada de una apuesta por calle y jugador | [DISEÑO] |

**Fórmulas:**

$$EV(F) = 0$$

$$EV(C) = \eta\,E_C\,\big(P + C + X_b\big) - C + Fut(E_C, k_C), \qquad X_b = \sum_{j \in J_b} (1 - f_j)\,C_j,\quad k_C = |J| - \sum_{j\in J_b} f_j$$

donde $C_j$ es lo que debe pagar el rival $j$ de $J_b$ para igualar el compromiso actual.

$$EV(R) = F \cdot P \;+\; (1-F)\Big[\eta\,E_c\big(P + R + \textstyle\sum_j \tfrac{1-f_j}{1-F} R_j\big) - R \;+\; \rho\,\big(2\eta E_{rr} B - B\big) + Fut(E_c, k_R)\Big]$$

con $E_{rr} = 0{,}8\,E_c$ [PRUEBA] la equity contra un rango que resube y $k_R = \sum_j (1-f_j)/(1-F)$ el número esperado de rivales que pagan.

**Interpretación:** $F\cdot P$ es el valor de fold equity. El corchete es el valor cuando hay call. El término $\rho$ aproxima que me resuban y yo pague una apuesta más. $Fut$ aproxima las apuestas de las calles siguientes (con signo positivo si la equity supera la "parte justa" $1/(k+1)$).

**Si no debo nada ($C = 0$):** $EV(C)$ es el EV de pasar, con $C = 0$ y $X_b = 0$, y $EV(R)$ es el de apostar.

**Aproximaciones y limitaciones (explícitas):**

1. Lookahead de una sola ronda de respuestas; las calles futuras se resumen en $Fut$ (lineal en $E$).
2. Se asume que, tras una resubida, siempre pago (no se modela un fold posterior ni una nueva resubida).
3. $E_c$ se calcula con la hipótesis de que continúan todos los que no foldean (la ponderación con $\kappa$ trata a cada rival por separado, pero el resultado es una aproximación).
4. Si paso, se ignora la posibilidad de que me apuesten después.
5. Botes laterales: solo se recorta $R_j$ por el stack. En la práctica no hay all-in (0.5, punto 1).
6. Los parámetros $\eta$, $\chi$ y $E_{rr}$ son heurísticos. **[PRUEBA]** Calibrarlos por regresión: en el laboratorio se registra el EV predicho y el neto real de miles de decisiones, y se ajusta minimizando el error cuadrático.

**Alternativa avanzada (si hay tiempo):** *EV por rollouts*. Se simula el resto de la mano N veces, con los rivales jugando según su modelo (umbral sobre $s_j$) y nosotros según π_CFR. Elimina las aproximaciones 1–4, pero cuesta unas 50 veces más. Solo es viable con pocos rollouts (alrededor de 200) y con una política propia barata en las calles futuras.

**Incertidumbre del EV (método delta):**

- $\sigma_E^2 = SE^2 + \sigma_{mod}^2$, con $\sigma_{mod} = 0{,}10\,(1 - \bar c)$ [PRUEBA]. Este término recoge el error de especificación del rango, que el muestreo no captura. Se aplica por igual a $E_C$, $E_c$ y $E_{rr}$, que comparten la fuente de error (el rango).
- Derivadas respecto de un desplazamiento común $\delta$ de la equity:
  - $D_C = \partial EV(C)/\partial\delta = \eta(P + C + X_b) + \chi B_{fut}(k_C+1)$
  - $D_R = \partial EV(R)/\partial\delta = (1-F)\big[\eta\,Pot_R + \chi B_{fut}(k_R+1) + 1{,}6\,\rho\,\eta B\big]$
- Derivada respecto de la fold equity: $\partial EV(R)/\partial F = P - V_{called}$, donde $V_{called}$ es el corchete de $EV(R)$ y $Pot_R$ el bote si me pagan.
- $\mathrm{Var}(F) \approx \sum_j (F/f_j)^2\,\mathrm{Var}(f_j)$ (varianzas de la Beta posterior).
- $SD(C) = |D_C|\,\sigma_E$ y $SD(R) = \sqrt{D_R^2\sigma_E^2 + (P - V_{called})^2\mathrm{Var}(F)}$.

### 3.6 Salida del explotador y política probabilística

```csharp
public struct ExploitResult {
    public bool Valid;            // false si no se ejecutó (tiempo, sin historial, confianza baja)
    public double EvF, EvC, EvR;  // fichas
    public double SdF, SdC, SdR;  // σ_EV por acción (SdF = 0)
    public double PiF, PiC, PiR;  // π_EXP sobre acciones legales
    public int Best;              // argmax EV legal
    public double Confidence;     // conf ∈ [0,1] (4.2)
    public double Equity, EquityCall, EquityCont, EquitySE; public int Sims;   // E, E_C, E_c, SE
    public double DC, DR, Vcalled, SigmaE, VarF;   // derivadas y varianzas para σ_A (4.3)
}
```

**De EV a política (softmax con temperatura según la incertidumbre):**

$$\pi_{EXP}(a) = \frac{\exp(EV(a)/T)}{\sum_{b \in A_{legal}} \exp(EV(b)/T)}, \qquad T = \max\big(T_{min},\ z_T \cdot \overline{\sigma_{EV}}\big)$$

con $T_{min} = 0{,}1\,B$ fichas y $z_T = 1$ [PRUEBA]. **Si las estimaciones son poco fiables, $T$ crece y $\pi_{EXP}$ se aplana**, así que no puede dominar la decisión. Además, el orquestador reduce λ (sección 4).

**Salvaguardas contra la agresividad excesiva:**

1. **Bluff guard:** si $EV(R) > EV(C)$ se debe sobre todo a la fold equity ($E_c < 0{,}3$), se exige que **cada** rival activo tenga $n_{FTB} \ge 8$ oportunidades. Si no, se recalcula $F$ con los priors.
2. **Cap de raise sin valor:** $\pi_{EXP}(R) \le 0{,}85$ si $E_c < 0{,}5$.
3. La máscara de legalidad se aplica siempre (nunca FOLD con $C = 0$).

### 3.7 Pseudocódigo del explotador

```text
// ---------------- UpdateOpponentModel (en OnHandEnd) ----------------
function UpdateOpponentModel(s: HandSummary):
    if HandNumber o Match cambió de forma incoherente: model.Reset()
    for seat in 0..s.PlayerNames.Count-1:
        if seat == s.MySeat or s.StacksBefore[seat] == 0: continue
        st = model.GetOrCreate(s.PlayerNames[seat]);  st.Hands++
    for street in Preflop..River:
        maxCommit = 0; firstFacingSeen[seat] = false; voluntaryPre[seat] = false
        for a in s.ActionsOn(street):
            if a.Kind in {SmallBlind, BigBlind}: maxCommit = max(maxCommit, a.CommittedThisStreet); continue
            st = model.Get(a.PlayerName) (omitir si es el mío)
            before = a.CommittedThisStreet − a.Amount;  facing = maxCommit > before
            if street == Preflop:
                hadDecisionPre[a.Seat] = true
                if a.Kind in {Call, Bet, Raise}: voluntaryPre[a.Seat] = true
                if a.Kind == Raise: raisedPre[a.Seat] = true
                if facing and maxCommit >= 2*BB and not countedFTR[a.Seat]:
                    st.FTR.n++;  if a.Kind == Fold: st.FTR.x++;  countedFTR[a.Seat] = true
            else:
                if a.Kind in {Bet, Raise, Call, Fold}: st.AFq.n++; if a.Kind in {Bet, Raise}: st.AFq.x++
                if facing and not firstFacingSeen[a.Seat]:
                    st.FTB.n++; st.RFB.n++
                    if a.Kind == Fold: st.FTB.x++
                    if a.Kind == Raise: st.RFB.x++
                    firstFacingSeen[a.Seat] = true
            maxCommit = max(maxCommit, a.CommittedThisStreet)
        if street == Preflop:
            for seat with hadDecisionPre: st.VPIP.n++, st.PFR.n++; st.VPIP.x += voluntaryPre; st.PFR.x += raisedPre
    for seat activo al llegar el flop (no foldeó en preflop y Board.Count ≥ 3):
        st.WTSD.n++;  if s.CardsOf(seat) != null: st.WTSD.x++
    for seat con CardsOf(seat) != null y Board.Count == 5:
        hs = HandStrength(CardsOf(seat), Board)
        if seat hizo Bet/Raise en el river: st.BluffR.n++; if hs < 0.5: st.BluffR.x++
        if seat hizo Call en el river:      st.CallHS.Add(hs)
    st.RecordAggressionSeries(...)   // para la consistencia (4.2)

// ---------------- EstimateOpponentRanges (en GetAction) ----------------
function EstimateOpponentRanges(o, model) -> ranges[]:
    dead = mis cartas ∪ board
    for j in rivales activos (no foldeados):
        w_j[c] = (c ∩ dead == ∅) ? 1 : 0
        st = model.Get(name_j);  τ_j = τ0 + τ1(1 − Conf_j)
        aplicar Paso 1 con las acciones preflop de j en o.ActionHistory
    for street in Flop..o.Street (solo las calles alcanzadas):
        Bs = board hasta esa calle;  val[c] = Evaluate(c, Bs) para c vivas;  orden = argsort(val)
        for j activo:
            s_j[c] = percentil ponderado de c en w_j (barrido sobre 'orden')
            for a in acciones de j en esa calle (orden cronológico): w_j[c] *= L(a, s_j[c], drawFuerte[c])
            normalizar w_j
    construir sumas prefijas W_j;  guardar s_j de la calle actual para κ_j
    return ranges

// ---------------- MonteCarloEquity ----------------
function MonteCarloEquity(o, ranges, deadline, Nmax) -> (E, EC, Ec, SE, N):
    si k == 1 y Board == 5: return Enumerate(ranges[0])            // exacto
    sE = 0; sEC = 0; sEc = 0; nEc = 0; N = 0
    while N < Nmax and reloj < deadline and not (N ≥ 400 and SE < 0.008):
        used = dead
        for j: c_j = SampleCombo(W_j, used); used ∪= c_j      // rechazo, máx. 8 intentos
        completar el board con cartas uniformes de (mazo \ used)
        v_me = Evaluate(mis cartas, board);  v_j = Evaluate(c_j, board) para cada j
        for j: cont_j = rng.NextDouble() < κ_j(c_j)            // un sorteo por rival
        sE  += Share(v_me, {v_j : j ∈ J})
        sEC += Share(v_me, {v_j : j ∉ J_b} ∪ {v_j : j ∈ J_b ∧ cont_j})
        if ∃ j: cont_j:  sEc += Share(v_me, {v_j : cont_j});  nEc++
        N++
    E = sE/N;  EC = sEC/N;  Ec = nEc > 0 ? sEc/nEc : EC;  SE = sqrt(EC(1−EC)/N)
    return (E, EC, Ec, SE, N)

// Share(v_me, V): 0 si algún v ∈ V supera a v_me; si no, 1/(1 + #{v ∈ V : v == v_me}). Con V = ∅ → 1.

// ---------------- EstimateActionEV ----------------
function EstimateActionEV(o, eq, model, ctxPos) -> (EV[3], SD[3]):
    calcular P,C,R,B,J,J_b,R_j,f_j,F,r_j,ρ,η,Fut según 3.5
    EV[F] = 0
    EV[C] = η·EC·(P + C + X_b) − C + Fut(EC, k_C)
    EV[R] = o.CanRaise ? F·P + (1−F)[η·Ec·PotR − R + ρ(2η·Err·B − B) + Fut(Ec, k_R)] : −∞
    SD    = método delta (3.5)
    aplicar bluff guard → si se activa, recalcular F con priors y repetir EV[R]

// ---------------- GetExploitPolicy ----------------
function GetExploitPolicy(o, model, deadline) -> ExploitResult:
    if o.ActionHistory vacío or reloj > T_skip: return {Valid=false}
    ranges = EstimateOpponentRanges(o, model)
    eq     = MonteCarloEquity(o, ranges, deadline, Nmax)
    (EV, SD) = EstimateActionEV(o, eq, model, ...)
    T = max(Tmin, zT · mean(SD de acciones legales))
    Pi = softmax(EV/T) sobre acciones legales (FOLD excluido si ToCall == 0)
    aplicar el cap de raise sin valor
    return {Valid=true, EV, SD, Pi, Best=argmax EV, Confidence=CalculateConfidence(...), eq}
```

---

## 4. Entregable D: especificación del orquestador

### 4.1 Política combinada: análisis de la fórmula propuesta

$$\pi_{final}(a|s) = (1-\lambda)\,\pi_{CFR}(a|s) + \lambda\,\pi_{EXP}(a|s)$$

**Evaluación:**

| Propiedad | Análisis |
|---|---|
| Validez | Es una combinación convexa de distribuciones sobre las mismas acciones legales, así que siempre produce una distribución válida. |
| Interpretación | Equivale a usar el explotador con probabilidad λ y CFR con probabilidad 1 − λ en cada decisión. |
| Desviación acotada | La distancia de variación total cumple $TV(\pi_{final}, \pi_{CFR}) \le \lambda$. Si el explotador se equivoca, la pérdida esperada frente a CFR en esa decisión es como mucho $\lambda \cdot \max_a |EV_{real}(a) - EV_{real}(\pi_{CFR})|$. Es una garantía local de "riesgo acotado". |
| EV lineal | Con valores verdaderos, $EV(\pi_{final}) = (1-\lambda)EV(\pi_{CFR}) + \lambda EV(\pi_{EXP})$. Si el modelo fuera exacto lo óptimo sería λ = 1; **λ < 1 solo se justifica por la incertidumbre del modelo**, y por eso λ debe depender de la confianza. |
| Debilidad 1 | Si π_EXP es casi uniforme (incertidumbre alta), mezclarla inyecta ruido. Se mitiga porque λ → 0 cuando la confianza es baja. |
| Debilidad 2 | Mezclar decisión a decisión produce líneas poco coherentes entre calles (CFR equilibrado con pedazos explotadores). Se acepta. |
| Debilidad 3 | π_EXP ignora lo que CFR "sabe" de la estructura del juego. |

**Conclusión:** la fórmula es **adecuada como versión V3**, siempre que λ sea dinámico (4.3) y π_EXP use la temperatura de 3.6.

**Variante V3b (regularización KL), recomendada para comparar.** Es la solución exacta de $\max_\pi \sum_a \pi(a)EV(a) - \frac{1}{\beta}KL(\pi\,\|\,\pi_{CFR})$:

$$\pi_{KL}(a) \propto \tilde\pi_{CFR}(a)\cdot\exp\!\big(\beta\,(EV(a) - \max_b EV(b))/T\big), \qquad \tilde\pi_{CFR} = \text{normalizar}(\max(\pi_{CFR}, 0{,}02))$$

con $\beta = \lambda/(1-\lambda)$, de modo que se reutiliza el mismo λ. Tiene un fundamento de "explotación regularizada hacia el equilibrio": desplaza la masa de CFR hacia las acciones con mejor EV en proporción a la evidencia, sin abandonar la forma de la estrategia base. El suelo de 0,02 evita que una acción con $\pi_{CFR} = 0$ quede bloqueada para siempre. **[PRUEBA]** V3 frente a V3b (G-3).

### 4.2 Confianza en el modelo

Para cada rival activo $j$, con los estadísticos **relevantes para la decisión**:

- Preflop: VPIP, PFR y FTR.
- Postflop enfrentando apuesta: AFq y RFB del agresor.
- Postflop apostando: FTB y RFB de los que deben responder.

| Factor | Fórmula | Qué captura |
|---|---|---|
| Cantidad | $c^{n}_j = \dfrac{n_j}{n_j + k_c}$, con $k_c = 15$ [PRUEBA] y $n_j$ la media de oportunidades de los estadísticos relevantes | Volumen de observación |
| Significancia | $c^{dev}_j = 0{,}5 + 0{,}5\min\!\big(1, z_j/2\big)$, con $z_j = \max_m \dfrac{|\hat p_m - p_{0,m}|}{\sqrt{p_{0,m}(1-p_{0,m})/n_m}}$ | Si el rival se aparta de la población, hay algo que explotar |
| Consistencia | $c^{cons}_j = 0{,}5$ si en VPIP o AFq $|\hat p^{(1)} - \hat p^{(2)}| > 2\sqrt{\bar p(1-\bar p)(1/n_1 + 1/n_2)}$ (mitades de la mesa); si no, $1$ | Rival adaptativo o no estacionario |
| Calidad del rango | $c^{rng}_j = 1 - (1 - H_j)(1 - c^n_j)$ | Rango muy concentrado con pocos datos = riesgo |

$$c_j = c^n_j \cdot c^{dev}_j \cdot c^{cons}_j \cdot c^{rng}_j$$

**Agregación** con relevancia $\omega_j$: peso 2 para el rival al que respondo (último agresor) y peso 1 para el resto.

$$c_{model} = \frac{\sum_j \omega_j c_j}{\sum_j \omega_j}, \qquad c_{mc} = 1 - \min(1, SE/0{,}03), \qquad \mathbf{conf} = c_{model}\cdot c_{mc}$$

**Ejemplo numérico:** si un rival lleva 6 manos ($n = 6$), $c^n = 6/21 = 0{,}29$; aunque todo lo demás valga 1, $conf \le 0{,}29$. Con 25 manos y VPIP muy desviado, $c^n = 0{,}63$.

### 4.3 Ventaja explotable y cálculo de λ

**Ventaja estimada** de π_EXP sobre π_CFR bajo el modelo:

$$A = \sum_a \Delta_a\,EV(a), \qquad \Delta_a = \pi_{EXP}(a) - \pi_{CFR}(a)$$

**Incertidumbre de la ventaja.** Los errores de $EV(C)$ y $EV(R)$ están **positivamente correlacionados**, porque ambos dependen del mismo rango. Sumar varianzas por separado sobrestima $\sigma_A$ y anula casi siempre la explotación. Se usa el método delta sobre los parámetros compartidos (3.5):

$$\sigma_A = \sqrt{\Big(\sum_a \Delta_a D_a\Big)^2\sigma_E^2 + \Big(\Delta_R\,(P - V_{called})\Big)^2\mathrm{Var}(F)}, \qquad D_F = 0$$

**Límite inferior de confianza:** $LCB = A - z_A\,\sigma_A$, con $z_A = 1{,}28$ (unilateral al 90 %) [PRUEBA]. El modelo puntúa su propia política a su favor (sesgo optimista), y por eso se exige un LCB positivo y no solo $A > 0$.

**λ dinámico:**

$$\lambda = \lambda_{max}\cdot \mathbf{conf}\cdot g(LCB)\cdot \kappa_{ctx}, \qquad g(x) = \frac{x^+}{x^+ + A_{1/2}}$$

con $A_{1/2} = 0{,}1\,B$ fichas, la ventaja con la que $g = 0{,}5$, y $\lambda_{max} = 0{,}4$ como hipótesis inicial.

**Justificación de $\lambda_{max} = 0{,}4$:** con 0,4 la desviación máxima frente a CFR es el 40 % de la masa. Aun en el peor caso, la mayor parte del comportamiento sigue siendo el de la estrategia base robusta. **[PRUEBA]** Se valida con la rejilla {0,2; 0,3; 0,4; 0,6; 0,8} en G-3. Se espera un valor óptimo más alto contra rivales extremos (Station, Raiser) y más bajo contra rivales equilibrados.

**Cuándo usar solo ES-MCCFR (λ = 0):**

| Condición | Motivo |
|---|---|
| El explotador no se ejecutó (`Valid == false`): falta tiempo, `ActionHistory` vacío o no hay rivales con datos | Sin evidencia |
| $\mathbf{conf} < c_{min} = 0{,}15$ | Muestra insuficiente |
| $LCB \le 0$ | La ventaja no es significativa |
| Modo de torneo LOCK ($\kappa_{ctx} = 0$) | Clasificado con holgura: no se arriesga |

**Cuándo λ crece:** con muchas observaciones, desviación significativa del rival respecto de la población, LCB alto (ventaja grande y precisa) y modo NORMAL o CHASE.

**Sin política CFR para el estado:** la base pasa a ser π_HEUR (HeuristicPolicy) y se usa $\lambda_{max}^{noCFR} = 0{,}6$, porque la base es más débil.

### 4.4 Contexto del torneo

**Fichas esperadas vs. probabilidad de clasificar.** CFR y el EV del explotador maximizan **fichas esperadas** (utilidad lineal). El torneo premia el **puesto** en la mesa: top `QualifyingSpots` en las rondas previas y solo el 1.º en la final. Con 3 mesas restantes de diferencia, ganar 300 o 3 000 fichas vale lo mismo si se queda 2.º (GUIA §1). La utilidad real es **cóncava** (aversión al riesgo) cuando se está clasificado con margen y **convexa** (búsqueda de riesgo) cuando se está fuera y quedan pocas manos.

**Datos usados [SDK]:** `o.Match.IsFinalTable`, `QualifyingSpots`, `HandsRemaining`, `HasHandLimit`, `PlayersAtStart`; las fichas de cada jugador se calculan como $S_j$ = `Players[j].Stack + Players[j].CommittedThisHand` (como `MiPuesto` en la guía).

**Variables derivadas:**

- $Q$ = `IsFinalTable ? 1 : (QualifyingSpots > 0 ? QualifyingSpots : min(3, n − 1))`.
- $H$ = `HasHandLimit ? HandsRemaining : 30`.
- $r$ = mi puesto por $S$, con empates resueltos de forma pesimista.
- $\sigma_h$: desviación típica del neto por mano. Valor inicial 150 fichas **[PRUEBA: medir en el laboratorio con `HandResult.Net` en mesas de 6]**. Puede refinarse en línea con los netos observados en `OnHandEnd`.
- Si estoy dentro ($r \le Q$): **colchón** $z = (S_{yo} - S_{(Q+1)})/(\sigma_h\sqrt{2H})$.
- Si estoy fuera: **déficit** $u = (S_{(Q)} - S_{yo})/(\sigma_h\sqrt{2H})$.

#### Versión sencilla (V4-simple): modos de riesgo

| Modo | Condición | $\kappa_{ctx}$ | Ajuste de riesgo $\alpha$ |
|---|---|---|---|
| **LOCK** | $r \le Q$ y $z \ge 2{,}0$ (final: $z \ge 2{,}5$) | 0 | +0,5 |
| **SAFE** | $r \le Q$ y $1{,}0 \le z < 2{,}0$ | 0,5 | +0,2 |
| **NORMAL** | Resto | 1 | 0 |
| **CHASE** | $r > Q$ y ($u \ge 1{,}0$ o $H \le 5$) | 1 | −0,3 |

**Ajuste de riesgo sobre la política final:**

$$\pi'(F) \propto \pi(F)(1+\alpha), \quad \pi'(C) \propto \pi(C), \quad \pi'(R) \propto \pi(R)(1-\alpha), \quad \text{renormalizar}$$

**Excepciones:**

- Nunca se aumenta el fold con manos fuertes: HS ≥ 0,85, o en preflop `PreflopPct` ≤ 0,10.
- En LOCK, $\pi(R) = 0$ si HS < 0,5 (sin faroles).
- En CHASE, $\pi(F)$ nunca baja de lo que pide la máscara de legalidad.

Los valores de $\alpha$ son heurísticos **[PRUEBA]**.

#### Versión avanzada (V4-adv): P(clasificar | acción)

1. **Modelo de carrera:** tras esta mano quedan $H' = H - 1$ manos. Se aproxima el neto futuro de cada jugador como $N(0, \sigma_h^2 H')$, independiente entre jugadores. La probabilidad de que $j$ termine por encima de mí es
   $$p_j = \Phi\!\left(\frac{S'_j - S'_{yo}}{\sigma_h\sqrt{2H'}}\right)$$
   (si $H' = 0$, es un indicador determinista).
2. **Distribución del número de jugadores por encima** (Poisson-binomial, programación dinámica $O(n^2)$): $P(\#arriba = k)$. Entonces $P_{qual} = \sum_{k=0}^{Q-1} P(\#arriba = k)$.
3. **Resultados por acción** (usando E, F y $E_c$ del explotador o, si no se ejecutó, HS como proxy de E):
   - FOLD: $S'_{yo} = $ `Stack`. El bote $P$ se reparte en valor esperado entre los rivales activos.
   - CALL: gano con probabilidad $\eta E$ y me llevo $S'_{yo} = $ `Stack` $- C + P + C + X_b$; pierdo con probabilidad $1 - \eta E$ y me queda $S'_{yo} = $ `Stack` $- C$ (el bote va a los rivales en valor esperado).
   - RAISE: los tres resultados {todos foldean ($F$), pagan y gano, pagan y pierdo}.
4. $U(a) = \sum_o P(o|a)\cdot P_{qual}(S'(o))$. En la final se usa $P(\text{1.º})$ con $Q = 1$.
5. **Integración** con la política:
   $$\pi_T(a) \propto \pi(a)\cdot\exp\!\Big(\beta_T\,\frac{U(a) - \max_b U(b)}{0{,}01}\Big)$$
   con $\beta_T = 1$ si $H \le 10$ y $\beta_T = 0{,}3$ en otro caso [PRUEBA]. Una diferencia de 1 punto porcentual en P(clasificar) multiplica por $e^{-\beta_T}$ el peso de la acción peor.

**Coste:** como mucho 3 acciones × 3 resultados × una DP de 6×6, es decir, microsegundos.

### 4.5 Control de tiempo

**Presupuesto** (medido con el `Stopwatch` propio desde la entrada a `GetAction`; los límites están en **tiempo local .NET** y se escalan con `Params.MonoFactor` si las pruebas en la Arena lo requieren):

| Fase | Presupuesto típico | Límite duro | Acción si se supera |
|---|---|---|---|
| Rasgos + HS (≈1 000 evaluaciones) | 0,1–1 ms | 5 ms | — |
| Lookup CFR | < 0,1 ms | 1 ms | — |
| Decidir si correr el explotador | — | $T_{skip} = 60$ ms transcurridos | λ = 0 |
| Rangos (≤ 5 rivales × ≤ 3 calles × 1326) | 2–10 ms | 40 ms | Si se supera: $N_{max} = 1000$ |
| Monte Carlo | Hasta $N_{max} = 4000$ | `deadline = T0 + 150 ms` | Se corta y se usa lo acumulado (si $N < 200$: `Valid = false`) |
| EV + orquestación + torneo | < 0,5 ms | — | — |
| **Tope global** | Objetivo < 30 ms medio | **$T_{hard} = 250$ ms** | Devolver la mejor política disponible (CFR o heurística) |

**Reglas:**

- **Cuándo ejecutar Monte Carlo:** el explotador está habilitado, algún rival relevante tiene $c_j \ge c_{min}$ y la decisión no es trivial. Se considera trivial si $\max_a \pi_{CFR}(a) \ge 0{,}97$ y el modo no es CHASE, o si se está en preflop y ningún rival tiene 5 o más manos observadas.
- **Reducir simulaciones:** si tras los rangos el transcurrido supera 40 ms, $N_{max} = 1000$. El corte por $SE < 0{,}008$ ahorra tiempo en spots claros.
- **Omitir el explotador:** `ActionHistory` vacío, $T_{skip}$ superado, `conf < c_min` o modo LOCK.
- **Sin política CFR:** backoff a la clave gruesa y luego HeuristicPolicy (2.6).
- **Excepciones:** `try/catch` en `GetAction` y en `OnHandEnd`. La función `SafeFallback` está anidada y protegida:
  1. si `ToCall == 0`, CALL (check);
  2. si no, muestra de π_CFR si existe;
  3. si no, HeuristicPolicy en su propio `try`;
  4. si todo falla, FOLD.
- **Sin asignaciones en caliente:** todos los buffers (`w[5][1326]`, sumas prefijas, `val[1326]`, orden, `Card[]`) se crean en el constructor o en `Initialize`, como campos de **instancia**. Sin LINQ en los bucles.
- **Calentamiento JIT:** `HandEvaluator.ClassTable` se construye de forma perezosa [SDK]. En `Initialize` (no cronometrado) se hacen unas evaluaciones y un lookup para que la primera decisión no pague la inicialización.
- **Comprobación del reloj** cada 32 simulaciones (`(N & 31) == 0`).
- **Nunca** usar hilos ni `Task` (prohibido [SDK]).

### 4.6 Pseudocódigo del orquestador

```text
// ---------------- CalculateConfidence ----------------
function CalculateConfidence(o, model, ranges, eq) -> conf:
    num = 0; den = 0
    for j in rivales activos:
        st = model.Get(name_j);  rel = StatsRelevantes(o, j)        // según calle y rol
        n  = media de oportunidades de rel
        cn = n/(n + Kc)
        z  = max_m |p̂_m − p0_m| / sqrt(p0_m(1−p0_m)/max(1,n_m))
        cdev  = 0.5 + 0.5·min(1, z/2)
        ccons = st.DriftDetected() ? 0.5 : 1
        crng  = 1 − (1 − ranges[j].NormEntropy)·(1 − cn)
        cj = cn·cdev·ccons·crng
        ω  = (j == ÚltimoAgresor(o)) ? 2 : 1
        num += ω·cj;  den += ω
    cmodel = den > 0 ? num/den : 0
    cmc = 1 − min(1, eq.SE/0.03)
    return cmodel·cmc

// ---------------- EstimateExploitAdvantage ----------------
function EstimateExploitAdvantage(piCFR, exp) -> (A, LCB):
    Δ[a] = exp.Pi[a] − piCFR[a]                                   // solo acciones legales
    A  = Σ_a Δ[a]·exp.EV[a]
    gE = Σ_a Δ[a]·exp.D[a]            // D[F]=0, D[C]=D_C, D[R]=D_R  (3.5)
    gF = Δ[R]·(P − exp.Vcalled)
    sA = sqrt(gE²·exp.SigmaE² + gF²·exp.VarF)
    return (A, A − zA·sA)

// ---------------- EvaluateTournamentContext ----------------
function EvaluateTournamentContext(o, params) -> ctx:
    m = o.Match
    S[j] = Players[j].Stack + Players[j].CommittedThisHand  (asientos con fichas o en la mano)
    Q = m.IsFinalTable ? 1 : (m.QualifyingSpots > 0 ? m.QualifyingSpots : min(3, nJugadores−1))
    H = m.HasHandLimit ? m.HandsRemaining : 30
    r = 1 + #{j ≠ yo : S[j] ≥ S[yo]}                        // empate pesimista
    escala = σh·sqrt(2H)
    if r ≤ Q:  z = (S[yo] − S_(Q+1)) / escala   (si no hay (Q+1)-ésimo: z = +∞)
         mode = z ≥ (m.IsFinalTable ? 2.5 : 2.0) ? LOCK : (z ≥ 1.0 ? SAFE : NORMAL)
    else:      u = (S_(Q) − S[yo]) / escala
         mode = (u ≥ 1.0 or H ≤ 5) ? CHASE : NORMAL
    ctx.kappa = {LOCK:0, SAFE:0.5, NORMAL:1, CHASE:1}[mode]
    ctx.alpha = {LOCK:+0.5, SAFE:+0.2, NORMAL:0, CHASE:−0.3}[mode]
    ctx.Q, ctx.H, ctx.S = ...;  ctx.useAdvanced = params.AdvancedTournament
    return ctx

// ---------------- CalculateLambda ----------------
function CalculateLambda(conf, LCB, ctx, hasCFR, B) -> λ:
    if conf < Cmin or LCB ≤ 0 or ctx.kappa == 0: return 0
    g = LCB / (LCB + 0.1·B)
    λmax = hasCFR ? LambdaMax : LambdaMaxNoCFR
    return clamp(λmax·conf·g·ctx.kappa, 0, λmax)

// ---------------- CombinePolicies ----------------
function CombinePolicies(piBase, exp, λ, mode, ctx, o, feat) -> pi[3]:
    if exp.Valid and λ > 0:
        if mode == LINEAR: pi[a] = (1−λ)·piBase[a] + λ·exp.Pi[a]
        else /*KL*/:       β = λ/(1−λ);  pi[a] ∝ max(piBase[a],0.02)·exp(β·(exp.EV[a] − maxEV)/T)
    else: pi = piBase
    // ajuste de torneo
    if ctx.useAdvanced and H ≤ 10: pi[a] ∝ pi[a]·exp(βT·(U(a) − maxU)/0.01)      // U según 4.4
    else: aplicar α (4.4, versión sencilla) respetando las excepciones de manos fuertes
    ApplyLegalMask(pi, o);  normalizar
    return pi

// ---------------- SelectLegalAction ----------------
function SelectLegalAction(pi, o, rng) -> ActionType:
    if o.ToCall == 0: pi[FOLD] = 0
    if not o.CanRaise: pi[RAISE] = 0
    if sum(pi) ≤ 0 or NaN: return o.ToCall == 0 ? CALL : FOLD       // nunca debería ocurrir
    normalizar;  u = rng.NextDouble()
    acumulado: devolver la primera acción con acumulado > u
    // garantía final: el valor devuelto ∈ {0,1,2}; FOLD solo si ToCall > 0

// ---------------- GetAction ----------------
function GetAction(o) -> ActionType:
    try:
        reloj.Restart()
        feat = FeatureExtractor.FromObservation(o, hsCalc)
        piCFR = cfr.Lookup(Abstraction.Encode(feat), Abstraction.CoarseKey(feat))   // null si no hay
        piBase = piCFR ?? heuristic.Policy(o, feat)
        ctx = EvaluateTournamentContext(o, P)
        exp = {Valid=false};  λ = 0
        if ExploiterEnabled(o, ctx, piBase) and reloj < Tskip:
            exp = GetExploitPolicy(o, model, deadline = min(T0+150ms, T0+Thard−20ms))
            if exp.Valid:
                exp.Confidence = CalculateConfidence(o, model, ranges, exp.eq)
                (A, LCB) = EstimateExploitAdvantage(piBase, exp)
                λ = CalculateLambda(exp.Confidence, LCB, ctx, piCFR != null, o.BetSize)
        if reloj > Thard: return SelectLegalAction(piBase, o, rng)
        pi = CombinePolicies(piBase, exp, λ, P.MixMode, ctx, o, feat)
        a = SelectLegalAction(pi, o, rng)
        Log/telemetría opcional (solo en el laboratorio)
        return a
    catch:
        return SafeFallback(o)
```

---

## 5. Entregable E: diseño de software en C#

### 5.1 Principios

1. **Un solo archivo y una sola clase pública agente**: `public sealed class AgenteHibrido : IPokerAgent, IHandObserver`. Todo lo demás va anidado.
2. Las clases anidadas que el **entrenador** necesita (`CardUtil`, `HandStrengthCalc`, `Abstraction`, `Features`, `FeatureExtractor`, `Params`) son **`public`**. `Entrenamiento` referencia el ensamblado `Agentes` [SDK] y por eso las ve. Al no implementar `IPokerAgent`, no interfieren con la regla de "una clase agente". El resto puede ser `private`.
3. **Sin estado `static` mutable.** Solo `const` o `static readonly` con tablas inmutables (política, `PreflopPct`, umbrales). Todo lo que cambia (modelo de rivales, buffers, `Random`) es un campo de instancia.
4. **Parámetros centralizados** en una instancia de `Params`. El laboratorio puede crear `new AgenteHibrido { Config = p }` para barridos sin editar el código. El workbench usa el constructor sin parámetros, que aplica los valores por defecto.
5. **Usings permitidos únicamente:** `System`, `System.Collections.Generic`, `System.Linq` (fuera de los bucles calientes), `System.Diagnostics` y `AgentsPoker.Core`.
6. **Palabras vetadas por el detector** en el archivo del agente: no escribir `File.`, `Process.`, `Parallel.`, `Directory.`, `System.IO`, etc., ni siquiera en comentarios (0.3).

### 5.2 Componentes

| Componente | Responsabilidad | Recibe | Devuelve | Métodos principales | Estructuras | Depende de | Complejidad | Rendimiento |
|---|---|---|---|---|---|---|---|---|
| `CardUtil` (static) | Índices: 169 clases preflop, 1326 combinaciones, máscaras | `Card` | `int` | `Canonical169(a,b)`, `ComboIndex(i,j)`, `ComboCards(idx)` | Tablas `static readonly` | SDK `Card` | O(1) | Sin asignaciones |
| `HandStrengthCalc` | HS por enumeración, clase de draw | Mano y board | `float` HS, `int` draw | `Hs(h0,h1,board,n)`, `DrawClass(...)`, `HsBin(street,hs)` | Buffers `Card[]`, `int[52]` | `HandEvaluator` | O(C(restantes,2)) ≈ 1 000 evaluaciones | ≈ 0,05–0,2 ms |
| `Features` (struct) | Rasgos abstractos | — | — | Campos `Street, Bucket, Pos, Opp, BetIdx, Facing, IAgg, Prev, PotBin` + `Hs, Draw` | — | — | — | Por valor |
| `FeatureExtractor` | `PlayerObservation` → `Features` (y `LhGame` → `Features` en el laboratorio) | `o` | `Features` | `FromObservation(o, hs)` | — | `HandStrengthCalc` | O(asientos + historial + HS) | < 1 ms |
| `Abstraction` (static) | Codificación radix y clave gruesa | `Features` | `int` | `Encode(in f)`, `CoarseKey(in f)`, `NumKeys`, `Version`, `Hash` | Radios y offsets `static readonly` | — | O(1) | — |
| `CfrPolicy` | Política promedio cuantizada y backoff | Clave | `bool` + `double[3]` | `TryLookup(k, kc, double[] dst)`, `IsCompatible` | `byte[]` fino y grueso | `PolicyData` | O(1) | — |
| `PolicyData` (static, generada) | Datos base64 | — | — | — | `string[]` | — | — | ≈ 400 KB de fuente |
| `HeuristicPolicy` | Política de seguridad sin estado (V0b) | `o`, `Features` | `double[3]` | `Policy(o, f, dst)` | — | `PreflopPct`, HS | O(1) | — |
| `BetaStat` (struct) | Contador Beta | Eventos | Media y varianza | `Add(bool)`, `Mean(p0,k0)`, `Var(...)` | 2 `int` | — | O(1) | — |
| `OpponentStats` | Métricas de un rival | — | — | Campos `BetaStat` ×8, `CallHS`, series | — | `BetaStat` | — | — |
| `OpponentModel` | Diccionario nombre → estadísticas; actualización | `HandSummary` | — | `ObserveHand(s)`, `Get(name)`, `Reset()` | `Dictionary<string,OpponentStats>` | `HandStrengthCalc` | O(acciones) | < 2 ms |
| `RangeEstimator` | Pesos de 1326 combinaciones por rival activo | `o`, modelo | `RangeSet` | `Build(o, model, rs)` | `float[6][1326]`, prefijas, `val`, `order`, `sRel` | `HandEvaluator`, `CardUtil` | O(calles × (1326 log 1326 + rivales × 1326)) | 2–10 ms |
| `MonteCarloEvaluator` | Equity E, E_c y SE | `RangeSet`, deadline | `EquityResult` | `Run(o, rs, deadline, nMax)`, `EnumerateRiverHU(...)` | `Card[5]`, `bool[52]` | `HandEvaluator`, `Random` | O(N × (k+1) evaluaciones) | ≤ 150 ms (corte) |
| `ExploitPolicy` | EV, SD y π_EXP | `o`, `EquityResult`, modelo | `ExploitResult` | `Evaluate(o, eq, model, rs)` | — | Modelo, MC | O(k) | < 0,1 ms |
| `TournamentContext` | Modo, κ, α, U(a) | `o`, EV/E | `ContextResult` | `Evaluate(o)`, `QualifyProb(stacks, Q, H)` | `double[7]` DP | — | O(n²) | µs |
| `PolicyOrchestrator` | Confianza, λ, mezcla, ajuste | Todo lo anterior | `double[3]` | `CalculateConfidence`, `EstimateAdvantage`, `CalculateLambda`, `Combine` | — | — | O(k) | µs |
| `ActionValidator` | Máscara, muestreo y respaldo | π, `o` | `ActionType` | `Mask(pi,o)`, `Sample(pi,o,rng)`, `SafeFallback(o)` | — | — | O(1) | — |
| `DecisionTrace` | Telemetría de la última decisión (solo lectura) | — | — | Propiedad `LastTrace` | Struct | — | — | Se puede apagar |

### 5.3 Esqueleto de la clase (contratos, C# 9 / netstandard 2.1)

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

public sealed class AgenteHibrido : IPokerAgent, IHandObserver
{
    // ===================== Configuración =====================
    public sealed class Params
    {
        public double LambdaMax = 0.4, LambdaMaxNoCfr = 0.6, Cmin = 0.15, ZA = 1.28, Kc = 15;
        public int    McMaxSims = 4000, McMinSims = 200;
        public double TskipMs = 60, McDeadlineMs = 150, ThardMs = 250, MonoFactor = 1.0;
        public double SigmaHand = 150, PurifyTau = 0.0;
        public int    MixMode = 0;                // 0 = lineal (V3), 1 = KL (V3b)
        public bool   UseCfr = true, UseExploiter = true, UseTournament = true, AdvancedTournament = false;
        public int?   Seed = null;                // null = no determinista (torneo)
        // … resto de parámetros del apéndice (sección 10)
    }
    public Params Config { get; set; } = new Params();

    // ===================== Cartas y abstracción (compartido con el entrenador) =====================
    public static class CardUtil
    {
        public static int Canonical169(Card a, Card b) { /* 2.2.2 */ throw null; }
        public static int ComboIndex(int c1, int c2) { /* c2*(c2-1)/2 + c1, c1 < c2 */ throw null; }
        public static readonly byte[] ComboC1, ComboC2;   // 1326 entradas, inmutables
    }
    public sealed class HandStrengthCalc
    {
        public float Hs(Card h0, Card h1, IReadOnlyList<Card> board);   // enumeración exacta
        public int DrawClass(Card h0, Card h1, IReadOnlyList<Card> board);// 0,1,2
    }
    public struct Features
    {
        public int Street, Bucket, Pos, Opp, BetIdx, Facing, IAgg, Prev, PotBin;
        public float Hs; public int Draw;   // informativos (heurística, torneo, log)
    }
    public static class Abstraction
    {
        public const int Version = 1;
        public const int NumKeys = 154008, NumCoarseKeys = 1852;
        public static int Encode(in Features f);
        public static int CoarseKey(in Features f);
        public static int HsBin(int street, float hs);
        public static int BucketPostflop(int street, float hs, int draw);
        public static uint ConstantsHash();          // hash de radios y umbrales → validar PolicyData
    }
    public static class FeatureExtractor
    {
        public static Features FromObservation(PlayerObservation o, HandStrengthCalc hs);
    }

    // ===================== Política CFR =====================
    private static class PolicyData { /* <POLICY-BEGIN …> … <POLICY-END> */ }
    private sealed class CfrPolicy
    {
        public bool IsCompatible { get; }                   // versión y hash coinciden
        public bool TryLookup(int key, int coarseKey, double[] dst3);
    }

    // ===================== Explotador =====================
    private struct BetaStat { public int N, X; public double Mean(double p0, double k0); public double Var(double p0, double k0); }
    private sealed class OpponentStats { public BetaStat Vpip, Pfr, FtrPre, AfqPost, FtbPost, RfbPost, Wtsd, BluffR; public double CallHsSum; public int CallHsN, Hands; /* … */ }
    private sealed class OpponentModel
    {
        public void ObserveHand(HandSummary s);
        public OpponentStats Get(string name);          // nunca null (crea con priors)
        public void Reset();
    }
    private sealed class RangeSet { public int Count; public int[] Seat; public float[][] W, Prefix, SRel; public float[] Entropy; }
    private sealed class RangeEstimator { public void Build(PlayerObservation o, OpponentModel m, RangeSet dst); }
    private struct EquityResult { public double E, EC, Ec, SE; public int N; public bool Exact; }
    private sealed class MonteCarloEvaluator { public EquityResult Run(PlayerObservation o, RangeSet rs, Stopwatch clock, double deadlineMs, int nMax); }
    private struct ExploitResult { /* 3.6 */ }
    private sealed class ExploitPolicy { public ExploitResult Evaluate(PlayerObservation o, EquityResult eq, OpponentModel m, RangeSet rs); }

    // ===================== Orquestación =====================
    private struct ContextResult { public int Mode, Q, H, Rank; public double Kappa, Alpha, Z, U; }
    private sealed class TournamentContext { public ContextResult Evaluate(PlayerObservation o, Params p); }
    private sealed class PolicyOrchestrator { /* CalculateConfidence, EstimateAdvantage, CalculateLambda, Combine */ }
    private static class ActionValidator
    {
        public static void Mask(double[] pi, PlayerObservation o);
        public static ActionType Sample(double[] pi, PlayerObservation o, Random rng);
        public static ActionType SafeFallback(PlayerObservation o);
    }
    private sealed class HeuristicPolicy { public void Policy(PlayerObservation o, in Features f, double[] dst3); }

    // ===================== Telemetría (lectura desde el laboratorio) =====================
    public struct DecisionTrace { public int Key; public bool CfrHit; public double L; public double[] PiCfr, PiExp, PiFinal, Ev; public int Mode; public double Ms; public int Sims; }
    public DecisionTrace LastTrace { get; private set; }

    // ===================== Estado de instancia =====================
    private Random _rng; private readonly Stopwatch _clock = new Stopwatch();
    private HandStrengthCalc _hs; private CfrPolicy _cfr; private OpponentModel _model;
    private RangeEstimator _ranges; private RangeSet _rs; private MonteCarloEvaluator _mc;
    private ExploitPolicy _exp; private TournamentContext _ctx; private PolicyOrchestrator _orc; private HeuristicPolicy _heur;

    public AgenteHibrido() { /* crea buffers; ligero */ }
    public void Initialize(string agentName) { /* rng, calentamiento JIT, validar política */ }
    public ActionType GetAction(PlayerObservation o) { /* 4.6 GetAction */ throw null; }
    public void OnHandEnd(HandSummary s) { try { _model.ObserveHand(s); } catch { } }
}
```

> El esqueleto define **firmas y responsabilidades**. Los `throw null` y los cuerpos vacíos son marcadores para la Persona 2.

### 5.4 Contratos entre módulos (invariantes comprobables por pruebas unitarias)

| Contrato | Precondición | Postcondición / invariante |
|---|---|---|
| `Canonical169` | Dos cartas distintas | Resultado en [0,168]; invariante frente a permutaciones de palos y de orden |
| `Hs` | Board de 3 a 5 cartas, sin duplicados | Resultado en [0,1]; `Hs(AA, board seco) > Hs(72o, …)`; en river es igual a la enumeración manual |
| `Encode` | `Features` dentro de rango | Resultado en [0, NumKeys); inyectiva dentro de cada calle (test exhaustivo de radios) |
| `FromObservation` ≡ `FromSim` | Misma mano reproducida | **Los mismos `Features`** (test F4.3) |
| `TryLookup` | `IsCompatible` | `dst` suma 1 ± 1e-9; `false` solo si los dos niveles tienen el centinela |
| `OpponentModel.ObserveHand` | Cualquier `HandSummary` | No lanza excepciones; contadores con `0 ≤ X ≤ N` |
| `RangeEstimator.Build` | — | `W ≥ 0`; `W = 0` en combinaciones con cartas muertas; prefijas crecientes; al menos una combinación viva por rival |
| `MonteCarloEvaluator.Run` | `deadline > now` | `E, Ec ∈ [0,1]`; `N ≥ 1` o `Valid = false`; respeta el deadline ± 1 ms |
| `ExploitPolicy.Evaluate` | `EquityResult` válido | `EvF = 0`; `EvR = −∞` si no se puede subir; π_EXP suma 1 solo sobre acciones legales |
| `Combine` | π_base válida | π_final válida, $TV(\pi_{final}, \pi_{base}) \le \lambda$ antes del ajuste de torneo |
| `Sample` | — | Valor ∈ {0,1,2}; **nunca FOLD con `ToCall == 0`**; RAISE solo si `CanRaise` |
| `GetAction` | — | Nunca lanza excepciones; tiempo < `ThardMs` + 5 ms |

### 5.5 Comportamiento con información degradada

| Situación | Comportamiento |
|---|---|
| `ActionHistory` vacío | $g = 0$, $q = 0$; explotador desactivado; se usa CFR o la heurística |
| Política incompatible (hash distinto) | CFR desactivado; `HeuristicPolicy` + explotador con `LambdaMaxNoCfr` |
| `HasHandLimit == false` (mesa suelta) | $H = 30$; `QualifyingSpots == 0` → $Q = \min(3, n-1)$ |
| Rival sin historial | Priors; $c_j \approx 0$ → λ ≈ 0 |
| Un rival all-in | No forma parte de $p$ (no actúa); sí cuenta en $m$ y en el Monte Carlo; $R_j$ recortado a su stack |

### 5.6 Componentes del laboratorio (no se entregan)

| Clase | Proyecto | Responsabilidad |
|---|---|---|
| `LhGame` | `Entrenamiento/Cfr/` | Simulador con apply/undo (2.4.1) |
| `SimFeatures` | `Entrenamiento/Cfr/` | `FromSim(LhGame, seat)` → `AgenteHibrido.Features` |
| `EsMccfrTrainer` | `Entrenamiento/Cfr/` | `Train(...)`, checkpoints, métricas |
| `PolicyExporter` | `Entrenamiento/Cfr/` | Cuantizar, construir la tabla gruesa, base64, reescribir el bloque `POLICY` del `.cs` |
| `KuhnGame` | `Entrenamiento/Cfr/Tests/` | Test de corrección del trainer (F5) |
| `ProfileBot` | `Entrenamiento/Bots/` | Bot parametrizable (VPIP, PFR, AFq, FTB, nivel de farol) |
| `ScenarioRunner` | `Entrenamiento/Tests/` | Escenarios de torneo con `SetStack` y `MatchInfo.Standalone(…)` |
| `Calibrators` | `Entrenamiento/Tools/` | `PreflopPct`, cuantiles de HS, priors poblacionales, $\sigma_h$, regresión de $\eta$ y $\chi$ |

Program.cs del laboratorio admite subcomandos, por ejemplo `bash entrenar.sh train-cfr 3600` o `bash entrenar.sh calibrate`, con lectura de `args` como ya hace la plantilla.

---

## 6. Entregable F: plan de implementación (Persona 2)

Orden pensado para tener **siempre un agente entregable**: cada hito deja un `.cs` que pasa `check` y `bench`.

| Paso | Qué construir | Depende de | Criterio de "terminado" |
|---|---|---|---|
| **F0** | Instalar el .NET SDK 9 o 10; ejecutar `bash run.sh list` y `bash run.sh speed 1000000`; anotar los ns por evaluación | — | `list` muestra `MiAgente OK` y la velocidad del evaluador queda registrada |
| **F1** | Esqueleto `AgenteHibrido` + `ActionValidator` + `SafeFallback` + `Params` + `HeuristicPolicy` (con una `PreflopPct` provisional) | F0 | `check` OK sin avisos; `bench --builtin Heuristic*5 --matches 200` con 0 timeouts y 0 errores; nunca FOLD con `ToCall == 0` (aserción en el laboratorio) |
| **F2** | `CardUtil` + `HandStrengthCalc` (HS exacto y draws) | F1 | Tests: 169 clases distintas e invariantes de palo; HS en river igual a la enumeración manual en 20 casos; draws correctos en 30 casos a mano (OESD, gutshot, FD y combo); `Hs` < 0,3 ms en .NET |
| **F3** | `Features` + `FeatureExtractor.FromObservation` + `Abstraction` | F2 | Test exhaustivo de que `Encode` es inyectivo por calle; claves registradas en el laboratorio en 1 000 decisiones reales sin valores fuera de rango; $p$ correcto en heads-up y 6-max (casos a mano) |
| **F4** | Simulador `LhGame` + `SimFeatures.FromSim` | F3 | **F4.1** 10⁵ manos aleatorias con acciones aleatorias y el mismo mazo (`StackDeckForNextHand`) en `PokerTable` y en `LhGame`: `Net` idéntico en todas. **F4.2** Apply/undo restaura el estado (hash antes = después). **F4.3** Un agente "grabador" en `PokerTable` registra `FromObservation`; el mismo guion en `LhGame` produce `FromSim` idénticos |
| **F5** | `EsMccfrTrainer` genérico sobre una interfaz `IGame` + `KuhnGame` | F4 (la interfaz) | En Kuhn poker de 2 jugadores, la estrategia promedio converge al equilibrio conocido: valor del juego −1/18 ≈ −0,0556 para el jugador 1 con error < 0,005 tras 10⁶ iteraciones; las probabilidades de apuesta del jugador 1 con J están en [0, 1/3] y con K valen 3·P(J) (familia de equilibrios). Este test **demuestra** que la implementación es ES-MCCFR correcto |
| **F6** | Entrenamiento LHE (empezar en heads-up y abstracción gruesa) + `PolicyExporter` + `CfrPolicy` | F5 | Roundtrip exportar → decodificar con error de cuantización < 1/255; hash compatible; **V1** (`UseExploiter = false`, `UseTournament = false`) compila con la política incrustada; `bench` sin errores y con `ms máx` < 50 |
| **F7** | `OpponentModel` + `OnHandEnd` | F1 | Contra `Station*5` tras 30 manos: VPIP de Station ≥ 0,8 y FTB ≤ 0,1. Contra `CheckFold`: VPIP ≤ 0,1. Sin excepciones en 10⁴ manos; `OnHandEnd` < 2 ms |
| **F8** | `RangeEstimator` + `MonteCarloEvaluator` | F2, F7 | Equity de AA frente a 1 mano aleatoria ≈ 0,852 ± 0,01; AKs frente a QQ ≈ 0,46 ± 0,015; enumeración exacta en river igual al MC con 10⁵ simulaciones ± 0,005; se respeta el deadline |
| **F9** | `ExploitPolicy` → **V2** (`UseCfr = false`: base heurística + explotador) | F8 | Contra `Station*5`, V2 no farolea: frecuencia de RAISE con HS < 0,3 en river < 5 %. Contra `CheckFold*5` aumenta los robos. Tests de EV con números a mano (sección 9) |
| **F10** | `PolicyOrchestrator` → **V3** (lineal) y **V3b** (KL) | F6, F9 | λ = 0 con < 5 manos de datos (test). TV ≤ λ (test). `ms máx` < 300 en `bench --builtin Heuristic*5` |
| **F11** | `TournamentContext` → **V4** (simple) y opcional V4-adv | F10 | Escenarios de `ScenarioRunner` (G-5): LOCK reduce la varianza; CHASE aumenta la agresión; `IsFinalTable` cambia Q a 1 |
| **F12** | Endurecimiento | Todo | Checklist final del kit: `check` OK sin avisos, `bench` de 200 mesas con 0/0, `ms máx` < 300; prueba en la Arena (Windows) si es posible; entrenamiento final y hash coherente; nombre de clase = archivo = registrado |

**Ruta crítica:** F0 → F1 → F2 → F3 → F4 → F5 → F6 (CFR). El explotador (F7 → F9) puede avanzar en paralelo después de F2.

**Plan B si falla F6 (rendimiento o calidad del CFR):** entrenar solo preflop con CFR (73 008 claves, la parte más barata y con más impacto) y usar la heurística y el explotador después del flop. Se pierde la estrategia CFR postflop, pero se conserva ES-MCCFR como componente central del preflop.

---

## 7. Entregable G: plan de pruebas (Persona 3)

### 7.1 Herramientas y métricas disponibles [SDK]

- `bash run.sh bench --agent X --builtin … --matches N [--seed S] [--final] [--fast]` da `Gana%`, `Clasif%`, `RankProm`, `StackProm`, `ms/dec`, `ms máx`, `Timeouts` y `Errores`. Los asientos se rotan al azar.
- `bash entrenar.sh [segundos] [--final]` da bb/100 ± margen al 95 % y Clasif% ± margen.
- `bash run.sh tournament --all …` reproduce el formato del bracket.
- En el laboratorio, por mesa: `TableMatch.GetStandings()` (`Rank`, `Qualifies`, `Stack`) y `HandResult.Net`. Del agente, `LastTrace` para λ, aciertos de CFR y tiempos.

**Métricas primarias:**

1. **Clasif%** (lo que decide el torneo).
2. **Gana%** (lo que decide la final).

**Métricas secundarias:** RankProm, StackProm, bb/100, `ms máx`, `ms/dec`, Timeouts, Errores, % de decisiones con λ > 0, λ medio y % de aciertos de CFR.

### 7.2 Control de la aleatoriedad y del margen de error

- **Números aleatorios comunes (comparación pareada):** se ejecutan las dos versiones con el **mismo `--seed`**. El bench genera las semillas de mesa y la rotación de asientos con ese `Random`, así que las mismas mesas reciben los mismos repartos; el mazo depende de la semilla de la mesa y no de las acciones. **[PRUEBA]** Comprobar con `--verbose` que las cartas coinciden entre dos agentes distintos con la misma semilla. Las semillas internas de los agentes también se fijan (`Params.Seed`).
- **Error estándar de una proporción:** $\pm 1{,}96\sqrt{\hat p(1-\hat p)/n}$. Con $n = 200$ mesas y $\hat p = 0{,}5$ es ±6,9 pp; con $n = 1000$, ±3,1 pp; con $n = 2000$, ±2,2 pp.
- **Comparación pareada de Clasif%:** con $d_t = Q^{B}_t - Q^{A}_t \in \{-1, 0, 1\}$ por mesa, **test de McNemar** sobre los discordantes $(b, c)$: $\chi^2 = (|b-c|-1)^2/(b+c)$. Para StackProm, *t* pareado sobre las diferencias por mesa.
- **Tamaño de muestra:** para detectar +5 pp con potencia 0,8 sin parear hacen falta unas 1 570 mesas por versión. Al parear suelen bastar entre 600 y 1 000 **[PRUEBA]**. **Protocolo estándar: 1 000 mesas por comparación**, y 2 000 si la diferencia queda dentro del margen.
- `--fast` se usa para los experimentos de fuerza: no hay timeout del motor, pero los presupuestos internos con `Stopwatch` siguen vigentes. Para que el resultado no dependa de la carga de la máquina, en los experimentos de fuerza se fija `McMaxSims` y se desactiva el corte por tiempo (`Params.DeterministicBudget = true`). **Los tiempos se miden aparte, sin `--fast`.**

**Criterio general de aceptación de una mejora (B sobre A):**

1. Mejora de Clasif% en el pool principal con IC95 % pareado cuyo límite inferior es > 0 (o McNemar con p < 0,05).
2. **Y** ningún pool secundario empeora más de 3 pp de forma significativa.
3. **Y** `Timeouts = 0`, `Errores = 0` y `ms máx < 300`.

Si la diferencia queda dentro del margen, el resultado es "no concluyente": se repite con 2 000 mesas o se conserva la versión más simple.

### 7.3 Pools de rivales

| Pool | Composición | Para qué |
|---|---|---|
| **P-Jefe** | `Heuristic*5` | Checklist del kit; el Jefe Final juega la final |
| **P-Mixto** | `Heuristic*2`, `Station`, `Raiser`, `Random` | Diversidad de builtins (Raiser y Station son explotables) |
| **P-Perfiles** | `ProfileBot` con 5 perfiles: tight-pasivo, loose-pasivo (station suave), tight-agresivo, loose-agresivo, folder | Evaluar el explotador de forma controlada |
| **P-Compañeros** | Agentes `.cs` de otros estudiantes en `Agents/otros/` (si los comparten) | El rival real |
| **P-Self** | Versiones previas propias (V0…V3) | Regresión |
| **Tamaños de mesa** | 2 (heads-up), 3, 4 y 6 jugadores | El agente debe funcionar con 1 a 5 rivales |
| **Final** | Cualquier pool con `--final` | Solo gana el 1.º |

### 7.4 Pruebas por versión

| Versión | Objetivo | Rivales | Mesas | Métricas | Criterio |
|---|---|---|---|---|---|
| **V0**: plantilla `MiAgente` renombrada (MC + pot odds) y `HeuristicPolicy` sola | Línea base y referencia de tiempos | Todos los pools | 1 000 por pool | Todas | Referencia (sin criterio) |
| **V1**: ES-MCCFR solo | ¿La estrategia base supera a la heurística? | P-Jefe, P-Mixto, P-Self(V0) | 1 000 pareadas | Clasif%, Gana%, bb/100, % de aciertos de CFR | V1 > V0 en P-Jefe (criterio general); % de aciertos de CFR > 95 % |
| **V2**: base heurística + explotador | ¿El explotador aporta por sí solo? | P-Perfiles, P-Mixto | 1 000 | Clasif%, bb/100 frente a cada perfil | V2 > V0 en P-Perfiles; sin pérdida > 3 pp en P-Jefe |
| **V3** / **V3b**: CFR + explotador + orquestador | ¿La combinación supera a sus partes? | Todos | 1 000–2 000 | Clasif%, λ medio, % con λ > 0 | V3 ≥ max(V1, V2) en el agregado y > V1 en P-Perfiles; V3 frente a V3b para elegir |
| **V4**: contexto de torneo | ¿Mejora la clasificación (no las fichas)? | P-Mixto, P-Compañeros, `tournament` | 2 000 mesas + 200 torneos | **Clasif%** y Gana% en `--final`; se espera StackProm igual o menor | V4 > V3 en Clasif% (o Gana% en la final) aunque StackProm baje |

### 7.5 Experimentos adicionales

| Exp. | Variable | Niveles | Medida | Nota |
|---|---|---|---|---|
| **E1** Monte Carlo | `McMaxSims` | 250, 500, 1 000, 2 000, 4 000, 8 000 y por tiempo | (a) Error absoluto de equity frente a la enumeración exacta en 2 000 situaciones grabadas; (b) Clasif% de V2/V3; (c) `ms máx` | Buscar el codo: el menor N cuyo Clasif% no difiere del máximo |
| **E2** Abstracción | Bins de HS y draws | A-gruesa (5/5/6 bins, sin draw), **A-base** (8/8/10 + draws), A-fina (12/12/14 + draws) | Clasif% de V1 con el mismo presupuesto de entrenamiento; % de claves visitadas | Una abstracción más fina necesita más iteraciones: comparar a igual tiempo de CPU |
| **E3** Entrenamiento | Iteraciones | Checkpoints de 10⁶, 10⁷, 5·10⁷, 10⁸, 3·10⁸ | Clasif% de V1 y $\delta$ (estabilidad) | Curva de aprendizaje y decisión de parada |
| **E4** Peso del explotador | `LambdaMax` | 0; 0,2; 0,3; 0,4; 0,6; 0,8 | Clasif% de V3 por pool | Justifica (o corrige) la hipótesis de 0,4 |
| **E5** Mezcla | `MixMode` | Lineal o KL | Clasif% de V3 | — |
| **E6** Perfiles | Perfil de `ProfileBot` | 5 perfiles × {1, 3, 5 copias} | Δbb/100 V3 − V1 por perfil | ¿Contra quién explota bien o mal? |
| **E7** Clasificación | Escenarios de `ScenarioRunner` | (a) Líder +2 000 a 5 manos del final; (b) 4.º a −800 a 5 manos; (c) final 2.º a −1 500 a 8 manos; (d) mesa con 3 jugadores y Q = 2 | P(clasificar) y P(1.º) sobre 2 000 repeticiones por escenario, V3 frente a V4-simple frente a V4-adv | Escenarios con `SetStack` y `MatchInfo.Standalone(handsToPlay: H, qualifyingSpots: Q, isFinalTable: f, playersAtStart: n)` |
| **E8** Purificación | `PurifyTau` | 0; 0,05; 0,10 | Clasif% de V1 | — |
| **E9** Calibración del EV | $\eta$, $\chi$, $E_{rr}$ | Regresión sobre decisiones grabadas | ECM entre EV predicho y neto realizado | Ajusta los parámetros de 3.5 |

### 7.6 Pruebas de robustez (obligatorias antes de entregar)

- **R1 Tiempos:** `bench --builtin Heuristic*5 --matches 200` sin `--fast`, con `ms máx < 300` y 0 timeouts. Repetir con `Raiser*5`, que produce las calles más largas y el mayor número de decisiones.
- **R2 Mono:** si hay un PC con Windows, ejecutar la Arena `ELEMENTAL_POKER_V0.2` con el agente y anotar los tiempos. Si el factor medido supera 3, ajustar `MonoFactor` o reducir `McMaxSims`.
- **R3 Errores:** 10⁵ manos en el laboratorio con 0 excepciones (contador `res.Errors` como en `Program.cs`).
- **R4 Mesas raras:** heads-up, 3 jugadores, jugadores eliminados durante la mesa (forzados con `SetStack` bajo) y un rival all-in.
- **R5 Detector de APIs:** `check` sin avisos.
- **R6 Política:** el hash del bloque `POLICY` coincide (`IsCompatible == true` en `LastTrace`).

---

## 8. Análisis crítico de viabilidad

| # | Pregunta | Respuesta | Tipo |
|---|---|---|---|
| 1 | ¿Es viable ES-MCCFR en este SDK? | **Sí**, entrenando **fuera** del SDK con un simulador propio, porque `PokerTable` no permite ramificar. El SDK aporta el evaluador (`HandEvaluator`) para el simulador y `PokerTable` para validarlo (F4). En ejecución, el agente solo consulta una tabla | Confirmado (API) + diseño |
| 2 | Dificultades multijugador | (a) No hay garantía de Nash ni de no perder (2.4.4); (b) la varianza de ES crece con el número de jugadores; (c) más estados por posición y número de rivales; (d) el promedio de estrategia está ponderado por el alcance de los demás; (e) no hay métrica de convergencia exacta y solo cabe la evaluación empírica | Teoría + diseño |
| 3 | ¿Cuántos estados? | Cota de **154 008** claves (73 008 en preflop y 81 000 después del flop). Se esperan visitados alrededor de la mitad | Diseño; [PRUEBA] |
| 4 | Recursos | Entrenamiento: ≈ 8 MB de RAM. CPU: estimación **[PRUEBA]** de 1 000 a 5 000 iteraciones por segundo y núcleo → con 8 núcleos y 8 horas, entre 2·10⁸ y 10⁹ iteraciones. Ejecución: ≈ 0,3 MB de tabla y < 1 ms por lookup | Estimación; medir en F6 |
| 5 | ¿Cabe la política en un `.cs`? | **Sí:** ≈ 401 KB de base64 en unas 100 líneas. Roslyn y Mono compilan literales de ese tamaño; el decodificado es `static readonly`, permitido. Si fuera necesario reducirla: RLE de los centinelas o solo preflop + gruesa (≈ 150 KB) | Diseño; [PRUEBA] compilar en la Arena |
| 6 | ¿Aprende algo útil el modelo en 30 manos? | **Parcialmente.** VPIP y PFR tienen entre 25 y 30 oportunidades por rival, suficientes para detectar desviaciones grandes (con $n = 30$, el error típico es ≈ ±0,09). FTB y RFB, entre 5 y 15: solo detectan extremos. Showdowns: pocos (de 3 a 10). Los rivales extremos (Station, Raiser, CheckFold, bots muy loose o tight) se detectan en 5–10 manos; los equilibrados, apenas. Por eso λ queda limitado por la confianza | Estimación |
| 7 | ¿Monte Carlo cabe en el tiempo? | **Sí, con holgura.** Unas 4 000 simulaciones contra 5 rivales ≈ 4–10 ms en .NET; aun multiplicando por 5 en Mono, < 60 ms. La plantilla del SDK ya usa hasta 600 ms | Estimación; medir en F0 y R2 |
| 8 | ¿Es buena la mezcla de políticas? | Válida, con riesgo acotado ($TV \le \lambda$) y fácil de controlar. Mejor con λ dinámico y LCB. La variante KL tiene mejor fundamento y se compara en E5 | Análisis |
| 9 | Mayores riesgos | **R-a)** Desajuste de rasgos entre el entrenador y el agente → se mitiga con código compartido, F4.3 y el hash. **R-b)** Rendimiento del entrenamiento → abstracción gruesa o solo preflop. **R-c)** CFR no explota a los bots débiles (puede perder Clasif% frente a heurísticas agresivas contra Station) → explotador y E6. **R-d)** Sesgo optimista del EV → LCB, temperatura y bluff guard. **R-e)** Tiempos en Mono → R2 y `MonoFactor`. **R-f)** Sobreajuste a los builtins → P-Perfiles y P-Compañeros. **R-g)** Calendario del equipo → plan B de F6 | — |
| 10 | Simplificaciones que no quitan fundamento | (1) **CFR solo en preflop** y heurística o EV después del flop. (2) Abstracción gruesa (≈ 30 000 claves). (3) EV de forma cerrada, sin rollouts. (4) Torneo con la versión sencilla (modos). (5) Entrenar mesas de 2 a 6 con pesos uniformes y sin descuento lineal. (6) Rangos solo con VPIP, PFR y FTB. Se sacrifica precisión en spots postflop raros y en la explotación fina, pero se mantienen ES-MCCFR como algoritmo central, el modelado bayesiano y la orientación a la clasificación | Diseño |

**Separación de certezas:**

- **Confirmado por el SDK:** API, reglas, tiempos, restricciones, visibilidad de cartas, `o.Match` e identidad del DLL de la Arena.
- **Propuesto:** abstracción, simulador, fórmulas de EV, λ y modos de torneo.
- **Pendiente de pruebas:** todos los parámetros marcados [PRUEBA], el rendimiento del entrenamiento, el tamaño visitado, los tiempos en Mono y la ganancia real de cada componente.

---

## 9. Ejemplos completos de decisión

> Los números de π_CFR y de las estadísticas del rival son **ilustrativos** (no salen de un entrenamiento real). Las cuentas siguen exactamente las fórmulas del documento y sirven como **casos de prueba unitarios** para la Persona 2 (verificar el EV, λ y π con estas entradas).

### 9.1 Ejemplo 1: preflop, 6 jugadores, contra un rival loose

**Situación:**

- Ronda clasificatoria, mano 18 de 30 ($H = 13$), $Q = 3$.
- Soy el **botón** con **A♠ J♦**. UTG y MP foldean, **CO sube** (`BetsThisStreet = 2`). Quedan por hablar SB y BB.
- `Pot = 70`, `ToCall = 40`, `RaiseCost = 60`, `BetSize = 20`, `CanRaise = true`.
- Fichas: yo 10 600 (2.º); 4.º con 9 900.

**1. Rasgos:**

- $s = 0$; $b = 166$ (AJo).
- $p = 2$ (SB y BB hablan después); $m = 3 \to$ índice 2.
- $\beta = 2 \to$ betaIdx 1; $f = 1$; $g = 0$; $q = 0$.
- $\rho$: $70/20 = 3{,}5 \to 0$.

**2. Clave:** $((((((166\cdot3+2)\cdot3+2)\cdot4+1)\cdot2+1)\cdot2+0)\cdot1+0)\cdot3+0 = $ **72 114**.

**3. Lookup CFR** (ilustrativo): $\pi_{CFR} = [F\ 0{,}15,\ C\ 0{,}30,\ R\ 0{,}55]$.

**4. Modelo del CO** (17 manos):

| Métrica | Datos | Media posterior |
|---|---|---|
| VPIP | 10/17 | $(10+3{,}5)/27 = 0{,}50$ |
| PFR | 6/17 | $(6+1{,}5)/27 = 0{,}278$ |
| FTR | 1/4 | $(1+3{,}6)/12 = 0{,}383$ |

SB y BB están cerca de los priors: FTR 0,45; enfrentando 2 o más apuestas en frío, $f = 1 - 0{,}7\cdot0{,}55 = 0{,}615$.

**5. Monte Carlo** (ilustrativo): $E_C = 0{,}47$, $E_c = 0{,}49$, $SE = 0{,}008$.

**6. EV(C):**

- $X_b = 0{,}385\cdot30 + 0{,}385\cdot20 = 19{,}25$; $k_C = 3 - 1{,}23 = 1{,}77$.
- $\eta = 1 + 0{,}08 - 0{,}10 = 0{,}98$ (botón = $ip$).
- $Fut = 0{,}5\cdot100\cdot(2{,}77\cdot0{,}47 - 1) = 15{,}1$.
- $EV(C) = 0{,}98\cdot0{,}47\cdot129{,}25 - 40 + 15{,}1 = $ **34,6**.

**7. EV(R):**

- $F = 0{,}383\cdot0{,}615^2 = 0{,}145$.
- $R_{CO} = 20$, $R_{SB} = 50$, $R_{BB} = 40$, así que $\sum_j \frac{1-f_j}{1-F}R_j = 54{,}96$ y $Pot_R = 184{,}96$; $k_R = 1{,}62$.
- $\rho = 1 - 0{,}889\cdot0{,}94^2 = 0{,}2145$; $E_{rr} = 0{,}392$.
- Corchete: $0{,}98\cdot0{,}49\cdot184{,}96 - 60 - 0{,}99 + 14{,}19 = 42{,}02$.
- $EV(R) = 0{,}145\cdot70 + 0{,}855\cdot42{,}02 = $ **46,1**.
- $EV(F) = 0$.

**8. Incertidumbres:**

- $\sigma_E = \sqrt{0{,}008^2 + 0{,}055^2} = 0{,}056$.
- $SD(C) = 265{,}2\cdot0{,}056 = 14{,}9$.
- $SD(R) = \sqrt{272{,}7^2\cdot0{,}056^2 + 27{,}98^2\cdot0{,}0041} = 15{,}4$.
- $T = \text{media}(0;\ 14{,}9;\ 15{,}4) = 10{,}1$.

**9. π_EXP:** softmax$([0;\ 34{,}6;\ 46{,}1]/10{,}1) = $ **[0,008; 0,240; 0,752]**.

**10. Ventaja:**

- $\Delta = [-0{,}142;\ -0{,}060;\ +0{,}202]$.
- $A = -0{,}060\cdot34{,}6 + 0{,}202\cdot46{,}1 = 7{,}24$.
- $g_E = -0{,}060\cdot265{,}2 + 0{,}202\cdot272{,}7 = 39{,}2$ y $g_F = 0{,}202\cdot27{,}98 = 5{,}65$.
- $\sigma_A = \sqrt{(39{,}2\cdot0{,}056)^2 + (5{,}65\cdot0{,}064)^2} = 2{,}22$.
- $LCB = 7{,}24 - 1{,}28\cdot2{,}22 = $ **4,39**.

**11. Confianza:**

- CO: $c^n = 12{,}7/27{,}7 = 0{,}458$; $z_{VPIP} = 0{,}238/0{,}116 = 2{,}06 \Rightarrow c^{dev} = 1$; $c^{rng} = 0{,}89$; $c_{CO} = 0{,}408$ (peso 2).
- Ciegas: $c = 0{,}286$ cada una.
- $c_{model} = 0{,}347$; $c_{mc} = 0{,}733$; $\mathbf{conf} = 0{,}254$.

**12. Torneo:** $z = 700/(150\sqrt{26}) = 0{,}92 \Rightarrow$ NORMAL, así que $\kappa = 1$ y $\alpha = 0$.

**13. λ:** $g = 4{,}39/(4{,}39+2) = 0{,}687$, de modo que $\lambda = 0{,}4\cdot0{,}254\cdot0{,}687 = $ **0,070**.

**14. π_final (V3):** $0{,}930\cdot[0{,}15;\ 0{,}30;\ 0{,}55] + 0{,}070\cdot[0{,}008;\ 0{,}240;\ 0{,}752] = $ **[0,140; 0,296; 0,564]**.

Con V3b (KL, $\beta = 0{,}075$, $T = 10{,}1$) el resultado es [0,114; 0,296; 0,590]: la variante KL retira masa del fold con más decisión.

**15. Muestreo:** con $u = 0{,}62$, los acumulados son (0,140; 0,436; 1,0), así que se elige **RAISE**.

**Lectura del resultado:** con 17 manos de evidencia moderada, el explotador desplaza la política solo unos 1,4 puntos hacia la subida. La desviación crecerá a medida que aumenten las observaciones del CO.

### 9.2 Ejemplo 2: river heads-up contra una calling station (apuesta de valor fina)

**Situación:**

- Mano 25 ($H = 6$), modo NORMAL.
- Me pasan en el river. `Pot = 200`, `ToCall = 0`, `BetSize = 40`.
- Mi mano es una pareja media con $HS = 0{,}62$; estoy en posición.
- $\pi_{CFR} = [0;\ 0{,}70;\ 0{,}30]$ (contra rivales equilibrados, la apuesta de valor fina es marginal).

**Rival** (22 manos):

| Métrica | Datos | Media posterior |
|---|---|---|
| FTB | 0/11 | $(0+3{,}2)/19 = 0{,}168$ |
| AFq | 0/20 | 0,117 |
| RFB | 0/11 | 0,05 |

**Equity** por enumeración exacta (river heads-up, $SE = 0$): contra su rango amplio, $E_C = E_c = 0{,}60$.

**EVs:**

- $\eta = 1{,}08$.
- $EV(C) = 1{,}08\cdot0{,}60\cdot200 = $ **129,6** (pasar).
- $EV(R) = 0{,}168\cdot200 + 0{,}832\cdot(1{,}08\cdot0{,}60\cdot280 - 40 + 0{,}05\cdot1{,}47) = $ **151,3**.

**Incertidumbres:**

- $c = 0{,}38$, así que $\sigma_E = 0{,}062$.
- $D_C = 216$ y $D_R = 0{,}832\cdot(302{,}4 + 3{,}5) = 254{,}5$.
- $SD(C) = 13{,}4$ y $SD(R) = \sqrt{15{,}8^2 + 58{,}5^2\cdot0{,}007} = 16{,}5$; $T = 14{,}95$.

**π_EXP:** [0; 0,190; 0,810].

**Ventaja:**

- $A = (0{,}190 - 0{,}70)\cdot129{,}6 + (0{,}810-0{,}30)\cdot151{,}3 = 11{,}06$.
- $g_E = 0{,}51\cdot(254{,}5 - 216) = 19{,}6$ y $g_F = 0{,}51\cdot58{,}5 = 29{,}8$.
- $\sigma_A = \sqrt{(19{,}6\cdot0{,}062)^2 + (29{,}8\cdot0{,}084)^2} = 2{,}78$.
- $LCB = 11{,}06 - 3{,}56 = 7{,}5$.

**λ:**

- $\mathbf{conf} = 0{,}38\cdot1$ ($c_{mc} = 1$ porque la equity es exacta).
- $g = 7{,}5/(7{,}5+4) = 0{,}652$.
- $\lambda = 0{,}4\cdot0{,}38\cdot0{,}652 = 0{,}099$.

**π_final:** [0; 0,650; 0,350]. Apuesta un poco más a menudo por valor fino.

**Contraste (por qué se calcula $\sigma_A$ con correlación):** si se sumaran las varianzas de cada acción como si fueran independientes, saldría $\sigma_A = 10{,}8$ y $LCB < 0$, es decir, λ = 0. Ese error volvería al explotador inútil.

**Caso de farol:** con Q♣J♣ sin ligar, $E_c \approx 0{,}11$. Con FTB posterior 0,168, $EV(R) \approx 26{,}7$ frente a $EV(C) \approx 25{,}9$, casi iguales. Con más datos (FTB tendiendo a 0), $EV(R) < EV(C)$ y π_EXP elimina los faroles. El *bluff guard* exige $n_{FTB} \ge 8$ antes de usar FTB alto para farolear.

### 9.3 Ejemplo 3: contexto de torneo (LOCK)

**Situación:**

- Mano 27 de 30 ($H = 4$), $Q = 3$.
- Fichas: yo **11 400** (1.º); los demás 10 900, 10 200, **9 300**, 9 200 y 9 000.
- $z = (11\,400 - 9\,300)/(150\sqrt{8}) = 4{,}95 \Rightarrow$ **LOCK** ($\kappa = 0$, $\alpha = +0{,}5$).

**Decisión:** en el flop me enfrento a una apuesta con un proyecto ($HS = 0{,}45$) y $\pi_{CFR} = [0{,}20;\ 0{,}50;\ 0{,}30]$.

1. λ = 0: el explotador no se ejecuta, lo que además ahorra tiempo.
2. Ajuste α: $[0{,}30;\ 0{,}50;\ 0{,}15] \to$ normalizado $[0{,}316;\ 0{,}526;\ 0{,}158]$.
3. Regla LOCK "sin agresión con HS < 0,5": $\pi(R) = 0$, y queda **[0,375; 0,625; 0]**.

**Versión avanzada:** si foldeo, $S'_{yo} = 11\,360$ y la escala es $150\sqrt{6} = 367$. Para el 4.º, $p = \Phi((9\,300 - 11\,360)/367) = \Phi(-5{,}6) \approx 0$, así que $U(F) \approx U(C) \approx 1$ y no hay diferencia relevante. La versión simple ya da el comportamiento correcto: arriesgar poco cuando la clasificación está asegurada.

### 9.4 Ejemplo 4: mesa final, persiguiendo (CHASE)

**Situación:**

- Final ($Q = 1$), mano 26 ($H = 5$).
- Yo 9 200 (3.º); líder 11 000.
- $u = 1\,800/(150\sqrt{10}) = 3{,}8 \Rightarrow$ **CHASE** ($\alpha = -0{,}3$).

**Decisión:** con $\pi = [0{,}35;\ 0{,}45;\ 0{,}20]$, el ajuste da $[0{,}245;\ 0{,}45;\ 0{,}26] \to$ **[0,257; 0,471; 0,272]**: más agresión y menos folds.

**V4-adv:** con $H' = 4$, alcanzar al líder exige ganar botes grandes. $U(R) > U(C) > U(F)$ porque la varianza favorece a quien va por detrás (utilidad convexa). Esto ilustra la diferencia entre maximizar fichas y maximizar la probabilidad de terminar 1.º.

---

## 10. Apéndice: tabla de parámetros

Todos los valores son **iniciales** salvo los marcados [SDK]. La columna "Exp." indica el experimento que los ajusta.

| Parámetro | Valor inicial | Sección | Exp. |
|---|---|---|---|
| Bins de HS en flop y turn | .30 .45 .55 .65 .75 .85 .93 1 | 2.2.2 | E2, cuantiles |
| Bins de HS en river | .20 .35 .50 .60 .70 .80 .88 .94 .98 1 | 2.2.2 | E2 |
| Bins del bote (bote/BetSize) | <4, 4–8, ≥8 | 2.2.1 | E2 |
| Pesos de tamaño de mesa en el entrenamiento | 6: .45, 5: .20, 4: .15, 3: .10, 2: .10 | 2.4.2 | — |
| $V_{min}$ (visitas para exportar) | 50 | 2.6 | E3 |
| Descuento lineal / poda | Desactivado | 2.3.6 | E3 |
| `PurifyTau` | 0 | 2.6 | E8 |
| Priors $p_0$ / $k_0$ | Tabla 3.1 | 3.1 | Calibración |
| $\tau_0$, $\tau_1$, $\varepsilon$ (rangos) | 0,05; 0,10; 0,03 | 3.3 | E6 |
| Factor de resubida preflop | 0,4 | 3.3, 3.5 | E9 |
| `McMaxSims`, corte por SE | 4 000; 0,008 | 3.4 | E1 |
| $\eta$ (±0,08 posición; −0,05 por rival) | — | 3.5 | E9 |
| $\chi$ | 0,5 | 3.5 | E9 |
| $E_{rr}/E_c$ | 0,8 | 3.5 | E9 |
| $f^{cold}$ | $1 - 0{,}7(1-f)$ | 3.5 | E9 |
| $\sigma_{mod}$ | $0{,}10(1-\bar c)$ | 3.5 | E1/E9 |
| $T_{min}$, $z_T$ | $0{,}1B$; 1 | 3.6 | E4 |
| Bluff guard $n_{FTB}$ | 8 | 3.6 | E6 |
| $k_c$, $c_{min}$ | 15; 0,15 | 4.2 | E4 |
| $z_A$, $A_{1/2}$ | 1,28; $0{,}1B$ | 4.3 | E4 |
| `LambdaMax` / sin CFR | 0,4 / 0,6 | 4.3 | **E4** |
| $\sigma_h$ | 150 fichas | 4.4 | Medir |
| Umbrales de modo ($z$, $u$) | 2,0 / 1,0 / 1,0 (final 2,5) | 4.4 | E7 |
| $\alpha$ por modo | +0,5 / +0,2 / 0 / −0,3 | 4.4 | E7 |
| $\beta_T$ (avanzado) | 1 si $H \le 10$; si no, 0,3 | 4.4 | E7 |
| $T_{skip}$, deadline MC, $T_{hard}$ | 60 / 150 / 250 ms | 4.5 | R1, R2 |
| Tiempo máximo por decisión | **1000 ms** | [SDK] | — |
| Manos por mesa, stack, ciegas | **30, 10 000, 10/20** | [SDK] | — |

### Referencias

- Zinkevich, M., Johanson, M., Bowling, M., Piccione, C. (2007). *Regret Minimization in Games with Incomplete Information*. NIPS.
- Lanctot, M., Waugh, K., Zinkevich, M., Bowling, M. (2009). *Monte Carlo Sampling for Regret Minimization in Extensive Games*. NIPS.
- Lanctot, M. (2013). *Monte Carlo Sampling and Regret Minimization for Equilibrium Computation and Decision-Making in Large Extensive Form Games*. Tesis doctoral, University of Alberta.
- Brown, N., Sandholm, T. (2019). *Superhuman AI for multiplayer poker* (Pluribus). Science 365.
- Brown, N., Sandholm, T. (2019). *Solving Imperfect-Information Games via Discounted Regret Minimization*. AAAI.
- Johanson, M., Bard, N., Lanctot, M., Gibson, R., Bowling, M. (2012). *Efficient Nash Equilibrium Approximation through Monte Carlo Counterfactual Regret Minimization*. AAMAS.
- Johanson, M., Bowling, M. (2009). *Data Biased Robust Counter Strategies*. AISTATS (explotación segura frente a modelos de rivales).
- Billings, D., Davidson, A., Schaeffer, J., Szafron, D. (2002). *The challenge of poker*. Artificial Intelligence 134 (modelado de rivales y rangos ponderados).
- Kit AgentsPoker SDK v0.4.0: `GUIA_AGENTES.md`, `REGLAS.md`, `LEEME.md`, `lib/AgentsPoker.Core.xml`.
