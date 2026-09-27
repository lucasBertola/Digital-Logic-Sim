using System;

namespace DLS.Simulation
{
	public class SimPin
	{
		public readonly int ID;
		public readonly SimChip parentChip;
		public readonly bool isInput;

		// Structure: the pins this pin feeds (wires), and how many wires feed it
		public SimPin[] ConnectedTargetPins = Array.Empty<SimPin>();
		public int numInputConnections;

		// Address of the pin this pin last took its value from (display colouring). Set by the compiled
		// program: statically for a pin with one source, per step for a merge node.
		public int latestSourceID = -1;
		public int latestSourceParentChipID = -1;

		// ---- State ----
		// Once the tree is compiled (SimProgram) the value lives in a slot of the program's state array,
		// shared with every pin on the same net; before that (a pin just created) it lives here.
		uint localState;
		internal uint[] stateArray;
		internal int stateIndex;

		// Compile scratch (SimProgram.Compile)
		internal int compileIndex;
		internal int compileStamp;

		public uint State
		{
			get { uint[] a = stateArray; return a == null ? localState : a[stateIndex]; }
			set { uint[] a = stateArray; if (a == null) localState = value; else a[stateIndex] = value; }
		}

		public SimPin(int id, bool isInput, SimChip parentChip)
		{
			this.parentChip = parentChip;
			this.isInput = isInput;
			ID = id;
			localState = PinState.FloatingLow;
		}

		public bool FirstBitHigh => PinState.FirstBitHigh(State);
	}
}
