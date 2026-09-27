using System.Collections.Generic;
using System.IO;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using UnityEngine;

namespace DLS.Bench
{
    // Loads a project FOLDER (fixtures committed in the repo under TestData/Bench, independent of the user's
    // save location) into a ChipLibrary, exactly as Loader does for a saved project.
    public static class BenchProject
    {
        public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string BenchRoot => Path.Combine(RepoRoot, "TestData", "Bench");
        public static string FixtureProjectDir(string project) => Path.Combine(BenchRoot, project);
        public static string GoldenDir(string project) => Path.Combine(BenchRoot, "Golden", project);

        public static ChipLibrary LoadLibrary(string projectDir, out ChipDescription[] customChips)
        {
            string pdPath = Path.Combine(projectDir, "ProjectDescription.json");
            ProjectDescription pd = Serializer.DeserializeProjectDescription(File.ReadAllText(pdPath));

            var chips = new List<ChipDescription>();
            foreach (string name in pd.AllCustomChipNames)
            {
                string path = Path.Combine(projectDir, "Chips", name + ".json");
                if (!File.Exists(path)) continue;
                chips.Add(Serializer.DeserializeChipDescription(File.ReadAllText(path)));
            }

            ChipDescription[] builtins = BuiltinChipCreator.CreateAllBuiltinChipDescriptions();
            var customNames = new HashSet<string>(chips.Select(c => c.Name), ChipDescription.NameComparer);
            builtins = builtins.Where(b => !customNames.Contains(b.Name)).ToArray();

            customChips = chips.ToArray();
            UpgradeHelper.ApplyVersionChanges(customChips, builtins);
            return new ChipLibrary(customChips, builtins);
        }

        // A library with only the builtin chips (for the hand-written unit cases)
        public static ChipLibrary BuiltinsOnly() => new(System.Array.Empty<ChipDescription>(), BuiltinChipCreator.CreateAllBuiltinChipDescriptions());
    }
}
