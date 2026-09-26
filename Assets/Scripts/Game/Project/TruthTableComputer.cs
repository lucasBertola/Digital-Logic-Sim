using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DLS.Description;
using DLS.Simulation;

namespace DLS.Game
{
    // Computes a chip's truth table using the REAL simulator. Must be called with the sim thread
    // parked (Project.RunWithSimulationPaused) since it uses the static Simulator.
    public static class TruthTableComputer
    {
        public const int MaxInputBits = 6; // 2^6 = 64 rows max (keeps the on-screen table readable)

        public class Result
        {
            public bool ok;
            public string message;
            public string[] inputNames = Array.Empty<string>();
            public string[] outputNames = Array.Empty<string>();
            public int[] inputBits = Array.Empty<int>();
            public int[] outputBits = Array.Empty<int>();
            public List<uint[]> inputRows = new();
            public List<uint[]> outputRows = new();
            public bool sequential;
        }

        public static Result Compute(ChipDescription desc, ChipLibrary lib, bool sequential)
        {
            var r = new Result { sequential = sequential };
            r.inputNames = desc.InputPins.Select(p => p.Name).ToArray();
            r.outputNames = desc.OutputPins.Select(p => p.Name).ToArray();
            r.inputBits = desc.InputPins.Select(p => Bits(p.BitCount)).ToArray();
            r.outputBits = desc.OutputPins.Select(p => Bits(p.BitCount)).ToArray();

            if (desc.InputPins.Length == 0 || desc.OutputPins.Length == 0)
            {
                r.message = "Ce circuit n'a pas d'entrée et/ou pas de sortie.";
                return r;
            }

            int totalInBits = r.inputBits.Sum();
            if (totalInBits > MaxInputBits)
            {
                r.message = $"Trop d'entrées ({totalInBits} bits, soit 2^{totalInBits} lignes) pour afficher une table complète.";
                return r;
            }

            int savedFrame = Simulator.simulationFrame;
            try
            {
                Simulator.forcedClockState = 0; // no real time passes here: pin the clock so rows are deterministic
                SimAudio audio = new();
                long combos = 1L << totalInBits;

                for (long c = 0; c < combos; c++)
                {
                    // Fresh state for every row (combinational semantics).
                    SimChip root = CircuitTester.BuildIsolatedSim(desc, lib);

                    int cursor = totalInBits;
                    var ins = new uint[desc.InputPins.Length];
                    for (int i = 0; i < desc.InputPins.Length; i++)
                    {
                        int w = r.inputBits[i];
                        cursor -= w;
                        uint v = (uint)((c >> cursor) & ((1L << w) - 1));
                        ins[i] = v;
                        PinState.Set(ref root.InputPins[i].State, (ushort)v, 0);
                    }

                    for (int s = 0; s < 8; s++)
                        Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);

                    var outs = new uint[desc.OutputPins.Length];
                    for (int i = 0; i < desc.OutputPins.Length; i++)
                        outs[i] = PinState.GetBitStates(root.OutputPins[i].State);

                    r.inputRows.Add(ins);
                    r.outputRows.Add(outs);
                }

                r.ok = true;
            }
            catch (Exception e)
            {
                r.ok = false;
                r.message = "Erreur de calcul : " + e.Message;
            }
            finally
            {
                Simulator.forcedClockState = -1;
                // Restore the live sim's frame counter and force it to re-establish its traversal order.
                Simulator.simulationFrame = savedFrame;
                Simulator.needsOrderPass = true;
            }

            return r;
        }

        static int Bits(PinBitCount c) => c switch { PinBitCount.Bit1 => 1, PinBitCount.Bit4 => 4, _ => 8 };
    }
}
