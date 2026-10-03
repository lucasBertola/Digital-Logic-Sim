using System;

namespace DLS.Simulation
{
	// RUN FAST (user, 2026-10-03): a module simulated by what it DOES instead of its gates. A model is one gate of the
	// compiled program (ChipType.FastModel leaf of the simulation tree): when one of its inputs changes, Run() maps
	// the input pin states (In) to the output pin states (Out, bits | floating flags << 16).
	public abstract class FastModel
	{
		public uint[] In, Out;
		// set by Run when its outputs must change again on the NEXT step with no input change (a register's output
		// lags its state by one step, like the clock-to-output delay of a real flip-flop)
		public bool RerunNextStep;
		public abstract void Run();
		public virtual FastModel Clone() => (FastModel)MemberwiseClone();

		// The compiled form SimKernel runs in place of Run() (0 = none: the program calls Run): its kind (1 table,
		// 2 sequential template), its int parameters, and the arrays it reads / writes (pinned by the program, shared
		// with Run so both paths see the same state).
		public virtual int KernelKind => 0;
		public virtual int[] KernelParams() => null;
		public virtual uint[][] KernelArrays() => null;

		protected FastModel(int inputs, int outputs)
		{
			In = new uint[inputs];
			Out = new uint[outputs];
		}
	}

	// A combinational module as a truth table, computed by the real simulator over every input combination (exact by
	// construction, floating outputs included). Index = the input pins' bits concatenated, first pin in the low bits.
	public sealed class LutModel : FastModel
	{
		public readonly int[] InputBits;
		public readonly uint[] Table; // [index * outputs + o]

		public LutModel(int[] inputBits, int outputs, uint[] table) : base(inputBits.Length, outputs)
		{
			InputBits = inputBits;
			Table = table;
		}

		public static int Index(uint[] inputs, int[] bits)
		{
			int index = 0, shift = 0;
			for (int j = 0; j < bits.Length; j++)
			{
				index |= (int)(inputs[j] & ((1u << bits[j]) - 1)) << shift;
				shift += bits[j];
			}
			return index;
		}

		public override void Run()
		{
			int b = Index(In, InputBits) * Out.Length;
			for (int o = 0; o < Out.Length; o++) Out[o] = Table[b + o];
		}

		public override FastModel Clone() => new LutModel(InputBits, Out.Length, Table);

		public override int KernelKind => 1;
		public override int[] KernelParams() => InputBits;
		public override uint[][] KernelArrays() => new[] { Table };
	}
}
