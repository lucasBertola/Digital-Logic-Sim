using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DLS.Description;
using DLS.Graphics;
using DLS.SaveSystem;
using DLS.Simulation;
using Seb.Helpers;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DLS.Game
{
	public class Project
	{
		public enum SaveMode
		{
			Normal,
			Rename,
			SaveAs
		}

		public static Project ActiveProject;
		public readonly ChipLibrary chipLibrary;

		public ChipInteractionController controller;
		public ProjectDescription description;

		// ---- Display state ----
		public bool ShowGrid => description.Prefs_GridDisplayMode == 1;
		public bool PinNameDisplayIsTabToggledOn;

		// ---- Chip view / edit state ----
		// At the bottom of the stack is the chip that currently is being edited. 
		// If chips are entered in view mode, they will be placed above on the stack.
		public readonly Stack<DevChipInstance> chipViewStack = new();

		// Chips opened for editing are kept in memory (keyed by name) so you can navigate freely between
		// them WITHOUT saving and without losing unsaved work. The simulation resolves sub-chips to these
		// live (possibly unsaved) versions via chipLibrary.SimOverride, so unsaved edits apply everywhere.
		readonly Dictionary<string, DevChipInstance> openChips = new(ChipDescription.NameComparer);

		SimChip ViewedSimChip => ViewedChip.SimChip;

		// The chip currently in view. This chip may be in view-only mode.
		public DevChipInstance ViewedChip => chipViewStack.Peek();
		public bool CanEditViewedChip => chipViewStack.Count == 1;
		public string ActiveDevChipName => ViewedChip.ChipName;

		public bool ChipHasBeenSavedBefore => ViewedChip.LastSavedDescription != null;

		// ---- Navigation history (mouse back / forward buttons) ----
		// Every chip the user lands on (bottom bar, OPEN, search, a new chip once named) is appended; the
		// mouse "back" button steps to the previous one, "forward" to the next, like a browser.
		readonly List<string> navHistory = new();
		int navIndex = -1;
		bool navigating;

		// The chip currently being edited. (This is not necessarily the currently viewed chip)
		DevChipInstance editModeChip;
		public AudioState audioState;

		// ---- Simulation settings and state ----
		static readonly bool debug_logSimTime = false;
		static readonly bool debug_runSimMainThread = false;
		public const float SimulationPerformanceTimeWindowSec = 1.5f;

		bool simThreadActive;
		public bool advanceSingleSimStep;
		public int simPausedSingleStepCounter;
		int mainThreadFrameCount;
		DevPinInstance[] inputPins = Array.Empty<DevPinInstance>();
		public int targetTicksPerSecond => Mathf.Max(1, description.Prefs_SimTargetStepsPerSecond);
		public int stepsPerClockTransition => description.Prefs_SimStepsPerClockTick;
		public bool simPaused => description.Prefs_SimPaused;
		public double simAvgTicksPerSec { get; private set; }
		public SimChip rootSimChip => editModeChip.SimChip;

		public Project(ProjectDescription description, ChipLibrary chipLibrary)
		{
			ActiveProject = this;
			this.description = description;
			this.chipLibrary = chipLibrary;
			chipLibrary.SimOverride = LiveDescForSim;
			SearchPopup.ClearRecentChips();
		}

		public void Update()
		{
			HandleProjectInput();

			if (UIDrawer.ActiveMenu is UIDrawer.MenuType.None or UIDrawer.MenuType.BottomBarMenuPopup)
			{
				controller.Update();
			}

			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.None)
			{
				Simulator.UpdateKeyboardInputFromMainThread();
			}

			inputPins = editModeChip.GetInputPins();
			mainThreadFrameCount++;

			if (debug_runSimMainThread)
			{
				Debug_RunMainThreadSimStep();
			}
		}

		// Bench hook: what Update() does for the simulation (publish the input pins, advance the frame count so
		// the sim thread syncs the dev pins) without any UI or input handling.
		public void TickMainThreadForTests()
		{
			inputPins = editModeChip.GetInputPins();
			mainThreadFrameCount++;
		}

		public void StartSimulation()
		{
			if (debug_runSimMainThread)
			{
				Debug.Log("Simulation will run on main thread");
				return;
			}

			simThreadActive = true;
			Thread simThread = new(SimThread)
			{
				Priority = System.Threading.ThreadPriority.Highest,
				Name = "DLS_SimThread",
				IsBackground = true
			};
			simThread.Start();
		}

		public bool AlwaysDrawDevPinNames => AlwaysDrawPinNames(description.Prefs_MainPinNamesDisplayMode);
		public bool AlwaysDrawSubChipPinNames => AlwaysDrawPinNames(description.Prefs_ChipPinNamesDisplayMode);

		bool AlwaysDrawPinNames(int prefIndex) => prefIndex == PreferencesMenu.DisplayMode_Always || (prefIndex == PreferencesMenu.DisplayMode_TabToggle && PinNameDisplayIsTabToggledOn);

		// Mouse back / forward buttons: previous / next chip in the navigation history. Called by Main BEFORE the
		// camera update of the frame, so the chip we land on is drawn with its own view straight away (a switch
		// made later in the frame would be drawn once with the previous chip's zoom and position).
		public void HandleNavigationInput()
		{
			if (UIDrawer.ActiveMenu != UIDrawer.MenuType.None || KeyboardShortcuts.TextInputActive) return;
			if (InputHelper.IsKeyDownThisFrame(KeyCode.Mouse3)) NavigateHistory(-1);
			if (InputHelper.IsKeyDownThisFrame(KeyCode.Mouse4)) NavigateHistory(+1);
		}

		void HandleProjectInput()
		{
			if (UIDrawer.ActiveMenu is UIDrawer.MenuType.None)
			{
				// Step to next simulation frame when paused
				if (simPaused && KeyboardShortcuts.SimNextStepShortcutTriggered)
				{
					advanceSingleSimStep = true;
				}

				if (InputHelper.IsKeyDownThisFrame(KeyCode.Tab))
				{
					PinNameDisplayIsTabToggledOn = !PinNameDisplayIsTabToggledOn;
				}
			}


			PreferencesMenu.HandleKeyboardShortcuts();
		}


		static bool SameSubChips(SimChip live, ChipDescription desc)
		{
			if (desc.SubChips == null || live.SubChips.Length != desc.SubChips.Length) return false;
			var ids = new HashSet<int>();
			foreach (SimChip c in live.SubChips) if (c != null) ids.Add(c.ID);
			foreach (SubChipDescription s in desc.SubChips) if (!ids.Contains(s.ID)) return false;
			return true;
		}

		public void SaveFromDescription(ChipDescription saveChipDescription, SaveMode saveMode = SaveMode.Normal)
		{
			ChipDescription oldSavedBaseline = ViewedChip.LastSavedDescription; // for reconciling open parents

			// The memory state (latches, RAM...) is saved with the chip, edited or not: captured from the live simulation
			// of the chip being saved (only when that simulation is indeed this description's: same sub-chip IDs).
			SimChip live = editModeChip?.SimChip;
			if (live != null && SameSubChips(live, saveChipDescription))
				RunWithSimulationPaused(() => saveChipDescription.MemoryState = MemorySnapshot.Capture(live));

			// If this chip hasn't been saved before, it can't have been used anyway so no need to update anything
			// (same thing if saving a new version of it)
			if (ViewedChip.LastSavedDescription != null && saveMode != SaveMode.SaveAs)
			{
				UpdateAndSaveAffectedChips(ViewedChip.LastSavedDescription, saveChipDescription, false);
			}

			if (saveMode is SaveMode.Rename)
			{
				string nameOld = ViewedChip.LastSavedDescription.Name;
				Saver.DeleteChip(nameOld, description.ProjectName, false);
				Saver.SaveChip(saveChipDescription, description.ProjectName);
				chipLibrary.NotifyChipRenamed(saveChipDescription, nameOld);
				RenameStarred(saveChipDescription.Name, nameOld, false, false);
				EnsureChipRenamedInCollections(nameOld, saveChipDescription.Name);
				UpdateAndSaveProjectDescription();
			}
			else
			{
				Saver.SaveChip(saveChipDescription, description.ProjectName);

				chipLibrary.NotifyChipSaved(saveChipDescription);
				bool isNewChip = !ChipHasBeenSavedBefore || saveMode is SaveMode.SaveAs;

				// New chips are automatically starred
				if (isNewChip)
				{
					SetStarred(saveChipDescription.Name, true, false, false);
					UpdateAndSaveProjectDescription();
				}
			}

			// Notify the chip itself that it has been saved
			ViewedChip.NotifySaved(saveChipDescription);
			SearchPopup.AddRecentChip(saveChipDescription.Name);
			CameraController.NotifyChipNameChanged(saveChipDescription.Name);

			// Keep the open-chips registry keyed by the current name (handles new chips + renames).
			if (editModeChip != null && !string.IsNullOrEmpty(saveChipDescription.Name))
			{
				foreach (string k in openChips.Where(kv => kv.Value == editModeChip).Select(kv => kv.Key).ToArray()) openChips.Remove(k);
				openChips[saveChipDescription.Name] = editModeChip;
				RecordVisit(saveChipDescription.Name);
			}

			ReconcileOpenChipsAfterSave(saveChipDescription.Name, oldSavedBaseline, saveChipDescription);

			// The user saved: persist the Ask Claude conversation alongside the project so it can be resumed.
			AskClaude.SaveForProject(description.ProjectName);
		}

		public bool ActiveChipHasUnsavedChanges()
		{
			// If chip has no last saved description, then has unsaved changes if any elements have been placed inside it
			if (ViewedChip.LastSavedDescription == null)
			{
				return ViewedChip.Elements.Count > 0;
			}

			if (ViewedChip.MemoryEdited) return true;
			return Saver.HasUnsavedChanges(ViewedChip.LastSavedDescription, DescriptionCreator.CreateChipDescription(ViewedChip));
		}

		public void CreateBlankDevChip()
		{
			controller = new ChipInteractionController(this);
			DevChipInstance devChip = new();
			devChip.SetSimChip(new SimChip());
			SetNewActiveDevChip(devChip);
		}

		// "New chip": remembers where the user was, so that cancelling the name prompt brings them back there
		// instead of leaving them on an empty, nameless chip.
		DevChipInstance chipBeforeNewChip;

		public void BeginNewChip()
		{
			chipBeforeNewChip = editModeChip != null && (editModeChip.LastSavedDescription != null || editModeChip.Elements.Count > 0) ? editModeChip : null;
			CreateBlankDevChip();
		}

		// Called when the name prompt of a new chip is cancelled. Returns to the previous chip if the new one
		// is still blank (nothing placed, never saved); otherwise leaves things as they are.
		public void CancelNewChip()
		{
			DevChipInstance previous = chipBeforeNewChip;
			chipBeforeNewChip = null;
			if (previous == null || editModeChip == null) return;
			if (editModeChip.LastSavedDescription != null || editModeChip.Elements.Count > 0) return;

			ActivateEditChip(previous);
		}

		public void LoadDevChipOrCreateNewIfDoesntExist(string chipName)
		{
			// Already open in memory? Reuse it so unsaved edits are preserved (free navigation).
			if (openChips.TryGetValue(chipName, out DevChipInstance already))
			{
				ActivateEditChip(already);
				return;
			}

			if (chipLibrary.TryGetChipDescription(chipName, out ChipDescription description))
			{
				(DevChipInstance devChip, bool anyElementFailedToLoad) = DevChipInstance.LoadFromDescriptionTest(description, chipLibrary);
				openChips[chipName] = devChip;
				ActivateEditChip(devChip);
				if (anyElementFailedToLoad) SaveChipInstance(devChip); // resave migrated version
			}
			else
			{
				CreateBlankDevChip();
			}
		}

		// Make devChip the edited chip and (re)build its simulation from its LIVE description so it reflects
		// unsaved edits — in itself and (via SimOverride) in its sub-chips.
		void ActivateEditChip(DevChipInstance devChip)
		{
			// A fresh controller has an empty selection: elements still flagged as selected from a previous
			// visit would be skipped by the renderer (they are drawn from the selection list), i.e. invisible.
			ClearSelectionFlags(editModeChip);
			ClearSelectionFlags(devChip);

			controller = new ChipInteractionController(this);
			editModeChip = devChip;
			chipViewStack.Clear();
			chipViewStack.Push(devChip);

			// Rebuild only the SIM from the live description (sub-chips resolved live) — the DevChipInstance
			// itself is reused so its undo history survives the switch.
			devChip.SetSimChip(Simulator.BuildSimChip(DescriptionCreator.CreateChipDescription(devChip), chipLibrary));

			if (devChip.LastSavedDescription != null)
			{
				SearchPopup.AddRecentChip(devChip.LastSavedDescription.Name);
				RecordVisit(devChip.LastSavedDescription.Name);
			}

		}

		void RecordVisit(string chipName)
		{
			if (navigating || string.IsNullOrEmpty(chipName)) return;
			if (navIndex >= 0 && navIndex < navHistory.Count && ChipDescription.NameMatch(navHistory[navIndex], chipName)) return;
			if (navIndex < navHistory.Count - 1) navHistory.RemoveRange(navIndex + 1, navHistory.Count - navIndex - 1); // a new visit drops the "forward" branch
			navHistory.Add(chipName);
			navIndex = navHistory.Count - 1;
		}

		// dir = -1 (back) / +1 (forward). Entries whose chip no longer exists are skipped.
		void NavigateHistory(int dir)
		{
			for (int i = navIndex + dir; i >= 0 && i < navHistory.Count; i += dir)
			{
				string name = navHistory[i];
				if (!openChips.ContainsKey(name) && !chipLibrary.HasChip(name)) continue;
				if (editModeChip != null && editModeChip.LastSavedDescription != null && ChipDescription.NameMatch(editModeChip.LastSavedDescription.Name, name)) { navIndex = i; continue; }

				navIndex = i;
				navigating = true;
				try { LoadDevChipOrCreateNewIfDoesntExist(name); }
				finally { navigating = false; }
				return;
			}
		}

		// ---- Undo / redo: always on the chip being edited. Ctrl+Z never changes chip (the mouse back /
		// forward buttons are the only way to move through the chip history).
		public void GlobalUndo() => editModeChip?.UndoController.TryUndo();
		public void GlobalRedo() => editModeChip?.UndoController.TryRedo();

		static void ClearSelectionFlags(DevChipInstance devChip)
		{
			if (devChip == null) return;
			foreach (IMoveable element in devChip.Elements) element.IsSelected = false;
		}

		void SetNewActiveDevChip(DevChipInstance devChip)
		{
			ClearSelectionFlags(editModeChip);
			editModeChip = devChip;
			chipViewStack.Clear();
			chipViewStack.Push(devChip);

			if (devChip.LastSavedDescription != null) SearchPopup.AddRecentChip(devChip.LastSavedDescription.Name);
		}

		// Live description of an open chip (for the simulation to resolve unsaved sub-chips).
		ChipDescription LiveDescForSim(string name) =>
			openChips.TryGetValue(name, out DevChipInstance dc) ? DescriptionCreator.CreateChipDescription(dc) : null;

		public bool IsDirty(DevChipInstance dc)
		{
			if (dc == null) return false;
			if (dc.LastSavedDescription == null) return dc.Elements.Count > 0;
			if (dc.MemoryEdited) return true;
			return Saver.HasUnsavedChanges(dc.LastSavedDescription, DescriptionCreator.CreateChipDescription(dc));
		}

		// A named chip is "dirty" if it is open in memory with unsaved changes (used for the * indicator).
		public bool IsChipDirty(string chipName) => openChips.TryGetValue(chipName, out DevChipInstance dc) && IsDirty(dc);

		// Any unsaved work anywhere (used for the quit / exit-project warning).
		public bool AnyUnsavedChanges()
		{
			if (IsDirty(editModeChip)) return true;
			foreach (DevChipInstance dc in openChips.Values) if (dc != editModeChip && IsDirty(dc)) return true;
			return false;
		}

		// Save ALL open chips that have unsaved changes at once.
		public void SaveAllOpenChips()
		{
			foreach (DevChipInstance dc in openChips.Values.ToArray())
			{
				if (dc == editModeChip) continue;
				if (dc.LastSavedDescription != null && IsDirty(dc)) SaveChipInstance(dc);
			}
			if (editModeChip != null && editModeChip.LastSavedDescription != null && IsDirty(editModeChip))
				SaveFromDescription(DescriptionCreator.CreateChipDescription(editModeChip));
		}

		// Simple save of one open chip (not the affected-chips propagation — that stays on the active chip's
		// SaveFromDescription path).
		void SaveChipInstance(DevChipInstance dc)
		{
			ChipDescription old = dc.LastSavedDescription;
			ChipDescription desc = DescriptionCreator.CreateChipDescription(dc);
			Saver.SaveChip(desc, description.ProjectName);
			chipLibrary.NotifyChipSaved(desc);
			dc.NotifySaved(desc);
			ReconcileOpenChipsAfterSave(desc.Name, old, desc);
		}

		// After saving a chip whose pins changed, remove now-dangling wires (to removed pins of that chip)
		// from every OPEN chip that uses it, so parents don't keep broken connections.
		void ReconcileOpenChipsAfterSave(string chipName, ChipDescription oldDesc, ChipDescription newDesc)
		{
			if (oldDesc == null || newDesc == null) return;
			var removed = new HashSet<int>();
			foreach (PinDescription p in oldDesc.InputPins) removed.Add(p.ID);
			foreach (PinDescription p in oldDesc.OutputPins) removed.Add(p.ID);
			foreach (PinDescription p in newDesc.InputPins) removed.Remove(p.ID);
			foreach (PinDescription p in newDesc.OutputPins) removed.Remove(p.ID);
			if (removed.Count == 0) return;

			foreach (DevChipInstance dc in openChips.Values)
			{
				var toDelete = new List<WireInstance>();
				foreach (WireInstance w in dc.Wires)
				{
					if (!w.IsFullyConnected) continue;
					if (WireHitsRemovedPin(w.SourcePin, chipName, removed) || WireHitsRemovedPin(w.TargetPin, chipName, removed)) toDelete.Add(w);
				}
				foreach (WireInstance w in toDelete) dc.DeleteWire(w);
			}
		}

		static bool WireHitsRemovedPin(PinInstance pin, string chipName, HashSet<int> removedIds)
			=> pin.parent is SubChipInstance sc && ChipDescription.NameMatch(sc.Description.Name, chipName) && removedIds.Contains(pin.Address.PinID);

		// Rename a chip (custom only). Updates the file, library, parents (refs), starred list & collections.
		public void RenameChip(string oldName, string newName)
		{
			if (string.IsNullOrWhiteSpace(newName)) return;
			for (int i = 0; i < navHistory.Count; i++) if (ChipDescription.NameMatch(navHistory[i], oldName)) navHistory[i] = newName;
			if (!chipLibrary.HasChip(oldName) || chipLibrary.IsBuiltinChip(oldName)) return;
			if (chipLibrary.HasChip(newName) && !ChipDescription.NameMatch(oldName, newName)) return; // name taken

			ChipDescription oldDesc = chipLibrary.GetChipDescription(oldName);
			// Rename keeps the SAVED content (never write the unsaved edits) so a rename doesn't clear the *.
			ChipDescription newDesc = Saver.CloneChipDescription(oldDesc);
			newDesc.Name = newName;

			UpdateAndSaveAffectedChips(oldDesc, newDesc, false);
			Saver.DeleteChip(oldName, description.ProjectName, false);
			Saver.SaveChip(newDesc, description.ProjectName);
			chipLibrary.NotifyChipRenamed(newDesc, oldName);
			RenameStarred(newName, oldName, false, false);
			EnsureChipRenamedInCollections(oldName, newName);
			UpdateAndSaveProjectDescription();

			// Keep the open instance's UNSAVED edits; just point its saved baseline at the renamed saved version.
			if (openChips.TryGetValue(oldName, out DevChipInstance dc))
			{
				openChips.Remove(oldName);
				openChips[newName] = dc;
				dc.LastSavedDescription = newDesc;
			}
			CameraController.NotifyChipNameChanged(newName);

			// Update open chips that REFERENCE the renamed chip so they don't keep the old (now-broken) name.
			foreach (string key in openChips.Keys.ToArray())
			{
				DevChipInstance inst = openChips[key];
				ChipDescription d = DescriptionCreator.CreateChipDescription(inst);
				bool changed = false;
				for (int i = 0; i < d.SubChips.Length; i++)
					if (ChipDescription.NameMatch(d.SubChips[i].Name, oldName)) { d.SubChips[i].Name = newName; changed = true; }
				if (!changed) continue;

				(DevChipInstance reloaded, _) = DevChipInstance.LoadFromDescriptionTest(d, chipLibrary, true);
				reloaded.LastSavedDescription = chipLibrary.HasChip(key) ? chipLibrary.GetChipDescription(key) : reloaded.LastSavedDescription;
				openChips[key] = reloaded;
				if (inst == editModeChip) editModeChip = reloaded;
			}

			if (editModeChip != null) ActivateEditChip(editModeChip); // refresh active view + sim
		}

		// Key chip has been bound to a different key, so simulation must be updated
		public void NotifyKeyChipBindingChanged(SubChipInstance keyChip, char newKey)
		{
			SimChip simChip = rootSimChip.GetSubChipFromID(keyChip.ID);
			simChip.InternalState[0] = newKey;
			keyChip.SetKeyChipActivationChar(newKey);
		}

		// Chip's pulse width has been changed, so simulation must be updated
		public void NotifyPulseWidthChanged(SubChipInstance chip, uint widthNew)
		{
			SimChip simChip = rootSimChip.GetSubChipFromID(chip.ID);
			simChip.InternalState[0] = widthNew;
			chip.InternalData[0] = widthNew;
		}

		// CLOCK stopped / restarted / clicked: [0] = stopped, [1] = level while stopped
		public void NotifyClockStateChanged(SubChipInstance clock)
		{
			SimChip simChip = rootSimChip.GetSubChipFromID(clock.ID);
			simChip.UpdateInternalState(clock.InternalData);
		}

		// Rom has been edited, so simulation must be updated
		public void NotifyRomContentsEdited(SubChipInstance romChip)
		{
			SimChip simChip = rootSimChip.GetSubChipFromID(romChip.ID);
			simChip.UpdateInternalState(romChip.InternalData);
		}

		public void NotifyLEDColourChanged(SubChipInstance ledChip, uint colIndex)
		{
			SimChip simChip = rootSimChip.GetSubChipFromID(ledChip.ID);
			simChip.InternalState[0] = colIndex;
			ledChip.InternalData[0] = colIndex;
		}

		public void DeleteChip(string chipToDeleteName)
		{
			// If the current chip only contains the deleted chip directly as a subchip, it will be removed from the sim and everything is fine.
			// However, if it is contained indirectly somewhere within one of the chip's subchips (or their subchips, etc), then it's a bit tricky (and
			// potentially expensive for large chips) to hunt down all references within the simulation and remove them. So, for now at least, simply
			// restart the simulation in this case (this is not ideal though, since state of latches etc will be lost)
			bool simReloadRequired = ChipContainsSubchipIndirectly(ViewedChip, chipToDeleteName);

			if (ChipContainsSubChipDirectly(ViewedChip, chipToDeleteName))
			{
				// if deleted chip is a subchip of the current chip, clear undo history as it may now be invalid
				// (Todo: maybe handle more gracefully...)
				ViewedChip.UndoController.Clear();
			}


			// Tolerate a chip that is starred / on disk but missing from the library: it must still be
			// removable rather than throwing (which looked, from the app, like the button doing nothing).
			if (chipLibrary.TryGetChipDescription(chipToDeleteName, out ChipDescription descriptionToDelete))
			{
				UpdateAndSaveAffectedChips(descriptionToDelete, null, true);
			}

			// Delete chip save file, remove from library, and update project description
			Saver.DeleteChip(chipToDeleteName, description.ProjectName);
			chipLibrary.RemoveChip(chipToDeleteName);
			openChips.Remove(chipToDeleteName);
			SetStarred(chipToDeleteName, false, false, false); // ensure removed from starred list
			EnsureChipRemovedFromCollections(chipToDeleteName);
			UpdateAndSaveProjectDescription();


			// The deleted chip leaves the navigation history
			navHistory.RemoveAll(n => ChipDescription.NameMatch(n, chipToDeleteName));
			navIndex = Mathf.Min(navIndex, navHistory.Count - 1);

			// If has deleted the chip that's currently being edited: go back to the last chip visited that
			// still exists (rather than to a blank, nameless chip); a blank chip only if there is none.
			if (ChipDescription.NameMatch(ViewedChip.ChipName, chipToDeleteName))
			{
				string fallback = null;
				for (int i = navIndex; i >= 0 && fallback == null; i--)
					if (openChips.ContainsKey(navHistory[i]) || chipLibrary.HasChip(navHistory[i])) fallback = navHistory[i];

				if (fallback != null)
				{
					navigating = true;
					try { LoadDevChipOrCreateNewIfDoesntExist(fallback); }
					finally { navigating = false; }
				}
				else CreateBlankDevChip();
			}
			else
			{
				// Remove any instances of the deleted chip from the active chip
				ViewedChip.DeleteSubchipsByName(chipToDeleteName);
				if (simReloadRequired)
				{
					ViewedChip.RebuildSimulation();
				}
			}
		}

		// Test if chip's subchips (or any of their subchips, etc...) contain the target subchip
		bool ChipContainsSubchipIndirectly(DevChipInstance devChip, string targetSubchip)
		{
			HashSet<string> visited = new(ChipDescription.NameComparer);

			foreach (IMoveable element in devChip.Elements)
			{
				if (element is SubChipInstance subchip && visited.Add(subchip.Description.Name))
				{
					if (ChipContainsSubchipDirectlyOrIndirectly(chipLibrary.GetChipDescription(subchip.Description.Name), targetSubchip))
					{
						return true;
					}
				}
			}

			return false;
		}

		// Test if chip (or any of its subchips, or the subchips' subchips, etc...) contain the target subchip
		bool ChipContainsSubchipDirectlyOrIndirectly(ChipDescription descRoot, string targetSubchip)
		{
			HashSet<string> visited = new(ChipDescription.NameComparer);
			bool found = false;
			SearchRecursive(descRoot);
			return found;

			void SearchRecursive(ChipDescription desc)
			{
				foreach (SubChipDescription sub in desc.SubChips)
				{
					if (found || ChipDescription.NameMatch(sub.Name, targetSubchip))
					{
						found = true;
						return;
					}

					ChipDescription subDescFull = chipLibrary.GetChipDescription(sub.Name);
					if (visited.Add(subDescFull.Name)) // Hasn't visited before
					{
						SearchRecursive(subDescFull);
					}
				}
			}
		}

		bool ChipContainsSubChipDirectly(DevChipInstance chip, string targetName)
		{
			foreach (IMoveable element in chip.Elements)
			{
				if (element is SubChipInstance s && ChipDescription.NameMatch(s.Description.Name, targetName))
				{
					return true;
				}
			}

			return false;
		}

		// Must be called prior to library being updated with the change
		// If deleting, new description can be left null
		void UpdateAndSaveAffectedChips(ChipDescription root_desc, ChipDescription root_descNew, bool willDelete)
		{
			// There a few ways in which chips other than the one currently being edited can be affected, and require resaving:
			// -- A chip is deleted from the library -> all chips that contain the deleted chip must be resaved with that chip and its connections removed
			// -- A chip is renamed -> all chips containing the renamed chip must be resaved with the new name applied
			// -- A chip is saved after removing an input/output pin -> all chips contained the edited chip must be resaved with affected connections removed


			ChipDescription[] affectedChips = chipLibrary.GetDirectParentChips(root_desc.Name);
			bool willRename = !willDelete && !ChipDescription.NameMatch(root_desc.Name, root_descNew.Name);

			HashSet<int> newDesc_AllDevPinIDs = new();
			if (!willDelete)
			{
				foreach (PinDescription p in root_descNew.InputPins) newDesc_AllDevPinIDs.Add(p.ID);
				foreach (PinDescription p in root_descNew.OutputPins) newDesc_AllDevPinIDs.Add(p.ID);
			}

			foreach (ChipDescription desc in affectedChips)
			{
				bool anyChanges = willDelete | willRename;


				(DevChipInstance devChip, bool anyElementFailedToLoad) = DevChipInstance.LoadFromDescriptionTest(desc, chipLibrary);

				anyChanges |= anyElementFailedToLoad;

				if (willDelete)
				{
					devChip.DeleteSubchipsByName(root_desc.Name);
				}
				else
				{
					// Detect deleted dev pins, and remove any connections to the corresponding subchip pins in the affected chip
					foreach (PinDescription p in root_desc.InputPins)
					{
						if (!newDesc_AllDevPinIDs.Contains(p.ID)) anyChanges |= devChip.DeleteWiresAttachedToPinOfSubChip(p.ID);
					}

					foreach (PinDescription p in root_desc.OutputPins)
					{
						if (!newDesc_AllDevPinIDs.Contains(p.ID)) anyChanges |= devChip.DeleteWiresAttachedToPinOfSubChip(p.ID);
					}
				}

				if (anyChanges)
				{
					ChipDescription updatedDesc = DescriptionCreator.CreateChipDescription(devChip);

					if (willRename)
					{
						for (int i = 0; i < updatedDesc.SubChips.Length; i++)
						{
							if (ChipDescription.NameMatch(updatedDesc.SubChips[i].Name, root_desc.Name))
							{
								updatedDesc.SubChips[i].Name = root_descNew.Name;
							}
						}
					}

					Saver.SaveChip(updatedDesc, this.description.ProjectName);
					chipLibrary.NotifyChipSaved(updatedDesc);
				}
			}
		}


		public void ToggleGridDisplay()
		{
			description.Prefs_GridDisplayMode = 1 - description.Prefs_GridDisplayMode;
		}

		public bool ShouldSnapToGrid => KeyboardShortcuts.SnapModeHeld || (description.Prefs_Snapping == 1 && ShowGrid) || description.Prefs_Snapping == 2;
		public bool ForceStraightWires => KeyboardShortcuts.StraightLineModeHeld || (description.Prefs_StraightWires == 1 && ShowGrid) || description.Prefs_StraightWires == 2;

		public void NotifyExit()
		{
			simThreadActive = false;
		}

		// ---- External pause handshake (lets the main thread safely use the static Simulator) ----
		public volatile bool simPauseForExternalRequest;
		public volatile bool simIsPausedForExternal;

		// Runs `action` on the calling thread with the sim thread parked, so it can use the static
		// Simulator (e.g. compute a truth table) without racing it. Restores nothing itself — the
		// caller is responsible for saving/restoring any Simulator state it disturbs.
		public void RunWithSimulationPaused(Action action)
		{
			if (!simThreadActive)
			{
				action();
				return;
			}

			simPauseForExternalRequest = true;
			Stopwatch sw = Stopwatch.StartNew();
			while (!simIsPausedForExternal && sw.ElapsedMilliseconds < 500) Thread.SpinWait(200);
			try { action(); }
			finally { simPauseForExternalRequest = false; }
		}

		void SimThread()
		{
			const int performanceTimeWindowMs = (int)(SimulationPerformanceTimeWindowSec * 1000);
			long perfWindowStartMs = 0, perfStepsInWindow = 0;
			int simLastMainThreadSyncFrame = -1;

			Stopwatch stopwatch = new();
			Stopwatch stopwatchTotal = Stopwatch.StartNew();

			while (simThreadActive)
			{
				// Park the sim thread while an external request (e.g. truth-table computation) uses the Simulator.
				if (simPauseForExternalRequest)
				{
					simIsPausedForExternal = true;
					Thread.Sleep(1);
					continue;
				}
				simIsPausedForExternal = false;

				Simulator.ApplyModifications();
				// ---- A new frame has been reached on main thread  ----
				if (mainThreadFrameCount > simLastMainThreadSyncFrame)
				{
					simLastMainThreadSyncFrame = mainThreadFrameCount;
					// Update graphical state from sim
					// Note: update graphical state even when paused so that subchips are automatically if viewed
					ViewedChip.UpdateStateFromSim(ViewedSimChip, !CanEditViewedChip);

					// Log sim time
					if (debug_logSimTime)
					{
						double elapsedMs = stopwatchTotal.ElapsedTicks * (1000.0 / Stopwatch.Frequency);
						int frame = Simulator.simulationFrame;
						if (frame > 0) UnityEngine.Debug.Log($"Avg sim step time: {elapsedMs / frame} ms NumSteps: {frame} secs: {elapsedMs / 1000.0:0.00}");
					}
				}

				// If sim is paused, sleep a bit and then check again
				// Also handle advancing a single step
				if (simPaused && !advanceSingleSimStep)
				{
					Simulator.UpdateInPausedState();
					stopwatchTotal.Stop();
					Thread.Sleep(10);
					continue;
				}

				if (advanceSingleSimStep)
				{
					simPausedSingleStepCounter++;
					advanceSingleSimStep = false;
				}
				else simPausedSingleStepCounter = 0;

				double targetTickDurationMs = 1000.0 / targetTicksPerSecond;
				stopwatch.Restart();
				if (!stopwatchTotal.IsRunning) stopwatchTotal.Start();

				// ---- Run sim ----
				Simulator.stepsPerClockTransition = stepsPerClockTransition;
				SimChip simChip = rootSimChip;
				if (simChip == null) continue; // Could potentially be null for a frame when switching between chips
				// Paced (a real target rate) or single-stepping: one step at a time. Unbounded: batches, idle steps skipped.
				bool paced = targetTicksPerSecond < 100000 || advanceSingleSimStep || simPausedSingleStepCounter > 0;
				int stepsDone = paced ? 1 : 0;
				if (paced) Simulator.RunSimulationStep(simChip, inputPins, audioState.simAudio);
				else stepsDone = Simulator.RunSimulationSteps(simChip, inputPins, audioState.simAudio, 256);

				// ---- Wait some amount of time (if needed) to try to hit the target ticks per second ----
				while (true)
				{
					double elapsedMs = stopwatch.ElapsedTicks * (1000.0 / Stopwatch.Frequency);
					double waitMs = targetTickDurationMs - elapsedMs;

					if (waitMs <= 0) break;

					// Wait some cycles before checking timer again (todo: better approach?)
					Thread.SpinWait(10);
				}

				// ---- Update perf counter (average steps per second over the last window) ----
				perfStepsInWindow += stepsDone;
				long elapsedMsTotal = stopwatchTotal.ElapsedMilliseconds;
				long windowMs = elapsedMsTotal - perfWindowStartMs;
				if (windowMs >= performanceTimeWindowMs / 3)
				{
					simAvgTicksPerSec = perfStepsInWindow / (double)windowMs * 1000;
					perfWindowStartMs = elapsedMsTotal;
					perfStepsInWindow = 0;
				}
			}
		}

		void Debug_RunMainThreadSimStep()
		{
			Simulator.stepsPerClockTransition = stepsPerClockTransition;
			Simulator.ApplyModifications();
			Simulator.RunSimulationStep(rootSimChip, inputPins, audioState.simAudio);
			ViewedChip.UpdateStateFromSim(ViewedSimChip, !CanEditViewedChip);
		}

		public void UpdateAndSaveProjectDescription()
		{
			ProjectDescription newDesc = description;
			newDesc.AllCustomChipNames = chipLibrary.GetAllCustomChipNames();
			UpdateAndSaveProjectDescription(newDesc);
		}

		public void UpdateAndSaveProjectDescription(ProjectDescription editedProjectDesc)
		{
			description = editedProjectDesc;
			SaveCurrentProjectDescription();
		}

		public void SaveCurrentProjectDescription()
		{
			Saver.SaveProjectDescription(description);
		}

		public void RenameCollection(int collectionIndex, string nameNew, bool autoSave = true)
		{
			ChipCollection collection = description.ChipCollections[collectionIndex];
			RenameStarred(nameNew, collection.Name, true, false);
			collection.Name = nameNew;
			collection.UpdateDisplayStrings();

			if (autoSave) SaveCurrentProjectDescription();
		}

		// Rename starred chip/collection (if is starred)
		public void RenameStarred(string nameNew, string nameOld, bool isCollection, bool autoSave = true)
		{
			List<StarredItem> starred = description.StarredList;
			bool modified = false;
			for (int i = 0; i < starred.Count; i++)
			{
				if (starred[i].IsCollection == isCollection && ChipDescription.NameMatch(starred[i].Name, nameOld))
				{
					starred[i] = new StarredItem(nameNew, isCollection);
					modified = true;
					break;
				}
			}

			if (autoSave && modified) SaveCurrentProjectDescription();
		}

		public void SetStarred(string chipName, bool star, bool isCollection, bool autoSave = true)
		{
			List<StarredItem> starred = description.StarredList;
			bool alreadyStarred = false;
			bool modified = false;

			for (int i = 0; i < starred.Count; i++)
			{
				if (starred[i].IsCollection == isCollection && ChipDescription.NameMatch(chipName, starred[i].Name))
				{
					if (!star)
					{
						modified = true;
						starred.RemoveAt(i);
					}

					alreadyStarred = true;
					break;
				}
			}

			if (star && !alreadyStarred)
			{
				modified = true;
				starred.Add(new StarredItem(chipName, isCollection));
			}

			if (autoSave && modified) SaveCurrentProjectDescription();
		}

		void EnsureChipRenamedInCollections(string chipNameOld, string chipNameNew)
		{
			foreach (ChipCollection collection in description.ChipCollections)
			{
				for (int i = 0; i < collection.Chips.Count; i++)
				{
					if (ChipDescription.NameMatch(collection.Chips[i], chipNameOld))
					{
						collection.Chips[i] = chipNameNew;
						return;
					}
				}
			}
		}

		void EnsureChipRemovedFromCollections(string chipNameToRemove)
		{
			foreach (ChipCollection collection in description.ChipCollections)
			{
				for (int i = 0; i < collection.Chips.Count; i++)
				{
					if (ChipDescription.NameMatch(collection.Chips[i], chipNameToRemove))
					{
						collection.Chips.RemoveAt(i);
						return;
					}
				}
			}
		}
	}
}