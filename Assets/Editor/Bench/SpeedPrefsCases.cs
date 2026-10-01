using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DLS.Description;
using DLS.Game;
using DLS.Graphics;

namespace DLS.Bench
{
    // Preferences: "Speed: Max" (no target, as fast as possible) and the frequency / speed display units.
    public static class SpeedPrefsCases
    {
        public static List<(string name, Func<string> run)> All() => new()
        {
            ("prefs: frequency shown in Hz / kHz / MHz / GHz, the unit changes at each thousand", Units),
            ("prefs: steps per second shown with thousands separated", Count),
            ("prefs: Speed Max = no target; Limited = the typed target (min 1)", Target),
            ("prefs: Speed Max is saved with the project, an older project loads as Limited", SavedWithProject),
        };

        public static (string name, Func<string> run) Live(string projectDir) =>
            ("live project: Speed Max ignores the target (target 1 step/s, the sim thread still runs at full speed)", () => MaxIsUnpaced(projectDir));

        static string Units()
        {
            var cases = new (double hz, string expect)[]
            {
                (0, "0 Hz"), (5.25, "5.25 Hz"), (999, "999 Hz"), (1000, "1 kHz"), (1500, "1.5 kHz"),
                (436022.857, "436.02 kHz"), (999999, "1 MHz"), (1000000, "1 MHz"), (2360000, "2.36 MHz"), (30e6, "30 MHz"), (1.2e9, "1.2 GHz"),
            };
            foreach (var c in cases)
            {
                string got = PreferencesMenu.FormatHz(c.hz);
                if (got != c.expect) return $"{c.hz} Hz shown as '{got}', expected '{c.expect}'";
            }
            return null;
        }

        static string Count()
        {
            var cases = new (double v, string expect)[] { (0, "0"), (999, "999"), (1000, "1 000"), (61043200, "61 043 200"), (7000000.4, "7 000 000") };
            foreach (var c in cases) if (PreferencesMenu.FormatCount(c.v) != c.expect) return $"{c.v} shown as '{PreferencesMenu.FormatCount(c.v)}', expected '{c.expect}'";
            return null;
        }

        static string Target()
        {
            var d = new ProjectDescription { Prefs_SimTargetStepsPerSecond = 7000000 };
            if (Project.TargetFor(d) != 7000000) return "limited target not used";
            d.Prefs_SimTargetStepsPerSecond = 0;
            if (Project.TargetFor(d) != 1) return "limited target below 1 not clamped";
            d.Prefs_SimMaxSpeed = true;
            return Project.TargetFor(d) == int.MaxValue ? null : "Max speed still has a target";
        }

        static string SavedWithProject()
        {
            var d = new ProjectDescription { ProjectName = "x", Prefs_SimMaxSpeed = true, Prefs_SimTargetStepsPerSecond = 1234, AllCustomChipNames = Array.Empty<string>(), StarredList = new(), ChipCollections = new() };
            ProjectDescription back = Serializer.DeserializeProjectDescription(Serializer.SerializeProjectDescription(d));
            if (!back.Prefs_SimMaxSpeed || back.Prefs_SimTargetStepsPerSecond != 1234) return "Max speed (or the target kept for Limited) lost by the serializer";
            string old = Serializer.SerializeProjectDescription(d).Replace("\"Prefs_SimMaxSpeed\":true,", "").Replace("\"Prefs_SimMaxSpeed\": true,", "");
            if (old.Contains("Prefs_SimMaxSpeed")) return "test setup: could not remove the field";
            return Serializer.DeserializeProjectDescription(old).Prefs_SimMaxSpeed ? "an older project (no field) loads as Max" : null;
        }

        static string MaxIsUnpaced(string projectDir)
        {
            ChipLibrary lib = BenchProject.LoadLibrary(projectDir, out _);
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(Path.Combine(projectDir, "ProjectDescription.json")));
            pd.ProjectName = "_BenchMaxSpeed";
            pd.Prefs_SimPaused = false;
            pd.Prefs_SimTargetStepsPerSecond = 1; // paced at 1 step/s if the target were used
            pd.Prefs_SimMaxSpeed = true;
            var p = new Project(pd, lib) { audioState = new AudioState() };
            try
            {
                p.LoadDevChipOrCreateNewIfDoesntExist("Registre8");
                p.StartSimulation();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double best = 0;
                while (sw.ElapsedMilliseconds < 1500) { p.TickMainThreadForTests(); Thread.Sleep(10); best = Math.Max(best, p.simAvgTicksPerSec); }
                return best > 100000 ? null : $"only {best:0} steps/s at Max with a target of 1";
            }
            finally { p.NotifyExit(); Thread.Sleep(30); }
        }
    }
}
