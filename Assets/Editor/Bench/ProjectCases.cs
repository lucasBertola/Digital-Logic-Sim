using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;

namespace DLS.Bench
{
    // Drives a REAL Project (its simulation thread, the main-thread frame handshake, player input states,
    // the display sync of the dev pins) on a fixture project. Runs serially: it uses Project.ActiveProject
    // and the shared Simulator modification queue.
    public static class ProjectCases
    {
        public static List<(string name, Func<string> run)> All(string projectDir) => new()
        {
            ("live project: sim thread computes Add4 from player inputs and syncs the dev pins", () => LiveAdd4(projectDir)),
            ("live project: switching chip keeps the sim thread going (Registre8 loads on the clock)", () => LiveRegister(projectDir)),
            ("live project: switching chip and back keeps the running memory (RAM word, register) and never draws driven wires as floating", () => LiveSwitchKeepsMemory(projectDir)),
            ("live project: switching fast through never-visited chips never stops or breaks the sim thread", () => LiveManySwitches(projectDir)),
            ("live project: a chip's new interface reaches the chips using it when it is SAVED (removed pin unwired, same-name pin kept, new pin shown, no star)", () => LiveInterfaceChange(projectDir)),
            ("live project: a parent with its own unsaved edits keeps them (and its star) when a chip it uses is saved with a new interface", () => LiveInterfaceDirtyParent(projectDir)),
            ("live project: RUN FAST takes the state over, runs, leaves on an edit or a chip switch, and gives the state back to the gates", () => LiveRunFast(projectDir)),
            ("live project: a wire from an input pin follows the input (open CPU, go into Registre8, toggle each input: the state the wire is drawn from follows)", () => LiveInputWires(projectDir)),
            ("live project: CREATE CHIP through the popup (selection cleared by the click, as in the app), then Ctrl+Z", () => LiveCreateChip(projectDir)),
        };

        static Project Open(string projectDir, string chip, string projectNameOverride = null)
        {
            ChipLibrary lib = BenchProject.LoadLibrary(projectDir, out _);
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(projectDir, "ProjectDescription.json")));
            if (projectNameOverride != null) pd.ProjectName = projectNameOverride; // where a save goes: never the fixture nor the user's project
            pd.Prefs_SimPaused = false;
            pd.Prefs_SimTargetStepsPerSecond = 2000;
            var p = new Project(pd, lib) { audioState = new AudioState() };
            p.LoadDevChipOrCreateNewIfDoesntExist(chip);
            p.StartSimulation();
            return p;
        }

        static void Close(Project p) { p.NotifyExit(); Thread.Sleep(30); }

        static DevPinInstance Pin(Project p, string name) => p.ViewedChip.Elements.OfType<DevPinInstance>().First(x => x.Name == name);
        static void Input(Project p, string name, int v) => Pin(p, name).Pin.PlayerInputState = (uint)v;
        static int Output(Project p, string name) => (int)(Pin(p, name).Pin.State & 0xFF);

        // Pumps the main-thread side (what Project.Update does for the sim) until `ready` or the timeout
        static bool Pump(Project p, Func<bool> ready, int timeoutMs = 3000)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                p.TickMainThreadForTests();
                if (ready()) return true;
                Thread.Sleep(2);
            }
            p.TickMainThreadForTests();
            return ready();
        }

        static string LiveAdd4(string projectDir)
        {
            Project p = Open(projectDir, "Add4");
            try
            {
                string[] a = { "A3", "A2", "A1", "A0" }, b = { "B3", "B2", "B1", "B0" }, s = { "S3", "S2", "S1", "S0" };
                int Sum() => s.Aggregate(0, (acc, n) => acc << 1 | (Output(p, n) & 1));
                void SetWord(string[] pins, int v) { for (int i = 0; i < 4; i++) Input(p, pins[i], v >> (3 - i) & 1); }

                SetWord(a, 5); SetWord(b, 7); Input(p, "C_in", 0);
                if (!Pump(p, () => Sum() == 12 && Output(p, "C_out") == 0)) return $"5+7: S={Sum()} C_out={Output(p, "C_out")} (expected 12, 0) after 3 s";
                SetWord(a, 9); SetWord(b, 8); Input(p, "C_in", 1);
                if (!Pump(p, () => Sum() == 2 && Output(p, "C_out") == 1)) return $"9+8+1: S={Sum()} C_out={Output(p, "C_out")} (expected 2, 1) after 3 s";
                if (!(p.simAvgTicksPerSec >= 0)) return "no tick rate reported";
                return null;
            }
            finally { Close(p); }
        }

        // User, 2026-10-01: "switching chips, wires change colour for 2 s; are those real values changing my RAM?" —
        // reproduced: every pin was drawn floating (flicker) until the rebuilt sim had run, and the rebuilt sim
        // started from the last SAVED memory (an edited RAM word came back to its saved value).
        static string LiveSwitchKeepsMemory(string projectDir)
        {
            Project p = Open(projectDir, "CPU");
            try
            {
                foreach (DevPinInstance d in p.ViewedChip.Elements.OfType<DevPinInstance>().Where(d => d.IsInputPin)) d.Pin.PlayerInputState = 0; // nothing loads: the memory holds
                // wait until the SIM sees them (not a fixed delay: a cold first run is slower, and an edit made while the
                // saved Load_A / We_RAM were still 1 got overwritten at the next clock edge — the case failed once that way)
                if (!Pump(p, () => p.rootSimChip.Program != null && p.rootSimChip.Program.StepsRun > 2000 && p.rootSimChip.InputPins.All(pin => (pin.State & 0xFFFF) == 0), 5000)) return "test setup: the inputs never reached 0 in the sim";
                Pump(p, () => false, 100);
                MemoryRules rules = MemoryLayout.ParseRules(MemoryLayoutCases.CpuRulesJson, out _);
                var pol = new Dictionary<string, int[]>();
                ChipDescription cpu = p.chipLibrary.GetChipDescription("CPU");
                foreach (string t in MemoryLayout.BankTypes(cpu, rules, p.chipLibrary)) { string e = MemoryLayout.Verify(t, rules, p.chipLibrary, pol); if (e != null) return "layout: " + e; }
                List<MemoryBank> Banks() => MemoryLayout.Banks(p.rootSimChip, cpu, rules, pol, p.chipLibrary, out _);
                uint Read(string bank, int w) { uint v = 0; p.RunWithSimulationPaused(() => v = Banks().First(b => b.Name == bank).Read(w)); return v; }

                uint ram9 = (Read("RAM", 9) ^ 0x5A) & 0xFF, regA = (Read("Registre A", 0) ^ 0x33) & 0xFF;
                p.RunWithSimulationPaused(() =>
                {
                    var bk = Banks();
                    bk.First(b => b.Name == "RAM").Write(9, ram9, p.rootSimChip.Program);
                    bk.First(b => b.Name == "Registre A").Write(0, regA, p.rootSimChip.Program);
                });
                Pump(p, () => false, 150);
                { uint r9 = Read("RAM", 9), ra = Read("Registre A", 0); if (r9 != ram9 || ra != regA) return $"test setup: the edit did not hold before switching (RAM[9] {r9} wrote {ram9}, A {ra} wrote {regA})"; }

                p.LoadDevChipOrCreateNewIfDoesntExist("Add4"); // FIRST visit of Add4
                // no main-thread frame (a slow frame): once Add4's sim has run some steps, it must already have its input
                // pins (waiting on the steps, not on a fixed delay: a loaded machine compiled late and failed once)
                var waitSteps = Stopwatch.StartNew();
                while (!(p.rootSimChip.Program != null && p.rootSimChip.Program.StepsRun >= 20) && waitSteps.ElapsedMilliseconds < 5000) Thread.Sleep(1);
                int floatingInputs = p.rootSimChip.InputPins.Count(pin => (pin.State >> 16) != 0);
                if (floatingInputs > 0) { DLS.Simulation.SimProgram pr = p.rootSimChip.Program; return $"{floatingInputs} of Add4's inputs float in its sim until the next frame (it ran with the previous chip's input list) [program {(pr == null ? "null" : "ok")}, steps run {pr?.StepsRun}, input list {(pr?.InputPinsArray == null ? "none" : ((System.Array)pr.InputPinsArray).Length + " pins, found " + pr.InputSimPins.Count(x => x != null))}]"; }
                string firstVisit = Flicker(p);
                if (firstVisit != null) return "first visit of Add4: " + firstVisit;
                p.LoadDevChipOrCreateNewIfDoesntExist("CPU"); // back
                string comingBack = Flicker(p);

                uint ram9Back = Read("RAM", 9), regABack = Read("Registre A", 0);
                if (ram9Back != ram9) return $"RAM[9] = {ram9Back} after switching chip and back, the running value was {ram9} (the sim restarted from the saved memory)";
                if (regABack != regA) return $"Registre A = {regABack} after switching chip and back, the running value was {regA}";
                if (comingBack != null) return "back on the CPU: " + comingBack;
                return p.simThreadExceptions == 0 ? null : $"{p.simThreadExceptions} exception(s) in the sim thread (see the log)";
            }
            finally { Close(p); }
        }

        // every frame after a switch: pins drawn floating (tristate flags reach the display) that are driven once settled
        static string Flicker(Project p)
        {
            var flaggedEarly = new HashSet<PinInstance>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 800)
            {
                p.TickMainThreadForTests();
                Thread.Sleep(2);
                foreach (PinInstance pin in AllPins(p)) if ((pin.State >> 16) != 0) flaggedEarly.Add(pin);
            }
            Pump(p, () => false, 300);
            var floatingSettled = new HashSet<PinInstance>(AllPins(p).Where(pin => (pin.State >> 16) != 0));
            var wrong = flaggedEarly.Where(pin => !floatingSettled.Contains(pin)).ToList();
            return wrong.Count == 0 ? null : $"{wrong.Count} driven pins were drawn floating (flicker) before the new sim had settled: " + string.Join(", ", wrong.Take(8).Select(pin => $"{(pin.parent is SubChipInstance sc ? (string.IsNullOrEmpty(sc.Label) ? sc.Description.Name : sc.Label) : "pin")}.{pin.Name}"));
        }

        // 2026-10-02: the display sync read the arriving chip's sim before it existed (a never-visited chip is built
        // while the sim thread runs): a NullReferenceException killed the sim thread for good. A frame just before each
        // switch makes the sim thread sync exactly in that window.
        static string LiveManySwitches(string projectDir)
        {
            Project p = Open(projectDir, "Add4");
            try
            {
                Pump(p, () => false, 100);
                string[] names = BenchProject.LoadLibrary(projectDir, out ChipDescription[] chips) != null ? chips.Select(c => c.Name).ToArray() : Array.Empty<string>();
                foreach (string name in names)
                {
                    p.TickMainThreadForTests();
                    p.LoadDevChipOrCreateNewIfDoesntExist(name);
                }
                p.LoadDevChipOrCreateNewIfDoesntExist("Registre8");
                if (!Pump(p, () => p.rootSimChip.Program != null && p.rootSimChip.Program.StepsRun > 50, 5000)) return "the simulation stopped after switching through the chips (sim thread dead or stuck)";
                return p.simThreadExceptions == 0 ? null : $"{p.simThreadExceptions} exception(s) in the sim thread while switching chips (see the log)";
            }
            finally { Close(p); }
        }

        // User, 2026-10-03: "I edit a chip (remove an input, add another), go to the chips using it: I still see it as it
        // was, and unwired". At the save of the edited chip, its users (open or not) must show its new interface: a
        // removed pin's wires go, a pin re-created with the SAME name keeps them, a new pin appears; no red star.
        static string LiveInterfaceChange(string projectDir)
        {
            const string tmpName = "_BenchInterface";
            string tmpDir = SavePaths.GetProjectPath(tmpName);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            CopyDir(projectDir, tmpDir);
            Project p = Open(tmpDir, "Add4", tmpName);
            try
            {
                SubChipInstance[] FullAs() => p.ViewedChip.Elements.OfType<SubChipInstance>().Where(s => s.Description.Name == "FullA").ToArray();
                int WiresTo(string pinName) => p.ViewedChip.Wires.Count(w => w.TargetPin.parent is SubChipInstance s && s.Description.Name == "FullA" && w.TargetPin.Name == pinName);
                int a1Before = WiresTo("A1"), a0Before = WiresTo("A0"), cinBefore = WiresTo("C_in");
                if (a1Before == 0 || cinBefore == 0) return "test setup: Add4 should wire FullA's A1 and C_in";

                // in FullA: remove C_in, remove A0 and re-create it with the same name, add NEW (nothing saved yet)
                p.LoadDevChipOrCreateNewIfDoesntExist("FullA");
                DevChipInstance fa = p.ViewedChip;
                DevPinInstance Pin(string n) => fa.Elements.OfType<DevPinInstance>().First(d => d.IsInputPin && d.Name == n);
                fa.DeleteDevPin(Pin("C_in"));
                DevPinInstance a0 = Pin("A0");
                var a0Desc = new PinDescription("A0", IDGenerator.GenerateNewElementID(fa), a0.Position, PinBitCount.Bit1, PinColour.Red, PinValueDisplayMode.Off);
                fa.DeleteDevPin(a0);
                fa.AddNewDevPin(new DevPinInstance(a0Desc, true), false);
                fa.AddNewDevPin(new DevPinInstance(new PinDescription("NEW", IDGenerator.GenerateNewElementID(fa), new UnityEngine.Vector2(-8, -3), PinBitCount.Bit1, PinColour.Red, PinValueDisplayMode.Off), true), false);
                p.SaveFromDescription(DescriptionCreator.CreateChipDescription(fa));

                p.LoadDevChipOrCreateNewIfDoesntExist("Add4");
                foreach (SubChipInstance s in FullAs())
                {
                    string names = string.Join(",", s.InputPins.Select(x => x.Name).OrderBy(x => x));
                    if (names != "A0,A1,NEW") return $"Add4 (open) shows FullA with inputs {names} after the save, expected A0,A1,NEW";
                }
                if (WiresTo("C_in") != 0) return "a wire to the removed C_in is still there";
                if (WiresTo("A1") != a1Before) return $"wires to A1: {WiresTo("A1")}, were {a1Before}";
                if (WiresTo("A0") != a0Before) return $"wires to A0 (re-created with the same name): {WiresTo("A0")}, were {a0Before}";
                if (p.IsChipDirty("Add4")) return "Add4 got a red star from FullA's save";
                Close(p);

                // reopened from the files: the same
                p = Open(tmpDir, "Add4", tmpName);
                foreach (SubChipInstance s in FullAs())
                {
                    string names = string.Join(",", s.InputPins.Select(x => x.Name).OrderBy(x => x));
                    if (names != "A0,A1,NEW") return $"Add4 (reopened) shows FullA with inputs {names}";
                }
                if (WiresTo("A0") != a0Before || WiresTo("A1") != a1Before || WiresTo("C_in") != 0) return "Add4 (reopened): wires not as expected";
                if (p.ActiveChipHasUnsavedChanges()) return "Add4 (reopened) shows a red star";
                return null;
            }
            finally
            {
                Close(p);
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            }
        }

        static string LiveInterfaceDirtyParent(string projectDir)
        {
            const string tmpName = "_BenchInterface2";
            string tmpDir = SavePaths.GetProjectPath(tmpName);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            CopyDir(projectDir, tmpDir);
            Project p = Open(tmpDir, "Add4", tmpName);
            try
            {
                // Add4's own unsaved edit: its XOR deleted
                SubChipInstance xor = p.ViewedChip.Elements.OfType<SubChipInstance>().First(s => s.Description.Name == "XOR");
                p.ViewedChip.DeleteSubChip(xor);
                if (!p.IsChipDirty("Add4")) return "test setup: Add4 should be dirty";

                p.LoadDevChipOrCreateNewIfDoesntExist("FullA");
                DevChipInstance fa = p.ViewedChip;
                fa.DeleteDevPin(fa.Elements.OfType<DevPinInstance>().First(d => d.IsInputPin && d.Name == "C_in"));
                p.SaveFromDescription(DescriptionCreator.CreateChipDescription(fa));

                p.LoadDevChipOrCreateNewIfDoesntExist("Add4");
                if (p.ViewedChip.Elements.OfType<SubChipInstance>().Any(s => s.Description.Name == "XOR")) return "Add4's own unsaved edit (XOR deleted) was lost";
                if (!p.IsChipDirty("Add4")) return "Add4 lost its red star (its own edit is still unsaved)";
                if (p.ViewedChip.Elements.OfType<SubChipInstance>().Where(s => s.Description.Name == "FullA").Any(s => s.InputPins.Any(x => x.Name == "C_in"))) return "Add4 still shows FullA's removed C_in";
                // on disk: the interface change, but NOT Add4's own unsaved edit
                ChipDescription onDisk = Serializer.DeserializeChipDescription(File.ReadAllText(Path.Combine(tmpDir, "Chips", "Add4.json")));
                if (!onDisk.SubChips.Any(s => s.Name == "XOR")) return "Add4's unsaved edit was written to its file";
                ChipDescription faSaved = p.chipLibrary.GetChipDescription("FullA");
                var faIDs = new HashSet<int>(onDisk.SubChips.Where(s => s.Name == "FullA").Select(s => s.ID));
                var faPins = new HashSet<int>(faSaved.InputPins.Concat(faSaved.OutputPins).Select(x => x.ID));
                if (onDisk.Wires.Any(w => (faIDs.Contains(w.TargetPinAddress.PinOwnerID) && !faPins.Contains(w.TargetPinAddress.PinID)) || (faIDs.Contains(w.SourcePinAddress.PinOwnerID) && !faPins.Contains(w.SourcePinAddress.PinID))))
                    return "Add4's file still has a wire to a pin FullA no longer has";
                return null;
            }
            finally
            {
                Close(p);
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            }
        }

        // user, 2026-10-04: "in CPU_2, inside MAR, toggling Reset_all: its wire never lights" (every wire from an input pin)
        static string LiveInputWires(string projectDir)
        {
            Project p = Open(projectDir, "CPU");
            try
            {
                if (!Pump(p, () => p.rootSimChip.Program != null && p.rootSimChip.Program.StepsRun > 200)) return "CPU sim never ran";
                p.LoadDevChipOrCreateNewIfDoesntExist("Registre8");
                if (!Pump(p, () => p.rootSimChip.Program != null && p.rootSimChip.Program.SettledAfterBuild)) return "Registre8 sim never settled";
                var inputs = p.ViewedChip.Elements.OfType<DevPinInstance>().Where(d => d.IsInputPin && p.ViewedChip.Wires.Any(w => w.SourcePin == d.Pin)).ToList();
                if (inputs.Count == 0) return "no wired input pin in Registre8";
                foreach (DevPinInstance d in inputs) d.Pin.PlayerInputState = 0;
                Pump(p, () => inputs.All(d => (d.Pin.State & 0xFFFF) == 0), 2000);
                foreach (DevPinInstance d in inputs)
                {
                    foreach (uint v in new uint[] { 1, 0 })
                    {
                        d.Pin.PlayerInputState = v;
                        bool ok = Pump(p, () => (d.Pin.State & 1) == v, 3000);
                        WireInstance w = p.ViewedChip.Wires.First(x => x.SourcePin == d.Pin);
                        if (!ok || (w.SourcePin.State & 1) != v) return $"input {d.Name} set to {v}: its displayed state stays {d.Pin.State & 1}, so its wire is drawn {(v == 1 ? "off" : "on")}";
                    }
                }
                return null;
            }
            finally { Close(p); }
        }

        static string LiveRunFast(string projectDir)
        {
            const string tmpName = "_BenchRunFast";
            string tmpDir = SavePaths.GetProjectPath(tmpName);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            CopyDir(projectDir, tmpDir);
            Project p = Open(tmpDir, "CPU", tmpName);
            try
            {
                DevPinInstance In(string n) => p.ViewedChip.Elements.OfType<DevPinInstance>().First(d => d.IsInputPin && d.Name == n);
                foreach (DevPinInstance d in p.ViewedChip.Elements.OfType<DevPinInstance>().Where(d => d.IsInputPin)) d.Pin.PlayerInputState = 0;
                if (!Pump(p, () => p.rootSimChip.Program != null && p.rootSimChip.Program.StepsRun > 2000 && p.rootSimChip.InputPins.All(pin => (pin.State & 0xFFFF) == 0), 5000)) return "test setup: inputs never reached 0";
                MemoryRules rules = MemoryLayout.ParseRules(MemoryLayoutCases.CpuRulesJson, out _);
                var pol = new Dictionary<string, int[]>();
                ChipDescription cpu = p.chipLibrary.GetChipDescription("CPU");
                foreach (string t in MemoryLayout.BankTypes(cpu, rules, p.chipLibrary)) { string e = MemoryLayout.Verify(t, rules, p.chipLibrary, pol); if (e != null) return "layout: " + e; }
                uint Read(string bank, int w) { uint v = 0; p.RunWithSimulationPaused(() => v = MemoryLayout.Banks(p.rootSimChip, cpu, rules, pol, p.chipLibrary, out _).First(b => b.Name == bank).Read(w)); return v; }
                uint ram9 = (Read("RAM", 9) ^ 0x5A) & 0xFF, regA = (Read("Registre A", 0) ^ 0x33) & 0xFF;
                p.RunWithSimulationPaused(() =>
                {
                    var bk = MemoryLayout.Banks(p.rootSimChip, cpu, rules, pol, p.chipLibrary, out _);
                    bk.First(b => b.Name == "RAM").Write(9, ram9, p.rootSimChip.Program);
                    bk.First(b => b.Name == "Registre A").Write(0, regA, p.rootSimChip.Program);
                });
                Pump(p, () => false, 100);

                // RUN FAST: the models take the state over
                p.RunFastNowForTests();
                if (!p.FastModeActive) return "fast mode did not start: " + p.FastModeStatus;
                if (!Pump(p, () => p.rootSimChip.Program != null && p.rootSimChip.Program.StepsRun > 500, 5000)) return "the fast tree never ran";
                if (p.rootSimChip.Program.GateCount > 2000) return $"the fast tree has {p.rootSimChip.Program.GateCount} gates: modules were not replaced";
                SeqModel Model(string label) => (SeqModel)p.rootSimChip.SubChips.First(c => c.ID == cpu.SubChips.First(s => s.Label == label || (s.Name == label && string.IsNullOrEmpty(s.Label))).ID).Model;
                if (Model("RAM256").State[9] != ram9) return $"the fast RAM holds {Model("RAM256").State[9]} at 9, the gates held {ram9}";
                if (Model("Registre A").State[0] != regA) return $"the fast Registre A holds {Model("Registre A").State[0]}, the gates held {regA}";

                // in fast mode, load Registre B through the inputs and the running clock, as a program would
                In("ENTREE").Pin.PlayerInputState = 0x3C; In("OE_entree").Pin.PlayerInputState = 1; In("Load_B").Pin.PlayerInputState = 1;
                if (!Pump(p, () => Model("Registre B").State[0] == 0x3C, 8000)) return $"Registre B never loaded 3C in fast mode (holds {Model("Registre B").State[0]:X})";
                In("Load_B").Pin.PlayerInputState = 0; In("OE_entree").Pin.PlayerInputState = 0;
                Pump(p, () => false, 300);

                // an edit of the chip leaves fast mode, and the gates get the state back
                var extra = new DevPinInstance(new PinDescription("EXTRA", IDGenerator.GenerateNewElementID(p.ViewedChip), new UnityEngine.Vector2(-20, -20), PinBitCount.Bit1, PinColour.Red, PinValueDisplayMode.Off), true);
                p.ViewedChip.AddNewDevPin(extra, false);
                if (p.FastModeActive) return "an edit of the chip did not leave fast mode";
                Pump(p, () => false, 200);
                if (Read("Registre B", 0) != 0x3C) return $"after leaving fast mode the gates' Registre B = {Read("Registre B", 0):X}, it was 3C in fast mode";
                if (Read("RAM", 9) != ram9 || Read("Registre A", 0) != regA) return "the state taken over at RUN FAST was lost on the way back";
                if (!File.Exists(Path.Combine(tmpDir, "FastModels.json"))) return "the fast-mode cache was not saved in the project";

                // the second time: from the cache; a chip switch leaves it too
                p.RunFastNowForTests();
                if (!p.FastModeActive) return "fast mode did not start the second time";
                p.LoadDevChipOrCreateNewIfDoesntExist("Add4");
                return p.FastModeActive ? "switching chip did not leave fast mode" : null;
            }
            finally
            {
                Close(p);
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            }
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (string d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        static IEnumerable<PinInstance> AllPins(Project p)
        {
            foreach (IMoveable e in p.ViewedChip.Elements)
            {
                if (e is SubChipInstance s) { foreach (PinInstance pin in s.InputPins) yield return pin; foreach (PinInstance pin in s.OutputPins) yield return pin; }
                else if (e is DevPinInstance d) yield return d.Pin;
            }
        }

        static string LiveCreateChip(string projectDir)
        {
            const string tmpName = "_BenchCreateChip";
            string tmpDir = SavePaths.GetProjectPath(tmpName);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            Directory.CreateDirectory(Path.Combine(tmpDir, "Chips"));
            Project p = Open(projectDir, "CPU", tmpName);
            try
            {
                SubChipInstance[] picked = p.ViewedChip.Elements.OfType<SubChipInstance>().Where(s => s.Description.Name is "RAM256" or "Tampon8").ToArray();
                if (picked.Length != 2) return "RAM256 / Tampon8 not found in the CPU";
                foreach (SubChipInstance s in picked) p.controller.Select(s, true);
                DLS.Graphics.CreateChipPopup.OpenForTests();
                p.controller.SelectedElements.Clear(); // what the click on the menu entry does in the app
                string err = DLS.Graphics.CreateChipPopup.ConfirmForTests("MEMOIRE TEST");
                if (err != null) return "create failed: " + err;
                if (DLS.Graphics.UIDrawer.ActiveMenu != DLS.Graphics.UIDrawer.MenuType.None) return "the popup is still open";
                if (!p.chipLibrary.HasChip("MEMOIRE TEST")) return "new chip not in the library";
                if (!File.Exists(Path.Combine(tmpDir, "Chips", "MEMOIRE TEST.json"))) return "new chip not saved";
                if (!p.description.AllCustomChipNames.Contains("MEMOIRE TEST")) return "new chip not registered in the project (it would vanish on reload)";
                // behaviour change 2026-10-01 (user): stay on the same view to test it (it opened the new chip before)
                if (p.ViewedChip.ChipName != "CPU") return $"the view changed to {p.ViewedChip.ChipName}, it must stay on the CPU";
                var names = p.ViewedChip.Elements.OfType<SubChipInstance>().Select(s => s.Description.Name).ToList();
                if (!names.Contains("MEMOIRE TEST") || names.Contains("RAM256") || names.Contains("Tampon8")) return "the selection was not replaced by the new chip: " + string.Join(", ", names);
                Pump(p, () => false, 100); // the sim thread applies the modifications
                p.ViewedChip.UndoController.TryUndo();
                names = p.ViewedChip.Elements.OfType<SubChipInstance>().Select(s => s.Description.Name).ToList();
                if (names.Contains("MEMOIRE TEST") || !names.Contains("RAM256") || !names.Contains("Tampon8")) return "Ctrl+Z did not restore the selection: " + string.Join(", ", names);
                // ... and deletes the created chip everywhere
                if (p.chipLibrary.HasChip("MEMOIRE TEST")) return "Ctrl+Z left the created chip in the library";
                if (File.Exists(Path.Combine(tmpDir, "Chips", "MEMOIRE TEST.json"))) return "Ctrl+Z left the created chip's file";
                if (p.description.AllCustomChipNames.Contains("MEMOIRE TEST")) return "Ctrl+Z left the created chip in the project list";
                if (p.description.StarredList.Any(s => s.Name == "MEMOIRE TEST")) return "Ctrl+Z left the created chip in the bottom bar";
                if (p.ViewedChip.ChipName != "CPU") return "Ctrl+Z changed the view";
                // Ctrl+Y: created and replaced again
                p.ViewedChip.UndoController.TryRedo();
                names = p.ViewedChip.Elements.OfType<SubChipInstance>().Select(s => s.Description.Name).ToList();
                if (!p.chipLibrary.HasChip("MEMOIRE TEST") || !File.Exists(Path.Combine(tmpDir, "Chips", "MEMOIRE TEST.json"))) return "Ctrl+Y did not create the chip again";
                if (!names.Contains("MEMOIRE TEST") || names.Contains("RAM256")) return "Ctrl+Y did not replace the selection again: " + string.Join(", ", names);
                Pump(p, () => false, 100);
                p.ViewedChip.UndoController.TryUndo(); // and back, for the empty-selection check below

                // nothing selected: the error is SHOWN (it used to be closed at once)
                DLS.Graphics.CreateChipPopup.OpenForTests();
                if (DLS.Graphics.CreateChipPopup.ConfirmForTests("EMPTY") == null) return "an empty selection was accepted";
                if (DLS.Graphics.UIDrawer.ActiveMenu != DLS.Graphics.UIDrawer.MenuType.Info) return "the error message is not shown";
                return null;
            }
            finally
            {
                DLS.Graphics.UIDrawer.SetActiveMenu(DLS.Graphics.UIDrawer.MenuType.None);
                Close(p);
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            }
        }

        static string LiveRegister(string projectDir)
        {
            Project p = Open(projectDir, "Add4");
            try
            {
                p.LoadDevChipOrCreateNewIfDoesntExist("Registre8");
                Input(p, "IN", 0x5A); Input(p, "LOAD", 1); Input(p, "OE", 1); Input(p, "Reset", 0); Input(p, "Clock", 0);
                Pump(p, () => false, 60);
                Input(p, "Clock", 1); Pump(p, () => false, 60);
                Input(p, "Clock", 0);
                if (!Pump(p, () => Output(p, "OUT") == 0x5A)) return $"OUT = {Output(p, "OUT")} after a clock cycle with IN=0x5A (expected 90)";
                Input(p, "IN", 0x33); Input(p, "LOAD", 0); Pump(p, () => false, 60);
                Input(p, "Clock", 1); Pump(p, () => false, 60); Input(p, "Clock", 0); Pump(p, () => false, 60);
                if (Output(p, "OUT") != 0x5A) return $"OUT = {Output(p, "OUT")} after a clock with LOAD=0 (expected to hold 90)";
                return null;
            }
            finally { Close(p); }
        }
    }
}
