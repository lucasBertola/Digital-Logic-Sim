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
