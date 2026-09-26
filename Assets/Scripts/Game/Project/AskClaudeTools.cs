using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Graphics;
using DLS.SaveSystem;
using DLS.Simulation;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DLS.Game
{
    // Tools Claude can call to actually edit circuits, plus the read-only QA tools it uses to verify its
    // own work (truth_table / test_sequence -> CircuitTester). Editing tools are BATCH-oriented: one call
    // does many adds/connections at once (one Clean Up at the end). Every tool returns a short summary
    // ("done (N)" or the per-item failures). All executors run on the MAIN thread (from AskClaude.Poll).
    public static class AskClaudeTools
    {
        public static JArray Schemas()
        {
            JObject Tool(string name, string desc, JObject props, params string[] required)
            {
                var req = new JArray();
                foreach (string r in required) req.Add(r);
                return new JObject
                {
                    ["name"] = name,
                    ["description"] = desc,
                    ["input_schema"] = new JObject { ["type"] = "object", ["properties"] = props, ["required"] = req }
                };
            }

            JObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
            JObject StrArray(string d) => new() { ["type"] = "array", ["description"] = d, ["items"] = new JObject { ["type"] = "string" } };
            JObject LinkArray(string d) => new()
            {
                ["type"] = "array",
                ["description"] = d,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject { ["from"] = Str("label de la sortie (source)"), ["to"] = Str("label de l'entree (cible)") },
                    ["required"] = new JArray { "from", "to" }
                }
            };

            return new JArray
            {
                Tool("view_module", "Ouvre/inspecte l'etat actuel d'un module (bascule la vue dessus) et renvoie sa description complete.",
                    new JObject { ["module"] = Str("Nom du module a regarder.") }, "module"),
                Tool("create_module", "Cree un nouveau module vide et bascule la vue dessus.",
                    new JObject { ["name"] = Str("Nom du nouveau module.") }, "name"),
                Tool("add_inputs", "Ajoute PLUSIEURS entrees d'un coup a un module.",
                    new JObject { ["module"] = Str("Module cible."), ["names"] = StrArray("Noms des entrees a creer."), ["bits"] = new JObject { ["type"] = "integer", ["description"] = "Largeur en bits (1, 4 ou 8). Defaut 1." } }, "module", "names"),
                Tool("add_outputs", "Ajoute PLUSIEURS sorties d'un coup a un module.",
                    new JObject { ["module"] = Str("Module cible."), ["names"] = StrArray("Noms des sorties a creer."), ["bits"] = new JObject { ["type"] = "integer", ["description"] = "Largeur en bits (1, 4 ou 8). Defaut 1." } }, "module", "names"),
                Tool("add_components", "Ajoute PLUSIEURS composants d'un coup a un module (par leur nom, ex: NAND).",
                    new JObject { ["module"] = Str("Module cible."), ["components"] = StrArray("Noms des composants a ajouter (repeter le meme nom pour plusieurs instances).") }, "module", "components"),
                Tool("connect", "Branche PLUSIEURS paires sortie->entree d'un coup. Labels EXACTS des pins (ex: 'A', 'NAND#2.OUT').",
                    new JObject { ["module"] = Str("Module cible."), ["links"] = LinkArray("Liste des connexions {from, to}.") }, "module", "links"),
                Tool("disconnect", "Debranche PLUSIEURS fils d'un coup.",
                    new JObject { ["module"] = Str("Module cible."), ["links"] = LinkArray("Liste des fils a retirer {from, to}.") }, "module", "links"),
                Tool("remove_elements", "Supprime des elements d'un module : composants (label exact, ex \"NAND#2\") et/ou entrees/sorties (leur nom). Les fils attaches sont retires avec eux.",
                    new JObject { ["module"] = Str("Module cible."), ["elements"] = StrArray("Labels des composants et/ou noms des entrees/sorties a supprimer.") }, "module", "elements"),
                Tool("delete_module", "Supprime COMPLETEMENT un module (son onglet et son fichier) du projet. Ses instances sont retirees des autres modules. Irreversible : uniquement pour un module que tu as cree en trop, ou si l'utilisateur le demande.",
                    new JObject { ["name"] = Str("Nom du module a supprimer.") }, "name"),
                Tool("bind_keys", "Assigne la touche clavier de composants KEY (une lettre A-Z ou un chiffre 0-9). Un KEY sort 1 tant que sa touche est pressee.",
                    new JObject
                    {
                        ["module"] = Str("Module cible."),
                        ["binds"] = new JObject
                        {
                            ["type"] = "array", ["description"] = "Liste {component, key}.",
                            ["items"] = new JObject { ["type"] = "object", ["properties"] = new JObject { ["component"] = Str("Label du composant KEY (ex \"KEY#2\")."), ["key"] = Str("Touche : une seule lettre A-Z ou chiffre 0-9.") }, ["required"] = new JArray { "component", "key" } }
                        }
                    }, "module", "binds"),
                Tool("set_layout",
                    "Contraintes de position, respectees par le Clean Up (qui tourne apres chaque modification) et conservees a la sauvegarde. Utilisable au moment de poser les elements comme plus tard. col = colonne : plus grand = plus a droite, valeur EGALE = meme colonne, 1 = juste apres les entrees (0 = automatique). row = ligne : plus grand = plus haut ; deux elements avec la MEME valeur de row sont alignes sur la meme ligne horizontale (0 = automatique). Un champ omis reste inchange.",
                    new JObject
                    {
                        ["module"] = Str("Module cible."),
                        ["items"] = new JObject
                        {
                            ["type"] = "array", ["description"] = "Liste {element, col, row}.",
                            ["items"] = new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    ["element"] = Str("Label d'un composant (ex \"NAND#2\") ou nom d'une entree/sortie."),
                                    ["col"] = new JObject { ["type"] = "integer", ["description"] = "Colonne imposee (>=1), 0 = automatique." },
                                    ["row"] = new JObject { ["type"] = "integer", ["description"] = "Ligne : plus grand = plus haut, meme valeur = meme ligne. 0 = automatique." }
                                },
                                ["required"] = new JArray { "element" }
                            }
                        }
                    }, "module", "items"),
                Tool("truth_table", "QA combinatoire : calcule la table de verite COMPLETE du module en le SIMULANT vraiment (max 6 bits d'entree au total). Etat neuf a chaque ligne.",
                    new JObject { ["module"] = Str("Module a tester.") }, "module"),
                Tool("test_sequence",
                    "QA / debug : simule vraiment le module en jouant une SUITE d'etapes (\"j'appuie sur ca, puis ca...\"). Apres CHAQUE etape, renvoie l'etat des sorties et des afficheurs (LED / 7-segments / ecrans) presents sur le module. L'etat interne (bascules, memoire) est CONSERVE d'une etape a l'autre : marche donc aussi pour le sequentiel. HORLOGE : le temps reel ne s'ecoule pas pendant un test, la CLOCK est donc tenue a 0 et c'est TOI qui la fais avancer — mets \"clocks\": N dans une etape pour jouer N cycles complets (un front montant par cycle), ou pilote le niveau a la main avec set {pin:\"CLOCK\", value:\"1\"/\"0\"}. \"clocks\" agit sur le composant CLOCK et/ou sur l'ENTREE nommee CLOCK/CLK du module ; une horloge qui est une entree sous un autre nom se pilote avec set. Le rapport dit ce que \"clocks\" a pilote. Ne debranche JAMAIS la CLOCK et n'ajoute jamais d'entree de test : tout est pilotable tel quel (entrees, KEY, CLOCK). Depart : entrees a 0, touches relachees, CLOCK a 0, memoires non initialisees. Chaque appel repart de ce depart : mets TOUTE la sequence dans UN seul appel.",
                    new JObject
                    {
                        ["module"] = Str("Module a tester."),
                        ["steps"] = new JObject
                        {
                            ["type"] = "array",
                            ["description"] = "Etapes jouees dans l'ordre (max " + CircuitTester.MaxSteps + ").",
                            ["items"] = new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    ["set"] = new JObject
                                    {
                                        ["type"] = "array",
                                        ["description"] = "Entrees a (re)positionner a cette etape ; celles non citees gardent leur valeur precedente.",
                                        ["items"] = new JObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JObject
                                            {
                                                ["pin"] = Str("Nom EXACT d'une entree du module, OU le nom d'un composant KEY (pour l'appuyer), OU \"CLOCK\" (pour forcer le niveau de l'horloge a la main)."),
                                                ["value"] = Str("Valeur : \"0\"/\"1\", un nombre decimal, ou du binaire prefixe \"0b1010\".")
                                            },
                                            ["required"] = new JArray { "pin", "value" }
                                        }
                                    },
                                    ["ticks"] = new JObject { ["type"] = "integer", ["description"] = "Nombre de ticks de simulation par phase (defaut " + CircuitTester.DefaultTicksPerStep + ", suffisant pour que tout se propage)." },
                                    ["clocks"] = new JObject { ["type"] = "integer", ["description"] = "Nombre de cycles d'horloge COMPLETS a jouer pendant cette etape (chaque cycle : CLOCK a 0 puis a 1 = un front montant, puis retour a 0). C'est LA facon de faire avancer un circuit pilote par une CLOCK : le temps reel ne s'ecoule pas pendant un test, donc sans ca l'horloge reste a 0. Agit sur le composant CLOCK et/ou sur l'entree du module nommee CLOCK/CLK ; les \"set\" de l'etape sont appliques avant les cycles. Max " + CircuitTester.MaxClockCyclesPerStep + " par etape." },
                                    ["note"] = Str("Courte etiquette de l'etape (ex: \"front montant\"), affichee dans le rapport.")
                                }
                            }
                        },
                        ["watch"] = StrArray("Optionnel : pins INTERNES a surveiller a chaque etape, labels exacts (ex: \"NAND#2.OUT\") — pour localiser une panne.")
                    }, "module", "steps"),
                Tool("rename_pins", "Renomme PLUSIEURS entrees/sorties d'un coup.",
                    new JObject
                    {
                        ["module"] = Str("Module cible."),
                        ["renames"] = new JObject
                        {
                            ["type"] = "array", ["description"] = "Liste des renommages {current, new}.",
                            ["items"] = new JObject { ["type"] = "object", ["properties"] = new JObject { ["current"] = Str("nom actuel"), ["new"] = Str("nouveau nom") }, ["required"] = new JArray { "current", "new" } }
                        }
                    }, "module", "renames")
            };
        }

        public static string Execute(string name, JObject input)
        {
            try
            {
                string S(string k) => (string)(input?[k]) ?? "";
                JArray A(string k) => input?[k] as JArray;
                return name switch
                {
                    "view_module" => ViewModule(S("module")),
                    "create_module" => CreateModule(S("name")),
                    "add_inputs" => AddPins(S("module"), A("names"), PinBits(input), true),
                    "add_outputs" => AddPins(S("module"), A("names"), PinBits(input), false),
                    "add_components" => AddComponents(S("module"), A("components")),
                    "connect" => ConnectBatch(S("module"), A("links")),
                    "disconnect" => DisconnectBatch(S("module"), A("links")),
                    "rename_pins" => RenameBatch(S("module"), A("renames")),
                    "remove_elements" => RemoveElements(S("module"), A("elements")),
                    "delete_module" => DeleteModule(S("name")),
                    "bind_keys" => BindKeys(S("module"), A("binds")),
                    "set_layout" => SetLayout(S("module"), A("items")),
                    "truth_table" => TruthTable(S("module")),
                    "test_sequence" => TestSequence(S("module"), A("steps"), A("watch")),
                    _ => "Outil inconnu : " + name
                };
            }
            catch (Exception e)
            {
                return "Erreur pendant l'execution : " + e.Message;
            }
        }

        // ---------------- tools ----------------

        static string ViewModule(string module)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
            return "Etat du module \"" + ModuleName(p) + "\" :\n\n" + CircuitExporter.ExportChip(desc, p.chipLibrary);
        }

        static string CreateModule(string name)
        {
            Project p = Project.ActiveProject;
            if (string.IsNullOrWhiteSpace(name)) return "Le nom du module est vide.";
            if (p.chipLibrary.HasChip(name)) return $"Un module \"{name}\" existe deja.";

            // Only the NEW module is written (a module must exist as a file/tab to be addressable); the
            // module being left is never auto-saved.
            p.CreateBlankDevChip();
            ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
            desc.Name = name;
            p.SaveFromDescription(desc, Project.SaveMode.SaveAs);
            AskClaudeMenu.NotifyViewportDirty();
            Touch(p.ViewedChip); // the empty module: Ctrl+Z on it empties it again
            return $"done. Module \"{name}\" cree et ouvert (vide).";
        }

        static string AddPins(string module, JArray names, int bits, bool isInput)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            if (!p.CanEditViewedChip) return "Le module n'est pas editable.";
            if (names == null || names.Count == 0) return "Aucun nom fourni.";

            var items = new List<(string, string)>();
            bool anyOk = false;
            foreach (JToken t in names)
            {
                string n = (string)t;
                string r = AddOnePin(p, n, bits, isInput);
                items.Add((n, r));
                anyOk |= r == "done";
            }
            if (anyOk) AfterMutation(p);
            return Summarise(items);
        }

        static string AddComponents(string module, JArray comps)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            if (!p.CanEditViewedChip) return "Le module n'est pas editable.";
            if (comps == null || comps.Count == 0) return "Aucun composant fourni.";

            var items = new List<(string, string)>();
            var createdIDs = new List<int>();
            bool anyOk = false;
            foreach (JToken t in comps)
            {
                string type = (string)t;
                string r = AddOneComponent(p, type, out int id);
                if (r == "done") createdIDs.Add(id);
                items.Add((type, r));
                anyOk |= r == "done";
            }
            if (anyOk) AfterMutation(p);

            string summary = Summarise(items);
            if (createdIDs.Count == 0) return summary;

            // Report the labels the new components ended up with AND their exact pin interface, so connect /
            // set_layout / bind_keys can address them straight away without probing ("#n" depends on how many
            // instances now exist, and a custom brick's interface is whatever it currently has).
            ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
            Dictionary<int, string> names = CircuitExporter.BuildComponentNames(desc);
            var byType = new List<(string type, List<string> labels, string iface)>();
            foreach (int id in createdIDs)
            {
                if (!names.TryGetValue(id, out string label)) continue;
                if (!p.ViewedChip.TryGetSubChipByID(id, out SubChipInstance sc)) continue;
                var entry = byType.FirstOrDefault(e => e.type == sc.Description.Name);
                if (entry.labels == null)
                {
                    entry = (sc.Description.Name, new List<string>(), CircuitExporter.ComponentInterfaceLine(sc.Description));
                    byType.Add(entry);
                }
                entry.labels.Add(label);
            }

            var parts = byType.Select(e => $"{string.Join(", ", e.labels)} ({e.iface})");
            return summary + "\nAjoutes : " + string.Join(" ; ", parts);
        }

        static string ConnectBatch(string module, JArray links)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            if (!p.CanEditViewedChip) return "Le module n'est pas editable.";
            if (links == null || links.Count == 0) return "Aucun lien fourni.";

            var map = CircuitExporter.BuildPinLabelMap(p.ViewedChip, p.chipLibrary);
            var items = new List<(string, string)>();
            bool anyOk = false;
            foreach (JToken l in links)
            {
                string from = (string)l["from"], to = (string)l["to"];
                string r = ConnectOne(p, from, to, map);
                items.Add(($"{from} -> {to}", r));
                anyOk |= r == "done";
            }
            if (anyOk) AfterMutation(p);
            return Summarise(items);
        }

        static string DisconnectBatch(string module, JArray links)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            if (!p.CanEditViewedChip) return "Le module n'est pas editable.";
            if (links == null || links.Count == 0) return "Aucun lien fourni.";

            var map = CircuitExporter.BuildPinLabelMap(p.ViewedChip, p.chipLibrary);
            var items = new List<(string, string)>();
            bool anyOk = false;
            foreach (JToken l in links)
            {
                string from = (string)l["from"], to = (string)l["to"];
                string r = DisconnectOne(p, from, to, map);
                items.Add(($"{from} -> {to}", r));
                anyOk |= r == "done";
            }
            if (anyOk) AfterMutation(p);
            return Summarise(items);
        }

        static string RenameBatch(string module, JArray renames)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            if (!p.CanEditViewedChip) return "Le module n'est pas editable.";
            if (renames == null || renames.Count == 0) return "Aucun renommage fourni.";

            var items = new List<(string, string)>();
            bool anyOk = false;
            foreach (JToken t in renames)
            {
                string cur = (string)t["current"], nw = (string)t["new"];
                string r = RenameOne(p, cur, nw); // rebuilds label map internally (renames change labels)
                items.Add(($"{cur} -> {nw}", r));
                anyOk |= r == "done";
            }
            if (anyOk) AfterMutation(p);
            return Summarise(items);
        }

        static string RemoveElements(string module, JArray elements)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            if (!p.CanEditViewedChip) return "Le module n'est pas editable.";
            if (elements == null || elements.Count == 0) return "Aucun element fourni.";

            // Resolve EVERY label first: deleting shifts the "#n" suffixes of the remaining components.
            Dictionary<string, IMoveable> map = BuildElementMap(p);
            var targets = new List<(string label, IMoveable el, string err)>();
            foreach (JToken t in elements)
            {
                string label = ((string)t ?? "").Trim();
                if (map.TryGetValue(label, out IMoveable el)) targets.Add((label, el, null));
                else targets.Add((label, null, $"element \"{label}\" introuvable. Presents : {string.Join(", ", map.Keys)}."));
            }

            var items = new List<(string, string)>();
            bool anyOk = false;
            foreach ((string label, IMoveable el, string resolveErr) in targets)
            {
                if (resolveErr != null) { items.Add((label, resolveErr)); continue; }
                bool ok = el is SubChipInstance ? p.ViewedChip.TryDeleteSubChipByID(el.ID) : p.ViewedChip.TryDeleteDevPinByID(el.ID);
                items.Add((label, ok ? "done" : "suppression impossible."));
                anyOk |= ok;
            }

            if (anyOk) AfterMutation(p);
            return Summarise(items) + (anyOk ? "\n(Les suffixes #n des composants restants ont pu changer : verifie avec view_module.)" : "");
        }

        static string DeleteModule(string name)
        {
            Project p = Project.ActiveProject;
            if (p == null) return "Aucun projet ouvert.";
            if (string.IsNullOrWhiteSpace(name)) return "Nom vide.";
            if (!p.chipLibrary.HasChip(name)) return $"Le module \"{name}\" n'existe pas.";
            if (p.chipLibrary.IsBuiltinChip(name)) return $"\"{name}\" est une primitive du simulateur : impossible de la supprimer.";

            p.DeleteChip(name);
            AskClaudeMenu.NotifyViewportDirty();
            return $"done. Module \"{name}\" supprime.";
        }

        static string BindKeys(string module, JArray binds)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            if (!p.CanEditViewedChip) return "Le module n'est pas editable.";
            if (binds == null || binds.Count == 0) return "Aucune assignation fournie.";

            Dictionary<string, IMoveable> map = BuildElementMap(p);
            var items = new List<(string, string)>();
            foreach (JToken t in binds)
            {
                string comp = ((string)t?["component"] ?? "").Trim();
                string key = ((string)t?["key"] ?? "").Trim().ToUpperInvariant();
                items.Add(($"{comp} -> {key}", BindOneKey(p, map, comp, key)));
            }

            return Summarise(items);
        }

        static string BindOneKey(Project p, Dictionary<string, IMoveable> map, string comp, string key)
        {
            if (key.Length != 1 || !((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9')))
                return "touche invalide (une seule lettre A-Z ou chiffre 0-9).";
            if (!map.TryGetValue(comp, out IMoveable el)) return $"composant \"{comp}\" introuvable.";
            if (el is not SubChipInstance sc || sc.ChipType != ChipType.Key) return $"\"{comp}\" n'est pas un composant KEY.";
            if (sc.InternalData == null || sc.InternalData.Length == 0) return "composant KEY sans donnee interne.";

            char c = key[0];
            sc.InternalData[0] = c;
            sc.SetKeyChipActivationChar(c);
            // The sim may not have been given this subchip yet (structural changes are applied on the sim
            // thread): in that case the queued build reads InternalData above, so nothing more to do.
            (bool found, SimChip simChip) = p.ViewedChip.SimChip.TryGetSubChipFromID(sc.ID);
            if (found && simChip.InternalState.Length > 0) simChip.InternalState[0] = c;
            return "done";
        }

        static string SetLayout(string module, JArray itemsIn)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            if (!p.CanEditViewedChip) return "Le module n'est pas editable.";
            if (itemsIn == null || itemsIn.Count == 0) return "Aucune contrainte fournie.";

            Dictionary<string, IMoveable> map = BuildElementMap(p);
            var items = new List<(string, string)>();
            bool anyOk = false;
            foreach (JToken t in itemsIn)
            {
                string label = ((string)t?["element"] ?? "").Trim();
                int? col = (int?)t?["col"];
                int? row = (int?)t?["row"];

                string r;
                if (!map.TryGetValue(label, out IMoveable el)) r = $"element \"{label}\" introuvable. Presents : {string.Join(", ", map.Keys)}.";
                else if (col == null && row == null) r = "ni col ni row fourni.";
                else if (col is < 0) r = "col doit etre >= 0 (0 = automatique).";
                else
                {
                    if (col.HasValue) el.LayoutCol = col.Value;
                    if (row.HasValue) el.LayoutRow = row.Value;
                    r = "done";
                }

                items.Add(($"{label} col={(col.HasValue ? col.Value.ToString() : "-")} row={(row.HasValue ? row.Value.ToString() : "-")}", r));
                anyOk |= r == "done";
            }

            if (anyOk) AfterMutation(p);
            return Summarise(items);
        }

        // Every addressable element of the viewed chip, keyed by the SAME labels the export/connect tools
        // use (dev pins by disambiguated name, components as "NAND" / "NAND#2").
        static Dictionary<string, IMoveable> BuildElementMap(Project p)
        {
            ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
            var byId = new Dictionary<int, IMoveable>();
            foreach (IMoveable e in p.ViewedChip.Elements) byId[e.ID] = e;

            var map = new Dictionary<string, IMoveable>(StringComparer.OrdinalIgnoreCase);
            string[] inNames = CircuitExporter.Disambiguate(desc.InputPins.Select(x => x.Name).ToArray());
            string[] outNames = CircuitExporter.Disambiguate(desc.OutputPins.Select(x => x.Name).ToArray());
            for (int i = 0; i < desc.InputPins.Length; i++)
                if (byId.TryGetValue(desc.InputPins[i].ID, out IMoveable el)) map[inNames[i]] = el;
            for (int i = 0; i < desc.OutputPins.Length; i++)
                if (byId.TryGetValue(desc.OutputPins[i].ID, out IMoveable el)) map[outNames[i]] = el;

            Dictionary<int, string> compNames = CircuitExporter.BuildComponentNames(desc);
            foreach (SubChipDescription sub in desc.SubChips)
                if (byId.TryGetValue(sub.ID, out IMoveable el)) map[compNames[sub.ID]] = el;

            return map;
        }

        // ---------------- QA / debug (read-only: simulates an isolated copy) ----------------

        static string TruthTable(string module)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            string result = null;
            p.RunWithSimulationPaused(() => result = CircuitTester.RunTruthTable(p));
            return result;
        }

        static string TestSequence(string module, JArray steps, JArray watch)
        {
            if (!SwitchToModule(module, out string err)) return err;
            Project p = Project.ActiveProject;
            if (steps == null || steps.Count == 0) return "Aucune etape fournie (steps).";

            var parsed = new List<CircuitTester.Step>();
            foreach (JToken t in steps)
            {
                var step = new CircuitTester.Step
                {
                    ticks = (int?)(t?["ticks"]) ?? CircuitTester.DefaultTicksPerStep,
                    clocks = (int?)(t?["clocks"]) ?? 0,
                    note = (string)(t?["note"])
                };
                if (t?["set"] is JArray sets)
                    foreach (JToken s in sets)
                        step.sets.Add(((string)s?["pin"], (string)s?["value"]));
                parsed.Add(step);
            }

            var watchLabels = new List<string>();
            if (watch != null)
                foreach (JToken w in watch)
                    watchLabels.Add((string)w);

            string result = null;
            p.RunWithSimulationPaused(() => result = CircuitTester.RunSequence(p, parsed, watchLabels));
            return result;
        }

        // ---------------- single-item cores (no module switch / no Clean Up) ----------------

        static string AddOnePin(Project p, string name, int bits, bool isInput)
        {
            if (string.IsNullOrWhiteSpace(name)) return "nom vide.";
            if (name.Length > 16) return "nom trop long (max 16).";

            PinBitCount bc = bits == 4 ? PinBitCount.Bit4 : bits == 8 ? PinBitCount.Bit8 : PinBitCount.Bit1;
            int id = IDGenerator.GenerateNewElementID(p.ViewedChip);
            float minY = 0f;
            foreach (IMoveable e in p.ViewedChip.Elements) if (e is DevPinInstance dpp) minY = Mathf.Min(minY, dpp.Position.y);
            var pos = new Vector2(isInput ? -6f : 6f, minY - 1.2f);
            var pinDesc = new PinDescription(name, id, pos, bc, PinColour.Red, PinValueDisplayMode.Off);
            p.ViewedChip.AddNewDevPin(new DevPinInstance(pinDesc, isInput), false);
            return "done";
        }

        static string AddOneComponent(Project p, string type, out int newID)
        {
            newID = -1;
            if (string.IsNullOrWhiteSpace(type)) return "type vide.";
            if (!p.chipLibrary.TryGetChipDescription(type, out ChipDescription chipDesc)) return $"composant \"{type}\" inconnu.";
            var io = ChipTypeHelper.IsInputOrOutputPin(chipDesc.ChipType);
            if (io.isInput || io.isOutput) return "utilise add_inputs / add_outputs pour les pins.";
            if (!Available(p).Contains(chipDesc.Name)) return $"\"{chipDesc.Name}\" non disponible (brique custom non epinglee ?).";
            if (!p.ViewedChip.CanAddSubchip(chipDesc.Name)) return $"impossible (cycle interdit).";

            // Place from the LIVE description: a custom brick built during this session is not saved (nothing
            // auto-saves), so the library copy may still be the empty one created by create_module — placing
            // that would give an instance with no pins.
            ChipDescription placeDesc = LiveDesc(p, chipDesc);

            int id = IDGenerator.GenerateNewElementID(p.ViewedChip);
            SubChipDescription subDesc = DescriptionCreator.CreateBuiltinSubChipDescriptionForPlacement(placeDesc.ChipType, placeDesc.Name, id, Vector2.zero);
            p.ViewedChip.AddNewSubChip(new SubChipInstance(placeDesc, subDesc), false);
            newID = id;
            return "done";
        }

        static string ConnectOne(Project p, string from, string to, Dictionary<string, PinInstance> map)
        {
            if (!map.TryGetValue(from, out PinInstance a)) return PinNotFound(from, map);
            if (!map.TryGetValue(to, out PinInstance b)) return PinNotFound(to, map);

            PinInstance driver, sink;
            if (a.IsSourcePin && !b.IsSourcePin) { driver = a; sink = b; }
            else if (!a.IsSourcePin && b.IsSourcePin) { driver = b; sink = a; }
            else return "pas une paire sortie->entree valide.";

            if ((int)driver.bitCount != (int)sink.bitCount) return $"largeurs incompatibles ({(int)driver.bitCount} vs {(int)sink.bitCount} bits).";

            foreach (WireInstance w in p.ViewedChip.Wires)
            {
                if (!w.IsFullyConnected) continue;
                if (SamePin(w.TargetPin, sink))
                {
                    if (SamePin(w.SourcePin, driver)) return "deja branches.";
                    return $"l'entree \"{to}\" est deja branchee.";
                }
            }

            int spawn = p.ViewedChip.Wires.Count > 0 ? p.ViewedChip.Wires[^1].spawnOrder + 1 : 0;
            var wire = new WireInstance(new WireInstance.ConnectionInfo { pin = driver }, new WireInstance.ConnectionInfo { pin = sink }, new[] { driver.GetWorldPos(), sink.GetWorldPos() }, spawn);
            p.ViewedChip.AddWire(wire, false);
            return "done";
        }

        static string DisconnectOne(Project p, string from, string to, Dictionary<string, PinInstance> map)
        {
            if (!map.TryGetValue(from, out PinInstance a)) return PinNotFound(from, map);
            if (!map.TryGetValue(to, out PinInstance b)) return PinNotFound(to, map);

            foreach (WireInstance w in p.ViewedChip.Wires)
            {
                if (!w.IsFullyConnected) continue;
                if ((SamePin(w.SourcePin, a) && SamePin(w.TargetPin, b)) || (SamePin(w.SourcePin, b) && SamePin(w.TargetPin, a)))
                {
                    p.ViewedChip.DeleteWire(w);
                    return "done";
                }
            }
            return "aucun fil entre ces pins.";
        }

        static string RenameOne(Project p, string current, string newName, System.Collections.Generic.Dictionary<string, PinInstance> reuseMap = null)
        {
            if (string.IsNullOrWhiteSpace(newName)) return "nouveau nom vide.";
            if (newName.Length > 16) return "nom trop long (max 16).";
            var map = reuseMap ?? CircuitExporter.BuildPinLabelMap(p.ViewedChip, p.chipLibrary);
            if (!map.TryGetValue(current, out PinInstance pin)) return PinNotFound(current, map);
            if (pin.parent is not DevPinInstance) return $"\"{current}\" n'est pas une entree/sortie.";
            pin.Name = newName;
            return "done";
        }

        // ---------------- helpers ----------------

        static string Summarise(List<(string label, string result)> items)
        {
            int ok = items.Count(i => i.result == "done");
            if (ok == items.Count) return $"done ({ok}/{items.Count}).";
            var sb = new StringBuilder($"{ok}/{items.Count} reussis. Echecs :");
            foreach ((string label, string result) in items)
                if (result != "done") sb.Append($"\n  - {label} : {result}");
            return sb.ToString();
        }

        static string ModuleName(Project p)
        {
            string n = p.ViewedChip.ChipName;
            return string.IsNullOrEmpty(n) ? "(brique courante, non enregistree)" : n;
        }

        static bool SamePin(PinInstance a, PinInstance b) => PinAddress.Equals(a.Address, b.Address);

        static string PinNotFound(string label, Dictionary<string, PinInstance> map)
        {
            string msg = $"pin \"{label}\" introuvable.";
            int dot = label.LastIndexOf('.'); // last dot: a chip name may itself contain dots (e.g. "dev.RAM-8")
            var pins = new List<string>();
            if (dot > 0)
            {
                string comp = label.Substring(0, dot);
                foreach (string k in map.Keys)
                    if (k.StartsWith(comp + ".", StringComparison.OrdinalIgnoreCase)) pins.Add(k.Substring(comp.Length + 1));

                if (pins.Count > 0) msg += $" Pins de \"{comp}\" : {string.Join(", ", pins)}.";
                else
                {
                    // Distinguish "no such component here" from "component present but with no pins at all".
                    var comps = new List<string>();
                    Project p = Project.ActiveProject;
                    if (p != null) comps.AddRange(BuildElementMap(p).Keys.Where(k => !k.Contains('.')));
                    bool exists = comps.Any(c => string.Equals(c, comp, StringComparison.OrdinalIgnoreCase));
                    msg += exists
                        ? $" Le composant \"{comp}\" est bien la mais n'expose AUCUN pin."
                        : $" Composant \"{comp}\" absent de ce module. Elements presents : {string.Join(", ", comps)}.";
                }
            }
            else
            {
                foreach (string k in map.Keys) if (!k.Contains('.')) pins.Add(k);
                if (pins.Count > 0) msg += $" Entrees/sorties : {string.Join(", ", pins)}.";
            }
            return msg;
        }

        static int PinBits(JObject input)
        {
            int b = (int?)(input?["bits"]) ?? 1;
            return b == 4 ? 4 : b == 8 ? 8 : 1;
        }

        // Components Claude may add: ALL base builtins (displays, clock, tri-state, split/merge, gates...)
        // plus PINNED custom bricks. Excluded: I/O-pin pseudo-chips (use add_inputs/outputs) and buses.
        static HashSet<string> Available(Project p)
        {
            var set = new HashSet<string>(ChipDescription.NameComparer);

            bool Addable(string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                if (p.chipLibrary.TryGetChipDescription(name, out ChipDescription d))
                {
                    var io = ChipTypeHelper.IsInputOrOutputPin(d.ChipType);
                    if (io.isInput || io.isOutput) return false;
                    if (ChipTypeHelper.IsBusType(d.ChipType)) return false;
                }
                return true;
            }

            foreach (ChipDescription c in p.chipLibrary.allChips)
                if (p.chipLibrary.IsBuiltinChip(c.Name) && Addable(c.Name)) set.Add(c.Name);
            set.Add("NAND");

            ProjectDescription desc = p.description;
            if (desc.StarredList != null)
            {
                foreach (StarredItem item in desc.StarredList)
                {
                    if (!item.IsCollection) { if (Addable(item.Name)) set.Add(item.Name); continue; }
                    if (desc.ChipCollections != null)
                        foreach (ChipCollection col in desc.ChipCollections)
                            if (ChipDescription.NameMatch(col.Name, item.Name) && col.Chips != null)
                                foreach (string c in col.Chips) if (Addable(c)) set.Add(c);
                }
            }
            return set;
        }

        static List<string> AvailableSorted(Project p)
        {
            var list = new List<string>(Available(p));
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static string AvailableComponentsList()
        {
            Project p = Project.ActiveProject;
            return p == null ? "NAND" : string.Join(", ", AvailableSorted(p));
        }

        // Every available component with (for builtins) its behaviour AND its exact pin interface, so
        // Claude never probes pins by trial-and-error.
        public static string AvailableComponentsDetailed()
        {
            Project p = Project.ActiveProject;
            if (p == null) return "";
            var sb = new StringBuilder();
            foreach (string name in AvailableSorted(p))
            {
                if (!p.chipLibrary.TryGetChipDescription(name, out ChipDescription d)) continue;
                string info = CircuitExporter.BuiltinInfo(name);
                sb.Append($"  - {name} : ");
                if (!string.IsNullOrEmpty(info)) sb.Append(info + "  ");
                sb.AppendLine(CircuitExporter.ComponentInterfaceLine(LiveDesc(p, d))); // live: unsaved edits count
            }
            return sb.ToString().TrimEnd();
        }

        // The in-memory (possibly unsaved) version of a chip when it is open, else the saved one.
        static ChipDescription LiveDesc(Project p, ChipDescription saved) =>
            p.chipLibrary.GetChipDescriptionForSim(saved.Name) ?? saved;

        // ---- One undo step per Claude request ----
        // The tools mutate chips directly (no per-action undo records). Instead, the first time a chip is
        // touched during a turn its full state is snapshotted; when the turn ends, each touched chip gets one
        // undo action (before -> after), so a single Ctrl+Z on that chip reverts everything Claude did to it.
        static readonly Dictionary<DevChipInstance, UndoController.ChipSnapshot> turnSnapshots = new();

        public static void BeginTurn() => turnSnapshots.Clear();

        static void Touch(DevChipInstance chip)
        {
            if (chip == null || turnSnapshots.ContainsKey(chip)) return;
            try { turnSnapshots[chip] = new UndoController.ChipSnapshot(chip); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("Claude turn snapshot failed: " + e.Message); }
        }

        public static void EndTurn()
        {
            Project p = Project.ActiveProject;
            foreach ((DevChipInstance chip, UndoController.ChipSnapshot before) in turnSnapshots)
            {
                try
                {
                    // Layout of what Claude added, once, at the end of the request (part of the same undo step):
                    // a module that was empty gets a full Clean Up; an existing layout is kept and only the new
                    // elements are slotted in at sensible places.
                    var newIDs = new HashSet<int>(chip.Elements.Select(e => e.ID).Where(id => !before.ElementIDs.Contains(id)));
                    bool onScreen = p != null && p.ViewedChip == chip;
                    if (before.IsEmpty && chip.Elements.Count > 0) CircuitAutoLayout.CleanUp(chip, recordUndo: false, focusCamera: onScreen);
                    else if (newIDs.Count > 0) CircuitAutoLayout.PlaceNewElements(chip, newIDs);
                    if (onScreen) AskClaudeMenu.NotifyViewportDirty();
                }
                catch (Exception e) { UnityEngine.Debug.LogWarning("Claude turn layout failed: " + e.Message); }

                try { chip.UndoController.RecordClaudeTurn(before, new UndoController.ChipSnapshot(chip)); }
                catch (Exception e) { UnityEngine.Debug.LogWarning("Claude turn undo record failed: " + e.Message); }
            }
            turnSnapshots.Clear();
        }

        static bool SwitchToModule(string module, out string err)
        {
            Project p = Project.ActiveProject;
            err = null;
            if (p == null) { err = "Aucun projet ouvert."; return false; }

            bool editingTarget = p.CanEditViewedChip && (ChipDescription.NameMatch(p.ViewedChip.ChipName ?? "", module) || string.IsNullOrWhiteSpace(module));
            if (editingTarget) { Touch(p.ViewedChip); return true; }

            if (p.chipLibrary.HasChip(module))
            {
                // No auto-save on switching module: unsaved edits stay in memory (like the user's own
                // workflow — switching tabs never saves). Saving is the user's decision.
                p.LoadDevChipOrCreateNewIfDoesntExist(module);
                AskClaudeMenu.NotifyViewportDirty();
                Touch(p.ViewedChip);
                return true;
            }

            err = $"Le module \"{module}\" n'existe pas. Utilise create_module pour le creer.";
            return false;
        }

        // No automatic Clean Up here (it used to run after every edit; the user prefers to trigger it himself).
        static void AfterMutation(Project p)
        {
            AskClaudeMenu.NotifyViewportDirty();
        }
    }
}
