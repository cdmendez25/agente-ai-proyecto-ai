# Agente híbrido de Limit Texas Hold'em: AgentsPoker SDK v0.4.0

Proyecto final de **Fundamentos de Inteligencia Artificial**. El agente combina tres cosas:

- **ES-MCCFR**: una estrategia base aprendida jugando contra sí mismo.
- **Explotador**: modelo de rivales + Monte Carlo + EV.
- **Capa de torneo**: decide cuánto arriesgar según el puesto en la mesa.

Todo cabe en **un solo archivo `.cs`**, como exige el torneo.

| Documento | Para qué |
|---|---|
| [`docs/Arquitectura_Agente_Poker_Actualizada.pdf`](docs/Arquitectura_Agente_Poker_Actualizada.pdf) | Arquitectura vigente |
| [`docs/HOJA_DE_RUTA.md`](docs/HOJA_DE_RUTA.md) | **Qué se hizo, resultados medidos y limitaciones** (leer primero) |
| [`docs/DISENO_AGENTE.md`](docs/DISENO_AGENTE.md) | Diseño técnico completo: fórmulas, pseudocódigo, plan de pruebas |
| [`docs/curso/`](docs/curso/) | Instructivo de entregas, reglas del póker y preguntas frecuentes del curso |
| [`Kit_AgentsPoker_SDK/LEEME.md`](Kit_AgentsPoker_SDK/LEEME.md), `GUIA_AGENTES.md`, `REGLAS.md` | Documentación oficial del kit |

---

## 1. Puesta en marcha (una sola vez)

1. **Instala el .NET SDK 10 (o 9).** El *runtime* solo no basta.
   - **macOS sin permisos de administrador**, con el script oficial de Microsoft:
     ```bash
     curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh && bash dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
     ```
     Luego, en **cada terminal nueva** (o añádelo a `~/.zshrc`):
     ```bash
     export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
     ```
   - **Windows o macOS con instalador:** https://dotnet.microsoft.com/download
   - Comprueba que `dotnet --version` muestra `10.x` o `9.x`.
2. **Clona el repositorio y entra al kit:**
   ```bash
   git clone https://github.com/cdmendez25/agente-ai-proyecto-ai.git
   cd agente-ai-proyecto-ai/Kit_AgentsPoker_SDK
   ```
3. **Primera compilación.** Tarda 1–2 minutos y necesita internet, porque descarga el compilador.
   ```bash
   bash run.sh list
   ```
   Debe mostrar `AgenteHibrido.cs OK` y `AgenteHibridoV2.cs OK`.

En Windows, cambia `bash run.sh` por `.\run.cmd` y `bash entrenar.sh` por `.\entrenar.cmd`.

---

## 2. Qué hay en el repositorio

```
Kit_AgentsPoker_SDK/
├── Agents/
│   ├── AgenteHibrido.cs       ← AGENTE QUE SE ENTREGA (V4b: CFR + explotador V3b + torneo T1)
│   ├── AgenteHibridoV2.cs     ← experimento: abstracción v2 (NO se entrega, el laboratorio entrena sobre él)
│   ├── MiAgente.cs, Rival.cs  ← ejemplos del kit (MiAgente = línea base del curso)
├── Entrenamiento/             ← LABORATORIO (no se entrega)
│   ├── Program.cs             ← subcomandos (ver §3)
│   ├── Cfr/LhSim.cs           ← simulador propio de Limit Hold'em (validado contra el SDK)
│   ├── Cfr/EsMccfr.cs         ← entrenador ES-MCCFR + exportación de la política al .cs
│   ├── Tests/                 ← pruebas unitarias, validación del simulador, comparación de versiones
│   ├── Tools/                 ← calibración de la tabla preflop, medición de all-in
│   └── cfr_checkpoint.bin     ← estado del entrenamiento v2 (242 M manos): permite CONTINUAR entrenando
├── lib/AgentsPoker.Core.dll   ← motor del torneo (el mismo que usa la Arena)
├── tools/AgentsPoker.Sim/     ← banco de pruebas oficial del kit (run.sh)
└── run.sh / entrenar.sh       ← lanzadores
docs/                          ← arquitectura, diseño, hoja de ruta y material del curso
```

---

## 3. Comandos (desde `Kit_AgentsPoker_SDK/`)

| Comando | Para qué |
|---|---|
| `bash run.sh check Agents/AgenteHibrido.cs` | Compila **igual que la Arena** (C# 9, .NET Standard 2.1) |
| `bash run.sh bench --agent Agents/AgenteHibrido.cs --builtin 'Heuristic*5' --matches 200 --seed 42` | Checklist del kit: Clasif%, Gana%, ms máx, timeouts, errores |
| `bash run.sh match --agent Agents/AgenteHibrido.cs --builtin 'Heuristic*5' --hands 5 --verbose` | Ver manos una a una |
| `bash entrenar.sh pruebas` | 25 pruebas unitarias (cartas, fuerza de mano, legalidad) |
| `bash entrenar.sh validar-sim 4000` | El simulador reproduce al SDK (debe dar **0 diferencias**) |
| `bash entrenar.sh comparar 1000 BASE,V4b` | **Comparación emparejada**: BASE = `AgenteHibrido.cs`; V0…V4b = variantes de `AgenteHibridoV2.cs` |
| `bash entrenar.sh entrenar-cfr 3600 8` | Sigue entrenando la v2 (segundos, hilos) desde el checkpoint; al terminar escribe la política en `AgenteHibridoV2.cs` |
| `bash entrenar.sh medir-allin 3000` | Frecuencia de all-in según el tipo de rivales |
| `bash entrenar.sh 30` | Laboratorio del kit: bb/100, latencias p50/p95/p99, Clasif% |

Con `--agent Agents/X.cs` siempre se usa ese archivo exacto, aunque haya dos clases agente en la carpeta.

---

## 4. Estado actual (resumen; el detalle está en `docs/HOJA_DE_RUTA.md`)

- **Entregable:** `Agents/AgenteHibrido.cs` (V4b).
  - Abstracción de 3 564 situaciones.
  - CFR entrenado con 62 M manos.
  - Explotador V3b.
  - Torneo T1.
- **Checklist del kit:** `check` OK; **0 timeouts, 0 errores**; `ms máx` ≈ 8–11 ms (el límite es 300).
- **Resultados** (1 000 mesas emparejadas, clasificación frente a la heurística V0):

  | Mesa | Diferencia |
  |---|---|
  | Heuristic×5 | +1,3 (empate) |
  | Mixto | +2,2 (empate) |
  | **Heads-up contra el Jefe** | **+5,0 (mejora significativa)** |

- **Experimento v2:** 56 736 situaciones y 242 M manos. **No mejoró**: en heads-up quedó −3 puntos. Está guardado para seguir trabajando en él.
- **Punto débil:** en la mesa final contra 5 Heuristic gana ≈14,5 % de las veces (el azar daría 16,7 %).

## 5. Ideas para seguir mejorando (en orden)

1. **Abstracción intermedia (~20 000 situaciones) + Linear CFR + 6–8 h de entrenamiento.** La v2 no tuvo entrenamiento suficiente para tantas situaciones.
2. **Explotador más preciso.** Añadir la estadística "fold ante subida preflop" y que los rangos se actualicen con las acciones postflop.
3. **Capa T2 para la mesa final:** probabilidad de terminar 1.º.

**Regla del equipo:** una versión nueva solo reemplaza a `AgenteHibrido.cs` si **gana en `comparar` con 1 000 mesas** y no empeora en ningún tipo de mesa.

---

## 6. Reglas que no se pueden romper (torneo)

- Se entrega **un solo archivo**. Su nombre de clase y de archivo deben ser **el nombre registrado** en el formulario del curso (es inmutable).
- C# 9 / .NET Standard 2.1. Están prohibidos archivos, red, hilos y reflexión, y también el estado `static` mutable (se permite `static readonly` con datos que no cambian).
- El comprobador marca el archivo si **contiene** las cadenas `File.`, `Process.`, `Parallel.`, `System.IO`, `Thread.Sleep`, `Task.Run`, … **aunque estén en comentarios**. En el laboratorio sí se pueden usar.
- **No se suben** `bin/`, `obj/` ni los ZIP. La Arena de Unity (`ELEMENTAL_POKER_V0.2.zip`, 55 MB, solo Windows) se comparte aparte; sirve para medir tiempos en Mono.
