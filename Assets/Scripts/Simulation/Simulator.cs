using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using DLS.Description;
using DLS.Game;
using Random = System.Random;

namespace DLS.Simulation
{
	public static class Simulator
	{
		// ---- Per-run state is THREAD-STATIC ----
		// The live simulation runs on its own thread; the QA harness (CircuitTester, truth tables) runs on the
		// main thread while that thread is paused; and the regression bench (Assets/Editor/Bench) runs many
		// isolated simulations IN PARALLEL. Each of those must have its own frame counter, clock override,
		// RNG and ordering flag, otherwise a step on one thread corrupts another. Fields that are set on one
		// thread and consumed on another (the modification queue, the audio state) stay shared.
		public static readonly Random rng = new();
		static readonly Stopwatch stopwatch = Stopwatch.StartNew();
		[ThreadStatic] public static int stepsPerClockTransition;
		[ThreadStatic] public static int simulationFrame;

		// QA harness override (CircuitTester): -1 = normal time-based clock, 0/1 = every CLOCK chip is held
		// at that level. Lets a test drive clock edges deterministically instead of waiting for real time.
		// All clock chips share the same phase in this simulator, so one global level is faithful.
		// (Stored +1 so that a fresh thread's default of 0 means "normal".)
		public static int forcedClockState { get => forcedClockStatePlusOne - 1; set => forcedClockStatePlusOne = value + 1; }
		[ThreadStatic] static int forcedClockStatePlusOne;
		[ThreadStatic] static uint pcg_rngState;

		// Seeded RNG for tests: when set on the current thread, the per-step PCG reseed comes from it instead
		// of the shared time-seeded Random, so a run is reproducible (and threads don't share a Random).
		[ThreadStatic] static Random testRng;

		// Set whenever the structure of the simulated tree changed (or a different tree is stepped): the
		// compiled program is rebuilt at the top of the next step.
		[ThreadStatic] public static bool needsOrderPass;

		// Every n frames the cut points inside feedback loops are re-drawn (to randomize outcome of race conditions)
		[ThreadStatic] public static bool canDynamicReorderThisFrame;

		[ThreadStatic] static SimChip prevRootSimChip;

		// Test support: fresh, reproducible per-thread run state.
		public static void ResetForTests(int seed)
		{
			simulationFrame = 0;
			forcedClockState = -1;
			needsOrderPass = true;
			prevRootSimChip = null;
			testRng = new Random(seed);
		}

		public static void ClearTestSeed() => testRng = null;
		static double elapsedSecondsOld;
		static double deltaTime;
		static SimAudio audioState;

		// Modifications to the sim are made from the main thread, but only applied on the sim thread to avoid conflicts
		static readonly ConcurrentQueue<SimModifyCommand> modificationQueue = new();

		// ---- Simulation outline ----
		// The SimChip tree is compiled (SimProgram) into a flat list of gates over a single array of pin-state
		// slots, scheduled in topological order; feedback loops are cut at a random gate, which then reads the
		// previous step's values. A step is: copy the player inputs into their slots, then run the gates in
		// order. See SimProgram.cs. The program is rebuilt whenever the tree changes (needsOrderPass).

		public static void RunSimulationStep(SimChip rootSimChip, DevPinInstance[] inputPins, SimAudio audioState)
		{
			Simulator.audioState = audioState;
			audioState.InitFrame();

			if (rootSimChip != prevRootSimChip)
			{
				needsOrderPass = true;
				prevRootSimChip = rootSimChip;
			}

			pcg_rngState = (uint)(testRng ?? rng).Next();
			canDynamicReorderThisFrame = simulationFrame % 100 == 0;
			simulationFrame++;

			if (needsOrderPass || rootSimChip.Program == null)
			{
				rootSimChip.Program = SimProgram.Compile(rootSimChip, rootSimChip.Program);
				needsOrderPass = false;
			}
			else if (canDynamicReorderThisFrame)
			{
				rootSimChip.Program.Reschedule();
			}

			// Step 1) Get player-controlled input states and copy values to the sim
			foreach (DevPinInstance input in inputPins)
			{
				try
				{
					SimPin simPin = rootSimChip.GetSimPinFromAddress(input.Pin.Address);
					simPin.State = input.Pin.PlayerInputState;
				}
				catch (Exception)
				{
					// Possible for sim to be temporarily out of sync since running on separate threads, so just ignore failure to find pin.
				}
			}

			// Step 2) Run every gate
			rootSimChip.Program.Step(audioState);

			UpdateAudioState();
		}

		public static void UpdateInPausedState()
		{
			if (audioState != null)
			{
				audioState.InitFrame();
				UpdateAudioState();
			}
		}

		static void UpdateAudioState()
		{
			double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
			if (simulationFrame <= 1) deltaTime = 0;
			else deltaTime = elapsedSeconds - elapsedSecondsOld;
			elapsedSecondsOld = stopwatch.Elapsed.TotalSeconds;
			audioState.NotifyAllNotesRegistered(deltaTime);
		}

		public static void UpdateKeyboardInputFromMainThread()
		{
			SimKeyboardHelper.RefreshInputState();
		}

		// ---- Random draws: ALL through the seeded per-thread stream, so a seeded run is exactly reproducible ----

		// 16 random bits (one PCG step), for the value carried by a floating (high-impedance) line
		public static ushort RandomBits16()
		{
			pcg_rngState = pcg_rngState * 747796405 + 2891336453;
			uint result = ((pcg_rngState >> (int)((pcg_rngState >> 28) + 4)) ^ pcg_rngState) * 277803737;
			result = (result >> 22) ^ result;
			return (ushort)result;
		}

		// Random index in [0, n)
		public static int RandomIndex(int n)
		{
			pcg_rngState = pcg_rngState * 747796405 + 2891336453;
			uint result = ((pcg_rngState >> (int)((pcg_rngState >> 28) + 4)) ^ pcg_rngState) * 277803737;
			result = (result >> 22) ^ result;
			return (int)(result % (uint)n);
		}

		// Random bytes for one-off initialisation (RAM contents): the test seed when one is set, else the shared Random
		public static void NextBytes(Span<byte> bytes)
		{
			if (testRng != null) testRng.NextBytes(bytes);
			else lock (rng) rng.NextBytes(bytes);
		}

		public static bool RandomBool()
		{
			pcg_rngState = pcg_rngState * 747796405 + 2891336453;
			uint result = ((pcg_rngState >> (int)((pcg_rngState >> 28) + 4)) ^ pcg_rngState) * 277803737;
			result = (result >> 22) ^ result;
			return result < uint.MaxValue / 2;
		}

		// ---- Building the tree ----

		public static SimChip BuildSimChip(ChipDescription chipDesc, ChipLibrary library)
		{
			return BuildSimChip(chipDesc, library, -1, null);
		}

		public static SimChip BuildSimChip(ChipDescription chipDesc, ChipLibrary library, int subChipID, uint[] internalState)
		{
			SimChip simChip = BuildSimChipRecursive(chipDesc, library, subChipID, internalState);
			return simChip;
		}

		// Recursively build full representation of chip from its description for simulation.
		static SimChip BuildSimChipRecursive(ChipDescription chipDesc, ChipLibrary library, int subChipID, uint[] internalState)
		{
			// Recursively create subchips
			SimChip[] subchips = chipDesc.SubChips.Length == 0 ? Array.Empty<SimChip>() : new SimChip[chipDesc.SubChips.Length];

			for (int i = 0; i < chipDesc.SubChips.Length; i++)
			{
				SubChipDescription subchipDesc = chipDesc.SubChips[i];
				ChipDescription subchipFullDesc = library.GetChipDescriptionForSim(subchipDesc.Name);
				// Tolerate an unresolved sub-chip (e.g. a stale in-memory reference after a rename): treat as empty.
				SimChip subChip = subchipFullDesc != null ? BuildSimChipRecursive(subchipFullDesc, library, subchipDesc.ID, subchipDesc.InternalData) : new SimChip();
				subchips[i] = subChip;
			}

			SimChip simChip = new(chipDesc, subChipID, internalState, subchips);

			// Create connections
			for (int i = 0; i < chipDesc.Wires.Length; i++)
			{
				simChip.AddConnection(chipDesc.Wires[i].SourcePinAddress, chipDesc.Wires[i].TargetPinAddress);
			}

			return simChip;
		}

		// ---- Runtime modifications (enqueued from the main thread, applied on the sim thread) ----

		public static void AddPin(SimChip simChip, int pinID, bool isInputPin)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.AddPin,
				modifyTarget = simChip,
				simPinToAdd = new SimPin(pinID, isInputPin, simChip),
				pinIsInputPin = isInputPin
			};
			modificationQueue.Enqueue(command);
		}

		public static void RemovePin(SimChip simChip, int pinID)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.RemovePin,
				modifyTarget = simChip,
				removePinID = pinID
			};
			modificationQueue.Enqueue(command);
		}

		public static void AddSubChip(SimChip simChip, ChipDescription desc, ChipLibrary chipLibrary, int subChipID, uint[] subChipInternalData)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.AddSubchip,
				modifyTarget = simChip,
				chipDesc = desc,
				lib = chipLibrary,
				subChipID = subChipID,
				subChipInternalData = subChipInternalData
			};
			modificationQueue.Enqueue(command);
		}

		public static void AddConnection(SimChip simChip, PinAddress source, PinAddress target)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.AddConnection,
				modifyTarget = simChip,
				sourcePinAddress = source,
				targetPinAddress = target
			};
			modificationQueue.Enqueue(command);
		}

		public static void RemoveConnection(SimChip simChip, PinAddress source, PinAddress target)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.RemoveConnection,
				modifyTarget = simChip,
				sourcePinAddress = source,
				targetPinAddress = target
			};
			modificationQueue.Enqueue(command);
		}

		public static void RemoveSubChip(SimChip simChip, int id)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.RemoveSubChip,
				modifyTarget = simChip,
				removeSubChipID = id
			};
			modificationQueue.Enqueue(command);
		}

		// Note: this should only be called from the sim thread
		public static void ApplyModifications()
		{
			while (modificationQueue.Count > 0)
			{
				needsOrderPass = true;

				if (modificationQueue.TryDequeue(out SimModifyCommand cmd))
				{
					if (cmd.type == SimModifyCommand.ModificationType.AddSubchip)
					{
						SimChip newSubChip = BuildSimChip(cmd.chipDesc, cmd.lib, cmd.subChipID, cmd.subChipInternalData);
						cmd.modifyTarget.AddSubChip(newSubChip);
					}
					else if (cmd.type == SimModifyCommand.ModificationType.RemoveSubChip)
					{
						cmd.modifyTarget.RemoveSubChip(cmd.removeSubChipID);
					}
					else if (cmd.type == SimModifyCommand.ModificationType.AddConnection)
					{
						cmd.modifyTarget.AddConnection(cmd.sourcePinAddress, cmd.targetPinAddress);
					}
					else if (cmd.type == SimModifyCommand.ModificationType.RemoveConnection)
					{
						cmd.modifyTarget.RemoveConnection(cmd.sourcePinAddress, cmd.targetPinAddress); //
					}
					else if (cmd.type == SimModifyCommand.ModificationType.AddPin)
					{
						cmd.modifyTarget.AddPin(cmd.simPinToAdd, cmd.pinIsInputPin);
					}
					else if (cmd.type == SimModifyCommand.ModificationType.RemovePin)
					{
						cmd.modifyTarget.RemovePin(cmd.removePinID);
					}
				}
			}
		}

		public static void Reset()
		{
			forcedClockState = -1;
			simulationFrame = 0;
			prevRootSimChip = null;
			modificationQueue?.Clear();
			stopwatch.Restart();
			elapsedSecondsOld = 0;
		}

		struct SimModifyCommand
		{
			public enum ModificationType
			{
				AddSubchip,
				RemoveSubChip,
				AddConnection,
				RemoveConnection,
				AddPin,
				RemovePin
			}

			public ModificationType type;
			public SimChip modifyTarget;
			public ChipDescription chipDesc;
			public ChipLibrary lib;
			public int subChipID;
			public uint[] subChipInternalData;
			public PinAddress sourcePinAddress;
			public PinAddress targetPinAddress;
			public SimPin simPinToAdd;
			public bool pinIsInputPin;
			public int removePinID;
			public int removeSubChipID;
		}
	}
}
