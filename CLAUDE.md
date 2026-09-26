# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Digital-Logic-Sim — a minimalist digital logic simulator (Sebastian Lague's project). Users build circuits from a small set of built-in chips (NAND, tri-state buffer, clock, memory, displays, buses, etc.), package them into custom chips, and simulate them in real time. This working copy is a fork ("electronniqe simu").

## Build / run / test

This is a **Unity 6 project** (editor version `6000.0.46f1`, pinned in `ProjectSettings/ProjectVersion.txt`). A standalone build entry point was added: `Assets/Editor/BuildTools.cs` → `BuildTools.BuildWindows()`, output `Builds/Windows/DigitalLogicSim.exe` (single scene `Assets/Build/DLS.unity`). The built `.exe` runs without Unity/account for the end-user; only BUILDING needs the licensed editor.

**How to build (and thereby compile-check) — you CAN do this yourself:**
- **Interactive-mode CLI (USE THIS):**
  ```
  Unity.exe -projectPath <proj> -executeMethod BuildTools.BuildWindows -buildOutput <proj>/Builds/Windows -quit -logFile <log>
  ```
  Note: **no `-batchmode`.** The editor window opens for a few seconds, builds, and quits by itself (fully autonomous). Grep the log for `BUILD SUCCEEDED` / `error CS`.
- **In the open editor:** menu `Tools > Build Windows Player (fork)` or `Ctrl+Shift+K`.
- **`-batchmode` does NOT work on this machine:** Unity's licensing-client code-signing cert expired (2026-06-09) vs the system clock (2026-08+), so batch builds fail with `Code 10 while verifying Licensing Client signature` / `No ULF license found / Token not found in cache`. The **interactive editor holds a valid license session**, so interactive-mode `-executeMethod` builds fine even though the same signature warning is logged (it's non-fatal there).
- Preconditions: **no editor already open** (project lock — check for a running `Unity.exe` editor first), and a Unity Personal license signed-in via Unity Hub. If the license breaks, open the project once interactively in the Hub to re-establish it.
- A successful build writes `Builds/Windows/.lastbuild` (staleness marker read by `lancerApp.bat`).

**Run:** `lancerApp.bat` — launches `Builds/Windows/DigitalLogicSim.exe`; if the exe is missing (fresh clone, no Unity) it downloads the latest GitHub Release zip into `Builds/Windows`; if a `.cs` is newer than the `.lastbuild` marker and Unity is installed it rebuilds first (interactive mode). `publierRelease.bat` has Claude read the commits since the last Release and return, as structured JSON (`scripts/releaseNotes.ps1`, raw HTTP to the Messages API, `claude-opus-5`, `output_config.format` json_schema), the release notes **and** the next version `V<major>.<minor>.<patch>` chosen from the size of the changes (V1.0.0 first; fallback without a key: raw commit list and minor+1); Enter accepts, then it pushes, zips `Builds/Windows` and publishes the Release via `gh` (`DRYRUN=1` env var skips push/publish for testing) (per-user install in `%LOCALAPPDATA%\Programs\gh`; else opens the release page prefilled) — that is how users without Unity get a new version; a push alone doesn't update what they download. When launching, inject `ANTHROPIC_API_KEY` (see Ask Claude below): a shell that started before a `setx` won't inherit a Machine-scope env var, so pass it explicitly (`$env:ANTHROPIC_API_KEY = [Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','Machine'); Start-Process <exe>`).

**Tests:** none (the Newtonsoft editor tests under `Assets/Scripts/Description/Serialization/Newtonsoft/` are vendored, not coverage). Build the real player to check a change.

### Editor entry point and startup behavior

`UnityMain` (`Assets/Scripts/Game/Main/UnityMain.cs`) is the **only** MonoBehaviour driving the app. In `Awake` it wires up audio and calls `Main.Init`; in `Update` it calls `Main.Update` every frame. Its many public fields (`testProjectName`, `chipToOpenA/B`, `openInMainMenu`, audio test vars, etc.) are editor-only dev knobs set on the scene object:
- In the editor, if `openInMainMenu` is false it auto-opens `testProjectName` / the chosen startup chip — set these to skip the main menu while developing.
- In a build it always starts at the main menu.

Domain reloading is disabled in the editor, so **static state does not reset between play sessions**. `UnityMain.ResetStatics()` manually resets the statics of `Simulator`, `UIDrawer`, `InteractionState`, `CameraController`, and `WorldDrawer`. Any new subsystem that holds static state must be reset there too, or it will carry stale data across runs.

## Save data location

Save format is JSON, one file per chip. Paths are defined in `Assets/Scripts/SaveSystem/SavePaths.cs`:
- **In editor**: `TestData/` at the repo root (`TestData/Projects/<name>/...`). This is committed test data.
- **In a build**: `Application.persistentDataPath`.

A project is a folder containing `ProjectDescription.json` plus a `Chips/` folder of per-chip JSON files. Version fields in each file drive migration via `Assets/Scripts/SaveSystem/UpgradeHelper.cs`; `Main.DLSVersion` / `DLSVersion_EarliestCompatible` (in `Main.cs`) gate compatibility. Bump `Main.DLSVersion` when the save format changes.

## Architecture

The core idea: a chip exists in **three parallel representations**, and most features touch all three. Understanding this split is the key to the codebase.

1. **Description layer** — `DLS.Description` (`Assets/Scripts/Description/`)
   Plain serializable data classes: `ChipDescription`, `SubChipDescription`, `PinDescription`, `WireDescription`, `PinAddress`, `ProjectDescription`, `AppSettings`. This is exactly what gets saved to / loaded from JSON. It has no behavior and no Unity dependencies beyond basic types. The `ChipType` enum (`Types/SubTypes/ChipTypes.cs`) enumerates every built-in chip; `ChipTypeHelper` classifies them (e.g. bus vs. non-bus).

2. **Game / editing layer** — `DLS.Game` (`Assets/Scripts/Game/`)
   The interactive runtime model. `DevChipInstance` is the chip currently open in the editor; its elements (`SubChipInstance`, `WireInstance`, `DevPinInstance`, `DisplayInstance` in `Game/Elements/`) implement `IMoveable` and carry placement/interaction state. `Project` (`Game/Project/Project.cs`) is the top-level session object: it owns the `ChipLibrary` (all available chip descriptions), the `chipViewStack` (chips entered in view mode; bottom of stack is the one being edited), save/rename/delete orchestration, and the **simulation thread**. `ChipInteractionController` and the classes in `Game/Interaction/` handle input, selection, undo (`UndoController`), and placement.

3. **Simulation layer** — `DLS.Simulation` (`Assets/Scripts/Simulation/`)
   `SimChip` / `SimPin` / `PinState` are the runtime execution graph, built from a `ChipDescription` by `Simulator.BuildSimChip` (recursive). `Simulator` (static) runs one sim step over the tree.

### Data flow between the three

- **Load**: JSON → `ChipDescription` (Loader) → `DevChipInstance` (via `DescriptionCreator` / `DevChipInstance.LoadFromDescriptionTest`) for editing, and `Simulator.BuildSimChip` → `SimChip` for execution.
- **Save**: `DescriptionCreator.CreateChipDescription(devChip)` reconstructs a `ChipDescription` from the live `DevChipInstance`, then `Saver` writes JSON. `UnsavedChangeDetector` / `Saver.HasUnsavedChanges` compares live vs. last-saved description.
- **Runtime edits** (add/remove subchip, pin, wire; edit ROM/key/pulse/LED): the Game layer mutates the `DevChipInstance` **and** enqueues a corresponding change on the `Simulator` so the running `SimChip` stays in sync — see the `Notify*` methods in `Project.cs` and the `SimModifyCommand` queue in `Simulator.cs`.

### Simulation threading model (important)

The simulation runs on a **dedicated background thread** (`Project.SimThread`), separate from Unity's main thread, targeting `Prefs_SimTargetStepsPerSecond` ticks/sec. Consequences to respect when editing sim or game code:

- **Never mutate the `SimChip` graph directly from the main thread.** All structural changes go through `Simulator.Add*/Remove*`, which enqueue onto a `ConcurrentQueue<SimModifyCommand>`. `Simulator.ApplyModifications()` drains the queue **on the sim thread** at the top of each step.
- Any structural change sets `needsOrderPass = true`. The next step then runs `StepChipReorder` (the expensive pass that determines a valid subchip traversal order) instead of the fast `StepChip`.
- The main thread reads sim results via `ViewedChip.UpdateStateFromSim(...)`, synced once per main-thread frame. Reads of pin state can transiently be out of sync (threads race) — sim code deliberately swallows "pin not found" exceptions rather than locking. Keep that tolerance in mind.
- `debug_runSimMainThread` in `Project.cs` forces the sim onto the main thread for debugging; `debug_logSimTime` logs step timing.

### How the simulation actually steps

The algorithm is documented in the header comment of `Simulator.cs`. In short: propagate player inputs, then repeatedly process any subchip that is "ready" (all inputs received). Built-in chips are evaluated directly in the big `ProcessBuiltinChip` switch (this is where per-chip behavior lives — add a new built-in's logic here). Custom chips recurse. Feedback loops (a pin depending on its own chip's output) are resolved by processing an unready chip at random. Every ~100 frames the order of unready sequential chips may be randomly swapped to give race conditions (e.g. SR latches) varied outcomes. A built-in PCG RNG (`Simulator.RandomBool`) is used for determinism-adjacent reordering.

### Graphics / UI — immediate mode

`DLS.Graphics` (`Assets/Scripts/Graphics/`) draws everything in **immediate mode** each frame; there are no Unity UGUI prefabs for the app UI. `WorldDrawer` renders the circuit (chips, wires, pins) from the current `DevChipInstance`; `UIDrawer` renders menus (the `Menus/` folder) and owns the active-menu state machine (`UIDrawer.MenuType` / `ActiveMenu`). Rendering primitives come from the vendored **Seb.Vis** library.

### `Seb` — vendored utility library

`Assets/Scripts/Seb/` (`DLS.Seb` assembly) is the author's personal, general-purpose library: `Seb.Vis` (immediate-mode drawing + UI: `Draw`, `DrawManager`, `UI/`), `Seb.Helpers` (`InputHelper`, `StringHelper`, math, etc.), and `Seb.Types`. Treat it as a stable dependency — prefer using its helpers over reimplementing, and avoid gratuitous edits inside it.

### Assemblies

Four project assemblies (`.asmdef`), roughly matching the layers: `DLS` (`Assets/Scripts/DLS.asmdef`, the main game/graphics/sim/save code), `DLS.Description`, `DLS.Seb`, and `DLS.Dev` (`Assets/Dev/`, editor/dev-only tools: `SaveRefac`, `VidTools`). Respect the dependency direction: Description has no upward dependencies; the sim and game layers build on Description.

## Built-in chips

To add or change a built-in chip you typically touch several places, kept in sync manually:
- `ChipType` enum (`Description/Types/SubTypes/ChipTypes.cs`) — the identity.
- `ProcessBuiltinChip` switch in `Simulator.cs` — its simulation behavior.
- `BuiltinChipCreator` (`Game/Project/BuiltinChipCreator.cs`) — its description (pins, size, colour, display).
- `BuiltinCollectionCreator` — which collection / starred list it appears in for new projects.
- Possibly `ChipTypeHelper` if it needs special classification (e.g. bus handling).

**Existing projects don't get new builtins for free**: `ProjectDescription.ChipCollections` is saved per project, so a chip added to a default collection only shows up in *new* projects. `BuiltinCollectionCreator.LateAddedBuiltins` + `AddMissingLateBuiltins` (called from `Loader.LoadProjectDescription`) backfills a new builtin into its collection, but only when it is absent from *every* collection — so a chip the user deliberately removed is never resurrected. Add new post-fork builtins to that list.

**`ChipType` enum values are serialized** (`ChipDescription.ChipType`, an int in the JSON), so only ever APPEND to the enum.

### VCC / GND (constant sources)

`ChipType.Vcc` / `ChipType.Gnd` — no input pins, one 1-bit output permanently held HIGH / LOW (`ProcessBuiltinChip`, alongside CLOCK which is likewise sourceless, so the "ready" logic already handles zero-input chips). They are **components, not I/O pins**, so they never appear in the chip's interface — that is the point: they let a circuit hard-wire values (LUTs, tri-state ROM matrices, tie-offs) without inflating its input count. `ChipTypeHelper.IsConstantType` classifies them. They are in the `BASIC` collection and, being builtins, are automatically in the Ask Claude `add_components` palette (described in `CircuitExporter.BuiltinDesc`).

## Fork additions ("Ask Claude" AI assistant + editor QoL)

This working copy adds an in-app AI assistant and several editor features. Anything with static state added here must be reset in `UnityMain.ResetStatics()` (domain reload is off) — e.g. `AskClaudeMenu.Reset()`.

### Ask Claude — in-app agentic chat (`Game/Project/AskClaude*.cs`, `Graphics/UI/Menus/AskClaudeMenu.cs`)
- `AskClaude.cs` talks to the Anthropic API with **`UnityWebRequest` polled on the main thread** — do **NOT** use `System.Net.Http.HttpClient`: it throws `TypeInitializationException` on init under Unity's Mono (`System.Configuration`/`ExeConfigurationHost` unavailable), freezing the app. This required enabling `com.unity.modules.unitywebrequest` in `Packages/manifest.json` (the project ships a trimmed module set). Streaming SSE is reconstructed into `text` / `tool_use` / `thinking` blocks (`SseHandler`); the agentic loop runs tools on the main thread in `Poll()` and feeds `tool_result` back. Model `claude-opus-4-8`, adaptive thinking (preserved across tool rounds, with signatures), `output_config.effort=high`, prompt caching via `cache_control`. The **API key** comes from the `ANTHROPIC_API_KEY` env var.
- `AskClaudeTools.cs` — tools that edit circuits (`view_module`, `create_module`, `delete_module`, `add_inputs`/`add_outputs`, `add_components`, `remove_elements`, `connect`, `disconnect`, `rename_pins`, `bind_keys`, `set_layout`) plus the read-only QA tools (`truth_table`, `test_sequence`, see below). Each returns `"done (N)"` or the per-item reasons. After each mutation it auto-runs Clean Up + re-frames the camera. The `add_components` palette = **all builtin primitives** + the user's **pinned** custom chips (excludes I/O-pin pseudo-chips — use add_inputs/outputs — and buses); it returns the **labels** the new components got (`NAND#3`), since `#n` suffixes depend on how many instances exist — and they **shift** when something is removed.
- **Nothing auto-saves.** Switching module keeps unsaved edits in memory (`Project.openChips`), exactly like the user's own tab switching; only `create_module` writes (a module must exist as a file to be addressable). Consequence — **everything that needs a chip's interface must read the LIVE description** (`ChipLibrary.GetChipDescriptionForSim`: in-memory when open, saved otherwise), not the library copy: `CircuitExporter` (dependency sections, component bits, netlist `Classify`, sequential detection), `AskClaudeTools.AvailableComponentsDetailed`, and above all `add_components` — `create_module` writes an *empty* chip, so placing from the library copy used to give an instance with **zero pins** and every `connect` failed. `add_components` returns each placed component's exact pin interface for the same reason.
  - Known limit of "never auto-save": if a parent is saved while a child it uses stays unsaved, a restart loads the parent against the *saved* (older) child and wires to pins that were never saved are dropped (with the usual migration resave). The quit guard (`AnyUnsavedChanges`) is what protects the user here.
- **Conversation persistence**: `AskClaude.SaveForProject` / `LoadForProject` store the whole thread (raw API messages, display transcript, incremental-export snapshot) in `<project>/AskClaudeConversation.json`. Written from `Project.SaveFromDescription` (i.e. whenever the user saves — same rule as their circuits), restored by `Main.CreateOrLoadProject` (and cleared when opening a project that has none, so threads never leak between projects). `DropIncompleteTail` trims a dangling `tool_use` with no `tool_result` — a save can land mid-loop, and the API rejects that shape.
- **Mid-loop user message**: the chat input stays active while Claude iterates. `AskClaude.Send` queues the text (`pendingUserText`, shown as "Toi (en attente…)"), and `Poll()` appends it as a text block **after** the `tool_result` blocks of the round that is finishing, so the API contract holds and Claude keeps its whole thread (thinking included) while reacting immediately. If the turn ends first, it is sent as a normal follow-up; on an API failure it is not silently dropped.
- `AskClaudeMenu.cs` — non-blocking, resizable right-side overlay (drag the left divider). Transcript scroll (mouse wheel + draggable bar). `COPIER` copies the full transcript (context + reasoning + tool calls/results) for diagnosis. It compensates the camera so the circuit stays visible in the area not covered by the panel.

### QA / debug harness (`Game/Project/CircuitTester.cs`)
Lets the assistant (and the truth-table view) **run the real simulator on an isolated copy** of a chip, so it can verify its own work; the live circuit and the sim thread are never touched.
- `BuildIsolatedSim(liveDesc, lib)` wraps the chip as the single subchip of a throwaway harness (the simulator only processes *subchips*, never the root's own logic) and resolves it under a temp name through `ChipLibrary.SimOverride` — so it simulates the **live, possibly unsaved** description and works for a never-saved chip too. `TruthTableComputer` uses the same builder (fresh sim per row).
- **Clock in tests**: no real time passes in the harness, so the builtin CLOCK (whose level is `simulationFrame / stepsPerClockTransition`, i.e. 250 ticks per transition) would never toggle over a few ticks. `Simulator.forcedClockState` (-1 = normal, 0/1 = forced; reset in `Simulator.Reset` and in the harness `finally`) pins every clock chip, so a test **drives** edges: `clocks: N` on a step plays N full cycles (LOW→HIGH per cycle, then a final LOW settle) and `set {pin:"CLOCK"}` sets the level by hand. All clock chips share one phase in this simulator, so a single global level is faithful — including clocks nested in sub-bricks (`ContainsClock` walks the sim tree). When a tested circuit contains a clock and the sequence never drives it, the report says so explicitly; the truth table pins the clock to 0 for determinism. This exists because Claude otherwise "fixes" the dead clock by rewiring the circuit (disconnecting CLOCK, adding test inputs) — the prompt now forbids that.
- `RunSequence` = the `test_sequence` tool: applies a list of steps (`set` input values, then N ticks) and reports, after each step, output pins + everything **visible** on the chip face — LED ON/off, 7-segment (decoded to the character it forms, else the lit segments), RGB/dot displays as a 16×16 ASCII grid (only reprinted when it changes), plus optional internal probe pins (`watch`). Sim state persists between steps, so sequential circuits (latches, counters, RAM) behave as in the app. Report only ever mentions things that exist (no "no LED" filler) — keep it token-lean.
- Displays are enumerated exactly like `DevSceneDrawer.DrawDisplay` (builtin display chips placed directly + displays exposed on a custom sub-chip's face, recursively), resolved in the sim by their subchip-ID path.
- KEY chips are pressable: `SimKeyboardHelper.SetVirtualKeys` overrides the real keyboard for the duration of a test (always cleared in `finally`, and in `UnityMain.ResetStatics`).
- Everything runs with the sim thread parked via `Project.RunWithSimulationPaused`, and restores `Simulator.simulationFrame` / forces `needsOrderPass` afterwards (the `Simulator` is static).
- The system prompt makes testing **mandatory** before Claude declares a module finished (truth_table if combinational, test_sequence if sequential).

### Editing circuits programmatically (used by the tools; reusable)
- `DevChipInstance.AddNewSubChip / AddNewDevPin / AddWire / DeleteWire / DeleteSubChip` mutate the dev chip **and** enqueue the matching `Simulator.Add*/Remove*` so the sim thread stays in sync (never touch `SimChip` from the main thread directly). Build a wire between two known pins with `new WireInstance(srcConnInfo, tgtConnInfo, points, spawnOrder)` then `AddWire(wire, false)`; a dev pin with `new DevPinInstance(new PinDescription(name, id, pos, bitCount, colour, mode), isInput)`; new element IDs via `IDGenerator.GenerateNewElementID(devChip)`.
- **Pin labels (subtle):** a chip's interface disambiguates duplicate *raw* pin names (two pins both literally `IN` → displayed `IN1`/`IN2`) via `CircuitExporter.Disambiguate`. Sub-chip pin labels MUST use the SAME disambiguated names **everywhere** (interface, netlist `Classify`, and the `connect` resolver `BuildPinLabelMap`) — a mismatch here silently breaks tool wiring.

### Editor quality-of-life
- **Layout constraints** (`LayoutCol` / `LayoutRow`, 0 = automatic): optional per-element hints honoured by Clean Up, set via the `set_layout` tool. `LayoutCol` (≥1) forces the column (bigger = further right, equal = same column, overrides the depth-based placement); `LayoutRow` orders within a column (bigger = higher) **and** aligns elements of *different* columns that share the same value onto one horizontal line (the column is shifted as a rigid block, so spacing/overlaps are untouched). Unconstrained elements keep their relative order — their sort keys are interpolated between the constrained ones — so a chip with no hints lays out exactly as before. Fields live on `SubChipDescription` / `PinDescription` (and on `IMoveable`), so they persist; they are purely additive and default to `0`, so **no save-format migration and no `DLSVersion` bump** (bumping would make every open chip look dirty, since `HasUnsavedChanges` diffs the serialized description).
- `Game/Interaction/CircuitAutoLayout.cs` — **Clean Up**: lays subchips in columns by signal-propagation depth, sorts dev pins **by name group, biggest number on top (A.. before D..; D4, D3, D2, D1, D0 — `PinOrderCompare`)**, orders each subchip column with **barycenter sweeps** (fewer wire crossings, and the remaining ones spread out rather than piled up), grid-snaps, and routes wires with **as few bends as possible**: a wire tries a straight line, then one bend (diagonal + horizontal lead into the target, or lead out of the source + diagonal), then two bends (lead-diagonal-lead) — first candidate that clips nothing and overlaps nothing wins. Only a pin feeding ≥ `TrunkFanOut` (3) wires gets a vertical **channel** in the gap after its column (gaps widen to fit; the topmost pin takes the farthest channel so leads nest): its wires go lead → shared channel (trunk, the only overlap allowed) → horizontal branch at the target's height. That is what keeps a fan-out-heavy circuit (ALU4: 8 inputs → 4 chips) readable as a regular grid instead of a heap of diagonals, while everything else stays direct. A wire whose branch would clip a component of a column it spans (or that runs backwards) gets a detour above/below the obstacle band on the nearest free track; wires from the same column to the same component are routed as a **group** (one side, nested tracks) so they travel together. **Wires may cross but never overlap** (`CollinearOverlap` check against every segment already laid). A `CleanUp: …` line in `Player.log` reports wires/detours/remaining overlaps. **Undo**: `UndoController.LayoutSnapshot` records every position and every wire's points before/after, so Ctrl+Z restores the exact previous layout (a plain move record would lose the wire points). A background-coloured "crossing gap" halo was tried for crossings and rejected by the user — don't redo it.
- Hover highlighting (`DevSceneDrawer.UpdateHoverHighlight`): hovering a wire highlights every wire joined to it and tints the components at their ends; hovering a component highlights its wires (same look as hovering them) and tints the components at the other end.
- Non-blocking overlays: drawing a `UI.DrawPanel` marks `InteractionState.MouseIsOverUI`, which blocks scene interaction only under the panel; suppress scene keyboard shortcuts while an on-screen text field is focused via `KeyboardShortcuts.TextInputActive`.
- `Graphics/UI/Menus/BulkRenamePinsPopup.cs` — RENAME on the right-click menu of a **selected** dev pin when ≥ 2 pins are selected: prefix + number, the topmost pin gets the highest number (`D` on 5 pins → D4..D0). For this to work the controller no longer clears the selection when the right-click lands on a selected element (`HandleRightMouseDown`), and a right-click on a pin's state toggle (which registers as an *unspecified* element so left-click keeps toggling) is forwarded to the pin via `InteractionState.ContextElementUnderMouse` / `ElementForContextMenu`, and `ContextMenu` shows `entries_multiDevPin` (RENAME / DELETE selection) instead of the single-pin entries. Clean Up (`PlaceColumn`) adds a gap of two pin heights between consecutive dev pins of different name groups (name minus trailing digits) when at least one group has ≥ 2 members.
- `Graphics/UI/Menus/ConfirmationPopup.cs` — generic "are you sure?" popup (message + confirm callback); used by the right-click **DELETE** entry on bottom-bar chips (`ContextMenu.entries_bottomBarChip` → `Project.DeleteChip`).
- `Graphics/UI/Menus/InfoPopup.cs` — generic read-only popup (message + OK, `MenuType.Info`); used by all three
  optimisation tools to report their result. Both popups also show the circuit **depth** (longest chain of gates
  from an input to an output, `NandMinimizer.NetlistDepth` / `GateMapper.CoverDepth`) — measured only, never optimised.
- Window-close guard: `UnityMain.OnWantsToQuit` (via `Application.wantsToQuit`) prompts on unsaved changes.
- **Keyboard layout (AZERTY!)**: Unity's legacy input reports keys by *physical* position named after the US layout, so on the user's AZERTY keyboard Ctrl+Z arrived as `KeyCode.W` and undo never fired (Ctrl+N worked: same position). `KeyboardShortcuts.Physical(KeyCode)` maps the letter we mean to the KeyCode Unity will report under the current Windows layout (user32 `VkKeyScanExW` + `MapVirtualKeyExW` → US scan-code table); every letter shortcut goes through it, and so does `SimKeyboardHelper` (KEY chips are bound by typed character). Any new letter shortcut must use it too.
- The Ask Claude input field drops focus on any click outside it (`AskClaudeMenu`): the field only sees unconsumed mouse downs and scene clicks are consumed by the controller first, so it used to stay focused forever and block every scene shortcut via `TextInputActive`.
- `Seb/SebVis/UI/UI.cs` `InputField` was patched to horizontally scroll so the caret stays visible on long text (the only intentional edit inside vendored Seb).
- `SavePaths.UseBuildPathInEditor = true` unifies editor + build save locations to `Application.persistentDataPath`; default I/O-pin-name display is `Always`.

### Circuit optimisation tools (bottom-bar right-click on a chip)

Three entries, all enabled only for a **custom** chip (a builtin is refused); `Optimise` additionally
requires the chip to be **combinational**.

- **`Count nand`** (`Game/Project/NandCounter.cs`) — total NAND needed to build one instance, expanding
  custom sub-chips recursively. Non-decomposable builtins (clock, RAM, displays, KEY, VCC/GND) are
  listed separately; buses and merge/split are not counted (pure wiring, zero gates).
- **`Nand only`** (`Game/Project/NandFlattener.cs`) — flattens the whole hierarchy into one chip of
  builtin primitives. Pure netlist operation: a `WireDescription` is only "source pin -> target pin"
  (`ConnectionType` is routing, `Simulator.BuildSimChipRecursive` ignores it). Each inlined chip's
  interface pin becomes a virtual node that is resolved away, so a signal crossing several levels
  becomes one direct wire. Buses vanish too (a bus origin is a pass-through, a terminus a dead end).
- **`Optimise`** (`Game/Project/GateMapper.cs`) — opens the package palette
  (`Graphics/UI/Menus/GatePaletteMenu.cs`), then rebuilds the chip out of the ticked packages while
  **minimising the number of packages**. Hierarchy is flattened on the way in, so it works directly on
  a chip built from sub-bricks.

#### The pipeline

`NandMinimizer.ReadAsNandNetlist` (flatten if needed, then `Extract` to an AIG) -> AIG rewriting +
don't-cares -> cut-based technology mapping -> bin-packing -> local search -> emit -> **verify**.

- `AigCore.cs` — the shared AIG: literals (`node*2 + complement`), structural hashing with constant
  folding, 4-input cut enumeration, the minimal-structure library, and bit-parallel equivalence
  checking. Node 0 is the constant, nodes `1..NumPI` the inputs.
- `AigDontCare.cs` — observability don't-cares, computed **exactly** (simulate the whole circuit twice,
  once with the node flipped, XOR the outputs). A node may be replaced by anything that agrees with it
  wherever it is observable.
- `NandMinimizer.cs` — DAG-aware rewriting (Mishchenko/Chatterjee/Brayton, DAC 2006), alternating with
  the don't-care pass until neither finds anything.
- `GateMapper.cs` — cut-based mapping (Chen & Cong's DAOmap scheme), then `Repack` and `LocalSearch`.
- `GatePackages.cs` — the palette. Each line is a package type (gate kind + gates per package);
  several lines may hold the same gate, in which case the effective capacity is the largest.
- `ChipEmitHelper.cs` — shared plumbing for building a `ChipDescription` from scratch.

#### Things that are easy to get wrong here

- **The objective is NAND gates, not AIG nodes.** A NAND computes `~(a&b)`, so an AND node whose output
  is *also* needed uncomplemented costs an extra inverter. Driving the search on node count alone makes
  circuits WORSE (a 4-NAND XOR "optimises" to 3 nodes + 3 inverters = 6 gates). `MappedCost` is the
  real metric everywhere.
- **The package objective is a sum of ceilings**, which no linear per-gate weighting can express. Two
  passes exist precisely for it, and both were measured to pay: `Repack` (a gate can be taken from
  another package type — an inverter is a NOR with its inputs tied — so emptying a nearly empty type
  removes a chip) and `LocalSearch` (re-implement one signal with a runner-up cut, judged on the true
  cost). On an 8-bit ALU they gave 38 -> 37 -> 36 packages.
- **Don't-care gating must budget the real cost**, not the input count. A flat "16 inputs max" silently
  disabled the pass on a 19-input ALU — the single biggest loss measured (42 -> 38 packages once fixed).
  Cost is `nodes x words` in memory and `nodes^2 x words` in time; both are budgeted in `Applicable`.
- **Generated chips must be registered in `ProjectDescription.AllCustomChipNames`.** It is the only list
  the loader rebuilds the library from — it does not scan `Chips/`. Starring a chip rewrites the
  description untouched, so a generated chip saved that way comes back invisible: present on disk,
  unusable, and undeletable (`DeleteChip` threw). `ContextMenu.CommitGeneratedChip` calls
  `UpdateAndSaveProjectDescription()` for this reason, and `Project.DeleteChip` now tolerates a chip
  missing from the library.
- **Generated chips must not look "modified" when opened.** `HasUnsavedChanges` diffs the serialized description, and a `SubChipDescription` written with `OutputPinColourInfo = null` differs from the live one (an entry per output pin). `ChipLibrary.FillMissingOutputPinColours` fills the defaults on load and on `NotifyChipSaved` (buses excepted). `Assets/Editor/DirtyCheck.cs` (`-executeMethod DirtyCheck.Run -benchProject "PC"`) reloads every chip of a project and prints the first JSON difference — run it whenever the red star shows up on a freshly opened chip.
- **Nothing is ever written before verification.** Every generated chip is read back from its own
  description (slot by slot for packages, so the emitted wiring itself is checked) and compared to the
  original over the whole input space — exhaustively up to 20 inputs, sampled beyond, and the report
  says which. A mismatch aborts and creates nothing.
- **Unconnected input pins read LOW** in the simulator, so `Extract` models a missing driver as
  constant 0. Two wires into one pin makes the result order-dependent, and is refused.
- Generated package bricks are named `4xAND2`, `6xNOT`, `1xNOR2`… A one-gate 2-input NAND package *is*
  the builtin NAND (no wrapper brick). An existing chip of the same name is reused **only** if its
  interface and function are verified identical, otherwise a suffixed name is taken.
- Statics added here must be reset in `UnityMain.ResetStatics()`: `GatePackages.Reset()` and
  `GatePaletteMenu.Reset()` are. `AigLibrary`'s table is a pure immutable cache and is deliberately not.

#### Measured dead ends — do not redo these

Each was implemented, measured on the ALU project, and removed because it gained **nothing**:

- rebuilding the circuit from its truth tables (BDD / Shannon, several variable orders) — 0, 50x slower;
- structural choices (ABC's `dch`: merge several structures, group equivalent nodes, let the mapper mix
  cuts across them) — 0, even with 94 multi-member classes on the ALU;
- two-divisor resubstitution in the don't-care pass — 0, 8x slower;
- banning a package type outright, marginal-cost feedback, fill-informed "squeeze" weightings,
  multi-start local search, longer optimisation loops;
- a 5x wider mapper search (500 weightings, 25 s budget) — 0 on every circuit.

The lesson: search effort is **not** the limiting factor, and neither is the AIG structure. Every real
gain came from the don't-care pass or from the two package-aware passes.

#### Regression bench

`Assets/Editor/NandBench.cs` (editor-only, nothing written to disk):

```
Unity.exe -projectPath <proj> -executeMethod NandBench.Run -benchProject "ALU" [-benchChip "ALU8"] -quit -logFile <log>
```

It flattens every chip of a project, optimises each twice (bare NANDs, then the full palette), runs
palette scenarios (including "only a 1-gate package" and "XOR only", which must be refused), checks
that no unticked package is ever used, and verifies every result against the original. It exits
non-zero on any failure. It caught both the deletion bug and the NAND-only gap — run it after any
change to these files.

#### Runtime limits worth knowing

Unity's **Mono** backend, .NET Standard 2.1 (`scriptingBackend Standalone: 0`). No
`System.Numerics.BitOperations`, no `System.Runtime.Intrinsics` — which matters, because the hot loops
(don't-cares, equivalence checking) are pure 64-bit word operations that AVX2 would do 4x faster. The
don't-care pass is parallelised over input-pattern ranges (`Parallel.For`), which took an 8-bit ALU from
11.8 s to 4.6 s. If speed becomes a problem again, flatten `ulong[][]` to a single `ulong[]` first.
