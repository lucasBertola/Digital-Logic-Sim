namespace DLS.Description
{
	// The memory state of a chip at the moment it was saved: the output of every NAND inside it (latches, flip-flops,
	// any feedback loop keeps its value this way; combinational gates are simply recomputed) and the contents of
	// the stateful builtins (RAM, display buffers, pulse counters). Restored when the chip is built for simulation,
	// only if its structure is still the one the state was captured from (Hash).
	public class ChipMemoryState
	{
		public string Hash;        // structure signature of the captured subtree
		public string Gates;       // NAND outputs, one bit each, in structure order, as hex
		public string[] Memories;  // internal words of each stateful builtin, in structure order, as hex
	}
}
