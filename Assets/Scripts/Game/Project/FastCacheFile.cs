using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DLS.Description;
using DLS.SaveSystem;
using DLS.Simulation;
using Newtonsoft.Json.Linq;

namespace DLS.Game
{
	// RUN FAST decisions saved in the project (<project>/FastModels.json), so the next RUN FAST skips the truth tables
	// and the template verifications. Hand-written JSON (the player build strips what reflection-based
	// deserialisation needs): per chip its structure hash and "none" / a table / a template spec.
	public static class FastCacheFile
	{
		static string PathFor(string projectName) => Path.Combine(SavePaths.GetProjectPath(projectName), "FastModels.json");

		public static FastCache Load(string projectName)
		{
			var cache = new FastCache();
			try
			{
				string path = PathFor(projectName);
				if (File.Exists(path)) FromJson(File.ReadAllText(path), cache);
			}
			catch (Exception) { }
			return cache;
		}

		public static void Save(string projectName, FastCache cache)
		{
			try { File.WriteAllText(PathFor(projectName), ToJson(cache)); } catch (Exception) { }
		}

		public static string ToJson(FastCache cache)
		{
			var root = new JObject();
			foreach ((string name, string hash, FastModel model, FastTemplates.Spec spec) in cache.Entries)
			{
				var e = new JObject { ["hash"] = hash };
				if (model is LutModel lut)
				{
					e["kind"] = "lut";
					e["bits"] = new JArray(lut.InputBits);
					e["outs"] = lut.Out.Length;
					var bytes = new byte[lut.Table.Length * 4];
					Buffer.BlockCopy(lut.Table, 0, bytes, 0, bytes.Length);
					e["table"] = Convert.ToBase64String(bytes);
				}
				else if (spec != null)
				{
					e["kind"] = "seq";
					e["spec"] = new JObject
					{
						["kind"] = spec.Kind.ToString(), ["d"] = spec.D, ["clk"] = spec.Clk, ["load"] = spec.Load, ["reset"] = spec.Reset, ["oe"] = spec.Oe, ["cs"] = spec.Cs, ["we"] = spec.We,
						["addr"] = new JArray(spec.Addr), ["q"] = spec.Q, ["qn"] = spec.QN, ["rising"] = spec.Rising, ["loadHigh"] = spec.LoadHigh, ["resetHigh"] = spec.ResetHigh,
						["oeHigh"] = spec.OeHigh, ["csHigh"] = spec.CsHigh, ["weHigh"] = spec.WeHigh, ["resetMode"] = spec.ResetMode
					};
				}
				else e["kind"] = "none";
				root[name] = e;
			}
			return root.ToString();
		}

		public static void FromJson(string json, FastCache cache)
		{
			JObject root = JObject.Parse(json);
			foreach (JProperty p in root.Properties())
			{
				if (p.Value is not JObject e) continue;
				string hash = (string)e["hash"], kind = (string)e["kind"];
				if (hash == null) continue;
				if (kind == "lut")
				{
					int[] bits = e["bits"].Select(x => (int)x).ToArray();
					int outs = (int)e["outs"];
					byte[] bytes = Convert.FromBase64String((string)e["table"]);
					var table = new uint[bytes.Length / 4];
					Buffer.BlockCopy(bytes, 0, table, 0, bytes.Length);
					if (table.Length != (1 << bits.Sum()) * outs) continue;
					cache.Put(p.Name, hash, new LutModel(bits, outs, table));
				}
				else if (kind == "seq" && e["spec"] is JObject s)
				{
					var spec = new FastTemplates.Spec
					{
						Kind = Enum.TryParse((string)s["kind"], out FastTemplates.Kind k) ? k : FastTemplates.Kind.Register,
						D = (int)s["d"], Clk = (int)s["clk"], Load = (int)s["load"], Reset = (int)s["reset"], Oe = (int)s["oe"], Cs = (int)s["cs"], We = (int)s["we"],
						Addr = s["addr"].Select(x => (int)x).ToArray(), Q = (int)s["q"], QN = (int)s["qn"], Rising = (bool)s["rising"], LoadHigh = (bool)s["loadHigh"],
						ResetHigh = (bool)s["resetHigh"], OeHigh = (bool)s["oeHigh"], CsHigh = (bool)s["csHigh"], WeHigh = (bool)s["weHigh"], ResetMode = (int)s["resetMode"]
					};
					cache.PutSpec(p.Name, hash, spec);
				}
				else if (kind == "none") cache.Put(p.Name, hash, null);
			}
		}
	}

	// Gates vs fast tree on the same clock cycles, the fast tree taking the gates' state over exactly as RUN FAST
	// does; every top-level component's outputs (driven bits and floating mask) compared just before each clock edge.
	public static class FastVerify
	{
		public static string Compare(ChipDescription d, ChipLibrary lib, FastCache cache, int period, int halfPeriods, uint[] inputs)
		{
			SimChip ga = Simulator.BuildSimChip(d, lib), fa = FastBuilder.Build(d, lib, cache, new List<FastBuilder.Instance>());
			Simulator.ResetForTests(9);
			Simulator.stepsPerClockTransition = period;
			for (int i = 0; i < d.InputPins.Length; i++) { ga.InputPins[i].State = PinState.Make((ushort)inputs[i], 0); fa.InputPins[i].State = PinState.Make((ushort)inputs[i], 0); }
			Simulator.RunSimulationStep(ga, Array.Empty<DevPinInstance>(), new SimAudio());
			FastState.CopyIn(ga, fa, lib);
			var traces = new List<List<string>>();
			foreach (SimChip root in new[] { ga, fa })
			{
				Simulator.ResetForTests(9);
				Simulator.stepsPerClockTransition = period;
				var audio = new SimAudio();
				var tr = new List<string>();
				for (int half = 0; half < halfPeriods; half++)
				{
					for (int k = 0; k < period - 3; k++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
					if (half >= 4) tr.Add(string.Join(" ", root.SubChips.Where(c => c.OutputPins.Length > 0).Select(c => c.ID + ":" + string.Join(",", c.OutputPins.Select(o => Shown(o.State))))));
					for (int k = 0; k < 3; k++) Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), audio);
				}
				traces.Add(tr);
			}
			Simulator.ClearTestSeed();
			int diff = Enumerable.Range(0, traces[0].Count).Where(i => traces[0][i] != traces[1][i]).DefaultIfEmpty(-1).First();
			return diff < 0 ? null : $"half-period {diff + 4}: gates {traces[0][diff]} | fast {traces[1][diff]}";
		}

		// a pin's meaning: driven bits, and which bits float (the bits of a floating line are leftovers, not a value)
		public static string Shown(uint state)
		{
			uint tri = state >> 16, bits = state & 0xFFFF & ~tri;
			return tri == 0 ? bits.ToString("X") : bits.ToString("X") + "z" + tri.ToString("X");
		}
	}
}
