using System;
using System.IO;
using System.Linq;
using System.Text;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEngine;

namespace DLS.Game
{
	// Speed of the REAL app (rendering, input, the sim thread, everything), measured inside the built player:
	//   DigitalLogicSim.exe -ratetest <project copy> <chip> [-ratetest-gates]
	// Opens the project like the main menu does, runs the chip in RUN FAST (unless -ratetest-gates), logs every
	// second the sim rate and the frame rate, then a profile of the sim thread, to <save folder>/ratetest.txt, and
	// quits. Run it on a COPY of a project (runRateTest.bat copies a project to _RateTest_<name> and deletes it afterwards).
	public static class PlayerRateTest
	{
		public static bool Active { get; private set; }
		static string project, chip;
		static bool gates;
		static int phase;
		static float t0, lastLog;
		static int frames, gc0;
		static float maxFrame;
		static readonly StringBuilder report = new();

		static long allocProject, allocWorld, allocUI;
		static long heap0;
		public static void NoteAllocations(long project, long world, long ui) { allocProject += project; allocWorld += world; allocUI += ui; }

		static string lcdAt3;
		// what the drawer shows: the LCD of the simulation displays are drawn from
		static string LcdHash(Project p) => LcdHash(p.DisplaySimChip(p.ViewedChip)) + " / gates " + LcdHash(p.ViewedChip.SimChip);
		static string LcdHash(SimChip root)
		{
			foreach (SimChip c in root.SubChips)
				if (c != null && c.ChipType == DLS.Description.ChipType.LcdSt7920)
				{
					uint h = 2166136261;
					foreach (uint w in c.InternalState) h = (h ^ w) * 16777619;
					return h.ToString("X8");
				}
			return null;
		}

		public static void CheckArgs()
		{
			string[] args = Environment.GetCommandLineArgs();
			int i = Array.FindIndex(args, a => a.Equals("-ratetest", StringComparison.OrdinalIgnoreCase));
			if (i < 0 || i + 2 >= args.Length) return;
			Active = true;
			project = args[i + 1];
			chip = args[i + 2];
			gates = args.Any(a => a.Equals("-ratetest-gates", StringComparison.OrdinalIgnoreCase));
			Project.AutoFastDisabled = gates; // measuring the gates: no automatic RUN FAST
		}

		// called at the end of Main.Update, after the normal frame
		public static void Update()
		{
			if (!Active) return;
			frames++;
			float now = Time.realtimeSinceStartup;
			Project p = Project.ActiveProject;
			if (phase == 0)
			{
				report.AppendLine($"rate test, build {Main.BuildInfoString}, {DateTime.Now}, Burst kernel {(SimKernel.CompiledByBurst() == 1 ? "on" : "OFF")}, vsync {QualitySettings.vSyncCount}");
				Main.CreateOrLoadProject(project, chip);
				phase = 1;
				t0 = now;
			}
			else if (phase == 1 && now - t0 > 1)
			{
				if (!gates) p.StartFastMode();
				phase = 2;
				t0 = lastLog = now;
				frames = 0; gc0 = GC.CollectionCount(0); maxFrame = 0;
			}
			else if (phase == 2)
			{
				maxFrame = Math.Max(maxFrame, Time.unscaledDeltaTime);
				if (now - lastLog >= 1)
				{
					int period = p.stepsPerClockTransition;
					report.AppendLine($"t={now - t0:0}s {p.simAvgTicksPerSec:0} steps/s = {p.simAvgTicksPerSec / (2.0 * period) / 1000:0.0} kHz, {frames / (now - lastLog):0} fps (longest frame {maxFrame * 1000:0} ms), {GC.CollectionCount(0) - gc0} GC, allocated KB/s: project {allocProject / 1024} world {allocWorld / 1024} UI {allocUI / 1024} heap growth {(UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - heap0) / 1024} (GC {UnityEngine.Scripting.GarbageCollector.GCMode}), {(p.FastModeActive ? "FAST" : "gates")} ({p.FastModeStatus})");
					frames = 0; gc0 = GC.CollectionCount(0); maxFrame = 0;
					allocProject = allocWorld = allocUI = 0;
					// seconds 2 to 4: the collector off, so the heap growth is what was allocated, and the rate without GC pauses
					UnityEngine.Scripting.GarbageCollector.GCMode = now - t0 > 1.5f && now - t0 < 3.5f ? UnityEngine.Scripting.GarbageCollector.Mode.Disabled : UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
					heap0 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
					lastLog = now;
				}
				// the user's game waits for Space: press it for half a second, then watch the LCD the user sees change
				if (now - t0 > 2.5f && now - t0 < 3f) SimKeyboardHelper.InjectedKey = ' ';
				else SimKeyboardHelper.InjectedKey = default;
				if (lcdAt3 == null && now - t0 > 3.5f) lcdAt3 = LcdHash(p);
				if (now - t0 > 6)
				{
					string lcdNow = LcdHash(p);
					report.AppendLine($"LCD (drawn / gates tree) at t=3.5s {lcdAt3}, at t=6s {lcdNow}: the drawn one {(lcdNow.Split('/')[0] != lcdAt3.Split('/')[0] ? "CHANGED (the game runs, the screen follows)" : "did NOT change")}");
					Simulator.ProfInBatch = Simulator.ProfLoopSteps = Simulator.ProfStep = Simulator.ProfIdle = Simulator.ProfSteps = Simulator.ProfFirst = Simulator.ProfBatches = 0;
					Simulator.ProfGates = Simulator.ProfKernelGates = Simulator.ProfNoise = Simulator.ProfKernel = Simulator.ProfKernelCalls = 0;
					Array.Clear(Simulator.ProfBailTypes, 0, 256);
					Simulator.Profile = true;
					phase = 3;
					t0 = now;
				}
			}
			else if (phase == 3 && now - t0 > 3)
			{
				Simulator.Profile = false;
				double wallNs = (now - t0) * 1e9;
				double halfs = Simulator.ProfLoopSteps / (double)p.stepsPerClockTransition;
				double ns(long ticks) => ticks * 1e9 / System.Diagnostics.Stopwatch.Frequency / halfs;
				report.AppendLine($"profile per half-period: wall {wallNs / halfs:0} ns, inside RunSimulationSteps {ns(Simulator.ProfInBatch):0} ns (Step {ns(Simulator.ProfStep):0}, IdleSteps {ns(Simulator.ProfIdle):0}, first full steps {ns(Simulator.ProfFirst):0} x{Simulator.ProfBatches / halfs:0.00}), real steps {Simulator.ProfSteps / halfs:0.00}, gates run {Simulator.ProfGates / halfs:0.0} (kernel {Simulator.ProfKernelGates / halfs:0.0}), batches {Simulator.ProfBatches}, handed back: "
					+ string.Join(", ", Enumerable.Range(0, 256).Where(x => Simulator.ProfBailTypes[x] > 0).Select(x => (x == 255 ? "Merge" : ((DLS.Description.ChipType)x).ToString()) + " " + (Simulator.ProfBailTypes[x] / halfs).ToString("0.00"))));
				try { File.WriteAllText(Path.Combine(SavePaths.AllData, "ratetest.txt"), report.ToString()); } catch (Exception) { }
				phase = 4;
				Application.Quit();
			}
		}
	}
}
