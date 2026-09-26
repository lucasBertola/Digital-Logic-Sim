# Digital-Logic-Sim — fork with an AI assistant and circuit tools

This is a fork of [Sebastian Lague's Digital-Logic-Sim](https://github.com/SebLague/Digital-Logic-Sim)
(based on version 2.1.6). Everything below this section is the original project; this section lists
what the fork adds on top.

## What's new in this fork

### Ask Claude — an AI assistant inside the editor
- A resizable chat panel (right side of the screen) where you describe the circuit you want and Claude
  builds it in the open project: it creates modules, adds inputs/outputs and components, wires them,
  renames pins, binds keys and lays everything out.
- Claude **tests its own work** with the real simulator before calling a module done: a truth table
  for combinational circuits, a step-by-step sequence (inputs, clock cycles, key presses) for
  sequential ones, reporting outputs, LEDs, 7-segment and pixel displays.
- Nothing is auto-saved: Claude's edits sit in memory exactly like your own until you save.
- The conversation is stored with the project and restored when you reopen it.
- You can keep typing while Claude is working; your message is taken into account mid-task.
- Requires an Anthropic API key in the `ANTHROPIC_API_KEY` environment variable.

### Circuit optimisation tools (right-click a custom chip in the bottom bar)
- **Count nand** — how many NAND gates the chip needs once fully expanded.
- **Nand only** — flattens the whole hierarchy into a single chip made of builtin primitives.
- **Optimise** — rebuilds a combinational chip out of a palette of gate packages (AND, OR, XOR, NOT,
  NAND, NOR… with several gates per package) while minimising the number of packages. Uses
  AIG rewriting, observability don't-cares and cut-based technology mapping. Every generated chip is
  verified against the original over its whole input space before anything is written.
- Both reports also show the circuit depth (longest gate chain from an input to an output).

### Editor quality of life
- **Clean Up** — automatic layout: components in columns by signal depth, pins sorted naturally
  (A2 before A10), grid snapping, and wires only detoured when they would cross a component.
- **VCC / GND** builtin chips: constant HIGH / LOW sources that don't add inputs to your chip.
- **Right-click on empty space** opens the IN/OUT collection at the cursor (same popup as the bottom bar,
  shift-click to place several at once; it stays until you click elsewhere).
- Confirmation popup before deleting a chip, and a prompt on quit when there are unsaved changes.
- Editor and standalone build share the same save location.

### Building
Unity `6000.0.46f1`. A build entry point is provided in `Assets/Editor/BuildTools.cs`
(`Tools > Build Windows Player (fork)` in the editor, or `BuildTools.BuildWindows` from the command
line); the output goes to `Builds/Windows/`. `lancerApp.bat` launches the last build with the API key
injected. See `CLAUDE.md` for the architecture notes.

---

# Digital-Logic-Sim
A minimalistic digital logic simulator, which I created as part of my video series: [Exploring How Computers Work](https://www.youtube.com/playlist?list=PLFt_AvWsXl0dPhqVsKt1Ni_46ARyiCGSq).
<br>You can find the latest builds over [here](https://sebastian.itch.io/digital-logic-sim).<br>

Note: Pull requests are welcome, but please be aware that I'm far more likely to merge performance/ux improvements and bug fixes than new built-in chips or features. I do hope to provide some form of mod support in the future, but don't have any concrete plans for it right now. If you'd like to ask or discuss anything relating to development with me/others, check out [Discussions/Dev](https://github.com/SebLague/Digital-Logic-Sim/discussions/categories/dev).

[![IMAGE ALT TEXT HERE](https://raw.githubusercontent.com/SebLague/Images/master/Exploring%20how%20computers%20work.jpg)](http://www.youtube.com/watch?v=QZwneRb-zqA)
