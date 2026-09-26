using System;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Diagnostic: simulates Registre8 (project PC) in isolation and prints, tick by tick, the state of the
// 3-STATE BUFFER output, the NOT#19 output and the OR#18 input pin that receives both, with the buffer
// disabled (IN=0) then enabled (IN=1).
//   Unity.exe -projectPath <proj> -executeMethod ZCheck.Run -quit -logFile <log>
public static class ZCheck
{
    const int BufferID = 1515351887, Not19ID = 1542998724, OrID = 948853649, Not17ID = 550525556;

    public static void Run()
    {
        StringBuilder sb = new();
        try
        {
            Project project = Loader.LoadProject("PC");
            ChipDescription desc = project.chipLibrary.GetChipDescription("Registre8");
            SimChip root = CircuitTester.BuildIsolatedSim(desc, project.chipLibrary);
            SimChip target = CircuitTester.TargetOf(root);

            SimChip buffer = target.GetSubChipFromID(BufferID);
            SimChip not19 = target.GetSubChipFromID(Not19ID);
            SimChip or = target.GetSubChipFromID(OrID);
            int inIdx = Array.FindIndex(desc.InputPins, p => p.Name == "IN");

            string S(uint state) => $"bits={PinState.GetBitStates(state) & 1} tri={PinState.GetTristateFlags(state) & 1}";

            foreach (ushort inVal in new ushort[] { 0, 1 })
            {
                sb.Append($"\n=== IN = {inVal} (buffer {(inVal == 1 ? "enabled" : "DISABLED = Z")}) ===\n");
                for (int t = 0; t < 8; t++)
                {
                    for (int i = 0; i < root.InputPins.Length; i++) PinState.Set(ref root.InputPins[i].State, (ushort)(i == inIdx ? inVal : 0), 0);
                    Simulator.RunSimulationStep(root, Array.Empty<DevPinInstance>(), new SimAudio());
                    sb.Append($"t{t}: BUF.OUT {S(buffer.OutputPins[0].State)} | NOT19.OUT {S(not19.OutputPins[0].State)} | OR.IN1 {S(or.InputPins[0].State)} | OR.IN2 {S(or.InputPins[1].State)} | OR.OUT {S(or.OutputPins[0].State)}\n");
                }
            }
        }
        catch (Exception e) { sb.Append("EXCEPTION: " + e + "\n"); }

        Debug.Log(sb.ToString());
        EditorApplication.Exit(0);
    }
}
