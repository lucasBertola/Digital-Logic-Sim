using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class ContextMenu
	{
		const int pad = 10;
		const int NandOnlyWarnThreshold = 800; // above this, confirm before rebuilding (layout + sim get heavy)
		const string menuDividerString = "#--#";
		static string interactionContextName;
		static bool bottomBarItemIsCollection;

		// Whether the right-clicked bottom-bar chip is a NAND-only combinational circuit (so it can be
		// minimised). Computed once when the menu opens: the entry's IsEnabled runs every frame.
		static bool bottomBarChipIsReducible;
		static Vector2 mouseOpenMenuPos;

		static MenuEntry[] activeContextMenuEntries;
		static readonly MenuEntry dividerMenuEntry = new(menuDividerString, (Action)null, null);
		static bool wasMouseOverMenu;
		static string contextMenuHeader;
		static int openSubmenuIndex = -1;  // entry whose sub-menu is shown (hover opens it, hovering another entry closes it)
		static Bounds2D openSubmenuAnchor; // bounds of that entry's button

		static readonly MenuEntry[] pinColEntries = ((PinColour[])Enum.GetValues(typeof(PinColour))).Select(col =>
			new MenuEntry(Format(Enum.GetName(typeof(PinColour), col)), () => SetCol(col), CanSetCol)
		).ToArray();


		static readonly MenuEntry setColourEntry = new(Format("SET COLOUR"), pinColEntries, CanSetCol);
		static readonly MenuEntry deleteEntry = new(Format("DELETE"), Delete, CanDelete);
		static readonly MenuEntry openChipEntry = new(Format("OPEN"), OpenChip, CanOpenChip);
		static readonly MenuEntry duplicateChipEntry = new(Format("DUPLICATE"), DuplicateChip, IsCustomChip);
		static readonly MenuEntry labelChipEntry = new(Format("RENAME"), OpenChipLabelPopup, CanLabelChip);
		static readonly MenuEntry displayNameEntry = new(() => Format(DisplayNameIsOn() ? "HIDE NAME" : "DISPLAY NAME"), ToggleDisplayName, CanLabelChip);
		static bool DisplayNameIsOn() => interactionContext is SubChipInstance sc && sc.ShowLabelOnChip && !string.IsNullOrWhiteSpace(sc.Label);

		static readonly MenuEntry[] entries_customSubchip =
		{
			openChipEntry,
			new(Format("EDIT MEMORY"), () => UIDrawer.SetActiveMenu(UIDrawer.MenuType.MemoryEdit), CanEditCurrentChip),
			duplicateChipEntry,
			labelChipEntry,
			displayNameEntry,
			deleteEntry
		};

		static readonly MenuEntry[] entries_builtinSubchip =
		{
			labelChipEntry,
			displayNameEntry,
			deleteEntry
		};

		static readonly MenuEntry[] entries_builtinLED = entries_builtinSubchip.Concat(new[] { setColourEntry }).ToArray();

		static readonly MenuEntry[] entries_builtinBus =
		{
			new(Format("FLIP"), FlipBus, CanFlipBus),
			labelChipEntry,
			deleteEntry
		};

		static readonly MenuEntry[] entries_builtinKeySubchip =
		{
			new(Format("REBIND"), OpenKeyBindMenu, CanEditCurrentChip),
			labelChipEntry,
			displayNameEntry,
			deleteEntry
		};

		static readonly MenuEntry[] entries_builtinRomSubchip =
		{
			new(Format("EDIT"), OpenRomEditMenu, CanEditCurrentChip),
			labelChipEntry,
			displayNameEntry,
			deleteEntry
		};

		static readonly MenuEntry[] entries_builtinPulseChip =
		{
			new(Format("EDIT"), OpenPulseEditMenu, CanEditCurrentChip),
			labelChipEntry,
			displayNameEntry,
			deleteEntry
		};


		static readonly MenuEntry[] entries_subChipOutput = { setColourEntry };

		static readonly MenuEntry[] entries_inputDevPin =
		{
			new(Format("RENAME"), OpenPinEditMenu, CanEditCurrentChip),
			new(Format("DELETE"), Delete, CanDelete),
			setColourEntry
		};

		static readonly MenuEntry[] entries_outputDevPin =
		{
			entries_inputDevPin[0],
			entries_inputDevPin[1]
		};

		// Several input/output pins selected, right-click on one of them
		static readonly MenuEntry[] entries_multiDevPin =
		{
			new(Format("RENAME"), RenameSelectedPins, CanEditCurrentChip),
			new(Format("DELETE"), DeleteSelectedElements, CanDelete)
		};

		// Several sub-chips selected, right-click on one of them (RENAME = their labels)
		static readonly MenuEntry[] entries_multiSubchip =
		{
			new(Format("RENAME"), RenameSelectedChips, CanEditCurrentChip),
			new(Format("DELETE"), DeleteSelectedElements, CanDelete)
		};

		static readonly MenuEntry[] entries_wire =
		{
			new(Format("EDIT"), EditWire, CanEditWire),
			new(Format("DELETE"), Delete, CanDelete)
		};

		static readonly MenuEntry countNandEntry = new(Format("COUNT NAND"), ShowNandCount, () => true);
		static readonly MenuEntry nandOnlyEntry = new(Format("NAND ONLY"), RecreateWithOnlyNand, CanRecreateWithOnlyNand);
		static readonly MenuEntry optimiseEntry = new(Format("OPTIMISE"), OptimiseWithChips, () => bottomBarChipIsReducible);

		static readonly MenuEntry[] entries_bottomBarChip =
		{
			openChipEntry,
			countNandEntry,
			nandOnlyEntry,
			optimiseEntry,
			new(Format("RENAME"), RenameBottomBarChip, CanDeleteBottomBarChip),
			duplicateChipEntry,
			new(Format("UN-STAR"), UnstarBottomBarEntry, () => true),
			new(Format("DELETE"), DeleteBottomBarChip, CanDeleteBottomBarChip)
		};

		static readonly MenuEntry[] entries_collectionPopupChip =
		{
			openChipEntry,
			duplicateChipEntry,
			countNandEntry,
			nandOnlyEntry,
			optimiseEntry
		};

		static readonly MenuEntry[] entries_bottomBarCollection =
		{
			new(Format("UN-STAR"), UnstarBottomBarEntry, () => true)
		};

		// Right-click on empty space in the scene
		static readonly MenuEntry[] entries_emptySpace =
		{
			new(Format("IN/OUT"), OpenInOutPopup, CanEditCurrentChip),
			new(Format("CLEAN UP"), BottomBarUI.CleanUp, CanEditCurrentChip),
			new(Format("ASK CLAUDE"), QuickAskBar.Open, CanEditCurrentChip)
		};

		public static bool IsOpen { get; private set; }
		public static IInteractable interactionContext { get; private set; }


		static string Format(string s)
		{
			s = char.ToUpper(s[0]) + s.Substring(1).ToLower();
			return s.PadRight(pad);
		}

		public static void Update()
		{
			bool inMenu = !(UIDrawer.ActiveMenu is UIDrawer.MenuType.None or UIDrawer.MenuType.BottomBarMenuPopup or UIDrawer.MenuType.ChipCustomization);
			if (inMenu)
			{
				CloseContextMenu();
			}
			else
			{
				HandleOpenMenuInput();

				// Draw
				if (IsOpen) DrawContextMenu(activeContextMenuEntries);

				// Close menu input
				if (InputHelper.IsMouseDownThisFrame(MouseButton.Left) || KeyboardShortcuts.CancelShortcutTriggered)
				{
					CloseContextMenu();
				}
			}
		}

		static void HandleOpenMenuInput()
		{
			// Open menu input
			if (InputHelper.IsMouseDownThisFrame(MouseButton.Right) && !KeyboardShortcuts.CameraActionKeyHeld && !InteractionState.MouseIsOverUI)
			{
				bool inCustomizeMenu = UIDrawer.ActiveMenu == UIDrawer.MenuType.ChipCustomization;
				IInteractable hoverElement = InteractionState.ElementForContextMenu;

				bool openSubChipContextMenu = hoverElement is SubChipInstance && !inCustomizeMenu;
				bool openDevPinContextMenu = (hoverElement is PinInstance pin && pin.parent is DevPinInstance) || hoverElement is DevPinInstance;
				bool openWireContextMenu = hoverElement is WireInstance;
				bool openSubchipOutputPinContextMenu = hoverElement is PinInstance pin2 && pin2.parent is SubChipInstance && pin2.IsSourcePin && !pin2.IsBusPin;

				if (openSubChipContextMenu || openDevPinContextMenu || openWireContextMenu || openSubchipOutputPinContextMenu)
				{
					interactionContextName = string.Empty;
					interactionContext = hoverElement;
					string headerName = string.Empty;

					List<IMoveable> currentSelection = Project.ActiveProject.controller.SelectedElements;
					int selectedChipCount = currentSelection.Count(e => e is SubChipInstance);

					if (openSubChipContextMenu && ((SubChipInstance)hoverElement).IsSelected && selectedChipCount >= 2)
					{
						// Keep the multi-selection: the menu acts on all selected chips
						SubChipInstance subChip = (SubChipInstance)hoverElement;
						interactionContextName = subChip.Description.Name;
						headerName = $"{selectedChipCount} CHIPS";
						activeContextMenuEntries = entries_multiSubchip;
					}
					else if (openSubChipContextMenu)
					{
						SubChipInstance subChip = (SubChipInstance)hoverElement;
						interactionContextName = subChip.Description.Name;

						if (subChip.ChipType == ChipType.Custom)
						{
							headerName = subChip.Description.Name;
							activeContextMenuEntries = entries_customSubchip;
						}
						else // builtin type
						{
							headerName = ChipTypeHelper.IsBusType(subChip.ChipType) ? "BUS" : subChip.Description.Name;
							if (subChip.ChipType is ChipType.Key) activeContextMenuEntries = entries_builtinKeySubchip;
							else if (ChipTypeHelper.IsRomType(subChip.ChipType)) activeContextMenuEntries = entries_builtinRomSubchip;
							else if (subChip.ChipType is ChipType.Pulse) activeContextMenuEntries = entries_builtinPulseChip;
							else if (ChipTypeHelper.IsBusType(subChip.ChipType)) activeContextMenuEntries = entries_builtinBus;
							else if (subChip.ChipType == ChipType.DisplayLED) activeContextMenuEntries = entries_builtinLED;
							else activeContextMenuEntries = entries_builtinSubchip;
						}

						Project.ActiveProject.controller.Select(interactionContext as IMoveable, false);
					}
					else if (openDevPinContextMenu)
					{
						if (interactionContext is DevPinInstance devPinInstance) interactionContext = devPinInstance.Pin;

						PinInstance activePin = (PinInstance)interactionContext;
						headerName = CreatePinHeaderName(activePin.Name);
						interactionContextName = activePin.Name;

						List<IMoveable> selection = Project.ActiveProject.controller.SelectedElements;
						int selectedPinCount = selection.Count(e => e is DevPinInstance);
						if (activePin.parent.IsSelected && selectedPinCount >= 2)
						{
							// Keep the multi-selection: the menu acts on all selected pins
							headerName = $"{selectedPinCount} PINS";
							activeContextMenuEntries = entries_multiDevPin;
						}
						else
						{
							Project.ActiveProject.controller.Select(activePin.parent, false);
							activeContextMenuEntries = activePin.IsSourcePin ? entries_inputDevPin : entries_outputDevPin;
						}
					}
					else if (openWireContextMenu)
					{
						WireInstance wire = (WireInstance)interactionContext;
						if (wire.IsBusWire) headerName = "BUS LINE";
						else headerName = CreateWireHeaderString(wire);

						activeContextMenuEntries = entries_wire;
					}
					else if (openSubchipOutputPinContextMenu)
					{
						PinInstance pinContext = (PinInstance)interactionContext;
						headerName = CreatePinHeaderName(pinContext.Name);
						activeContextMenuEntries = entries_subChipOutput;
					}

					SetContextMenuOpen(headerName);
				}
				else if (hoverElement == null && UIDrawer.ActiveMenu == UIDrawer.MenuType.None && Project.ActiveProject.CanEditViewedChip)
				{
					interactionContextName = string.Empty;
					interactionContext = null;
					activeContextMenuEntries = entries_emptySpace;
					string chipName = Project.ActiveProject.ViewedChip.ChipName;
					SetContextMenuOpen(string.IsNullOrEmpty(chipName) ? "CHIP" : chipName);
				}
				else
				{
					CloseContextMenu();
				}
			}
		}

		static string CreateWireHeaderString(WireInstance wire)
		{
			string pinName = wire.SourcePin.Name;
			if (string.IsNullOrWhiteSpace(pinName)) return "WIRE";

			return "WIRE: " + pinName;
		}

		static string CreatePinHeaderName(string pinName)
		{
			if (string.IsNullOrWhiteSpace(pinName)) return "PIN";

			return "PIN: " + pinName;
		}

		public static void OpenBottomBarContextMenu(string name, bool isCollection, bool isFromInsideCollection)
		{
			interactionContextName = name;
			bottomBarItemIsCollection = isCollection;
			interactionContext = null;
			bottomBarChipIsReducible = !isCollection && NandMinimizer.CanOptimize(name, Project.ActiveProject.chipLibrary);
			SetContextMenuOpen(name);

			if (isCollection)
			{
				activeContextMenuEntries = entries_bottomBarCollection;
			}
			else
			{
				activeContextMenuEntries = isFromInsideCollection ? entries_collectionPopupChip : entries_bottomBarChip;
			}
		}

		static void SetContextMenuOpen(string header)
		{
			mouseOpenMenuPos = UI.ScreenToUISpace(InputHelper.MousePos);
			contextMenuHeader = header.PadRight(pad);
			openSubmenuIndex = -1;
			IsOpen = true;
		}


		static void DrawContextMenu(MenuEntry[] menuEntries)
		{
			Draw.StartLayer(Vector2.zero, 1, true);

			const float textOffsetX = 0.45f;
			ButtonTheme theme = DrawSettings.ActiveUITheme.MenuPopupButtonTheme;
			ButtonTheme headerTheme = DrawSettings.ActiveUITheme.MenuPopupButtonTheme;
			headerTheme.buttonCols.inactive = ColHelper.MakeCol(0.18f);
			headerTheme.textCols.inactive = Color.white;

			float menuWidth = Draw.CalculateTextBoundsSize(contextMenuHeader, theme.fontSize, theme.font).x + 1;
			foreach (MenuEntry entry in menuEntries)
			{
				if (entry.Text == menuDividerString) continue;
				menuWidth = Mathf.Max(menuWidth, Draw.CalculateTextBoundsSize(EntryLabel(entry), theme.fontSize, theme.font).x + 1);
			}

			Draw.ID panelID = UI.ReservePanel();
			Vector2 buttonSize = new(menuWidth, 2);


			Vector2 pos = mouseOpenMenuPos;
			if (pos.x + menuWidth > UI.Width)
			{
				pos.x = UI.Width - menuWidth;
			}

			bool expandDown = pos.y >= UI.Height * 0.35f;
			float dirY = expandDown ? -1 : 1;
			Anchor anchor = expandDown ? Anchor.TopLeft : Anchor.BottomLeft;

			using (UI.BeginBoundsScope(true))
			{
				for (int i = 0; i < menuEntries.Length; i++)
				{
					int index = expandDown ? i : menuEntries.Length - i - 1;
					MenuEntry entry = menuEntries[index];

					if (index == 0 && expandDown) DrawHeader();

					if (entry.Text == menuDividerString)
					{
						pos.y += 0.5f * dirY;
						UI.DrawPanel(pos, new Vector2(menuWidth, 0.15f), ColHelper.MakeCol(0.6f), Anchor.CentreLeft);
						pos.y += 0.5f * dirY;
					}
					else
					{
						bool enabled = entry.IsEnabled();
						if (UI.Button(EntryLabel(entry), theme, pos, buttonSize, enabled, false, false, anchor, true, textOffsetX))
						{
							entry.OnPress?.Invoke();
						}

						// Hovering an entry with a sub-menu opens it; hovering any other entry closes it
						if (UI.MouseInsideBounds(UI.PrevBounds))
						{
							if (entry.SubEntries != null && enabled)
							{
								openSubmenuIndex = index;
								openSubmenuAnchor = UI.PrevBounds;
							}
							else openSubmenuIndex = -1;
						}

						pos.y += buttonSize.y * dirY;
					}

					if (index == 0 && !expandDown) DrawHeader();
				}

				Bounds2D bounds = UI.GetCurrentBoundsScope();
				Vector2 menuSize = new(menuWidth, bounds.Height);
				UI.ModifyPanel(panelID, bounds.Centre, menuSize + Vector2.one * 0.5f, ColHelper.MakeCol(0.91f));
			}

			wasMouseOverMenu = UI.MouseInsideBounds(UI.PrevBounds);

			if (openSubmenuIndex >= 0 && openSubmenuIndex < menuEntries.Length && menuEntries[openSubmenuIndex].SubEntries != null)
			{
				DrawSubMenu(menuEntries[openSubmenuIndex].SubEntries, theme, textOffsetX);
			}

			// Second menu, beside the hovered entry (to its right, or to its left when there is no room)
			static void DrawSubMenu(MenuEntry[] subEntries, ButtonTheme theme, float textOffsetX)
			{
				float width = 0;
				foreach (MenuEntry e in subEntries) width = Mathf.Max(width, Draw.CalculateTextBoundsSize(e.Text, theme.fontSize, theme.font).x + 1);

				Vector2 buttonSize = new(width, 2);
				float totalHeight = buttonSize.y * subEntries.Length;
				bool right = openSubmenuAnchor.Right + 0.25f + width <= UI.Width;
				float x = right ? openSubmenuAnchor.Right + 0.25f : openSubmenuAnchor.Left - 0.25f - width;
				float top = Mathf.Clamp(openSubmenuAnchor.Top, totalHeight, UI.Height);
				Vector2 pos = new(x, top);

				Draw.ID panelID = UI.ReservePanel();
				using (UI.BeginBoundsScope(true))
				{
					foreach (MenuEntry e in subEntries)
					{
						if (UI.Button(e.Text, theme, pos, buttonSize, e.IsEnabled(), false, false, Anchor.TopLeft, true, textOffsetX))
						{
							e.OnPress?.Invoke();
						}

						pos.y -= buttonSize.y;
					}

					Bounds2D bounds = UI.GetCurrentBoundsScope();
					UI.ModifyPanel(panelID, bounds.Centre, new Vector2(width, bounds.Height) + Vector2.one * 0.5f, ColHelper.MakeCol(0.91f));
				}

				wasMouseOverMenu |= UI.MouseInsideBounds(UI.PrevBounds);
			}

			static string EntryLabel(MenuEntry entry) => entry.SubEntries == null ? entry.Text : entry.Text + "  >";

			void DrawHeader()
			{
				UI.Button(contextMenuHeader, headerTheme, pos, buttonSize, false, false, false, anchor, true, textOffsetX);
				pos.y += buttonSize.y * dirY;
			}
		}

		static bool IsCustomChip() => !Project.ActiveProject.chipLibrary.IsBuiltinChip(interactionContextName);
		static bool CanLabelChip() => Project.ActiveProject.CanEditViewedChip;

		static bool CanDelete() => Project.ActiveProject.CanEditViewedChip;
		static bool CanFlipBus() => Project.ActiveProject.CanEditViewedChip;

		static bool CanSetCol()
		{
			if (!Project.ActiveProject.CanEditViewedChip || UIDrawer.ActiveMenu == UIDrawer.MenuType.ChipCustomization) return false;
			if (interactionContext is PinInstance pin) return pin.IsSourcePin;
			if (interactionContext is SubChipInstance subchip) return subchip.ChipType == ChipType.DisplayLED;

			return false;
		}

		static void FlipBus()
		{
			((SubChipInstance)interactionContext).FlipBus();
		}

		static void SetCol(PinColour col)
		{
			if (interactionContext is PinInstance pin)
			{
				pin.Colour = col;
			}
			else if (interactionContext is SubChipInstance subchip)
			{
				Project.ActiveProject.NotifyLEDColourChanged(subchip, (uint)col);
			}
			
		}

		// Anchored where the menu was opened, so the popup appears where the user right-clicked
		static void OpenInOutPopup() => BottomBarUI.OpenInOutPopupAt(mouseOpenMenuPos);

		// DISPLAY NAME: show the chip's label on its body, all the time, instead of its type name. Toggles;
		// a chip with no label yet gets the rename popup first.
		static void ToggleDisplayName()
		{
			if (interactionContext is not SubChipInstance subChip) return;
			if (string.IsNullOrWhiteSpace(subChip.Label))
			{
				subChip.ShowLabelOnChip = true;
				OpenChipLabelPopup();
			}
			else subChip.ShowLabelOnChip = !subChip.ShowLabelOnChip;
		}

		// DUPLICATE: copy of the chip (bottom bar or scene) under a new name, asked in a popup
		static void DuplicateChip() => DuplicateChipPopup.Open(interactionContextName);

		static void RenameSelectedPins()
		{
			BulkRenamePinsPopup.Open(Project.ActiveProject.controller.SelectedElements.OfType<DevPinInstance>());
		}

		static void RenameSelectedChips()
		{
			BulkRenamePinsPopup.OpenChips(Project.ActiveProject.controller.SelectedElements.OfType<SubChipInstance>());
		}

		static void DeleteSelectedElements() => Project.ActiveProject.controller.DeleteSelected();

		static void OpenChipLabelPopup()
		{
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipLabelPopup);
		}

		public static void EditWire()
		{
			Project.ActiveProject.controller.EnterWireEditMode((WireInstance)interactionContext);
		}

		static void Delete()
		{
			if (interactionContext is IMoveable moveable)
			{
				Project.ActiveProject.controller.Delete(moveable);
			}
			else if (interactionContext is WireInstance wire)
			{
				Project.ActiveProject.controller.DeleteWire(wire);
			}
			else if (interactionContext is PinInstance pin)
			{
				Project.ActiveProject.controller.Delete(pin.parent);
			}
		}

		static void OpenKeyBindMenu()
		{
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.RebindKeyChip);
		}

		static void OpenRomEditMenu() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.RomEdit);

		static void OpenPulseEditMenu() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.PulseEdit);

		static bool CanEditCurrentChip() => Project.ActiveProject.CanEditViewedChip;

		static bool CanEditWire() => CanEditCurrentChip();

		static void OpenPinEditMenu()
		{
			PinEditMenu.SetTargetPin((DevPinInstance)((PinInstance)interactionContext).parent);
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.PinRename);
		}

		static void OpenChip()
		{
			Project project = Project.ActiveProject;
			string chipToOpenName = interactionContextName;

			// Free navigation: switching chips never prompts; unsaved work is kept in memory.
			OpenChipIfConfirmed(true);

			void OpenChipIfConfirmed(bool confirm)
			{
				if (confirm)
				{
					project.LoadDevChipOrCreateNewIfDoesntExist(chipToOpenName);
				}
			}
		}

		static bool CanOpenChip() => IsCustomChip() && CanEditCurrentChip();

		public static void Reset()
		{
			CloseContextMenu();
		}

		public static void CloseContextMenu()
		{
			IsOpen = false;
		}

		public static bool HasFocus() => IsOpen && wasMouseOverMenu;

		public static void UnstarBottomBarEntry()
		{
			Project.ActiveProject.SetStarred(interactionContextName, false, bottomBarItemIsCollection, true);
		}

		static bool CanDeleteBottomBarChip() => !bottomBarItemIsCollection && !Project.ActiveProject.chipLibrary.IsBuiltinChip(interactionContextName);

		static void DeleteBottomBarChip()
		{
			string name = interactionContextName;
			string msg = $"Supprimer la brique \"{name}\" du projet ?\nToute instance de cette brique sera retiree des circuits qui l'utilisent.";
			ConfirmationPopup.Open(msg, () => Project.ActiveProject.DeleteChip(name));
		}

		static void RenameBottomBarChip() => RenameChipPopup.Open(interactionContextName);

		// Total number of NAND gates the chip is made of (custom sub-chips expanded recursively).
		static void ShowNandCount() => InfoPopup.Open(NandCounter.BuildReport(interactionContextName, Project.ActiveProject.chipLibrary));

		static bool CanRecreateWithOnlyNand() => !Project.ActiveProject.chipLibrary.IsBuiltinChip(interactionContextName);

		// Creates (and opens) a new chip doing exactly the same thing, but flattened down to primitives only.
		static void RecreateWithOnlyNand()
		{
			Project p = Project.ActiveProject;
			string sourceName = interactionContextName;
			string newName = MakeNandChipName(sourceName, p.chipLibrary);
			ChipDescription flat = NandFlattener.Flatten(sourceName, p.chipLibrary, newName, out NandFlattener.Report report);

			if (flat == null)
			{
				InfoPopup.Open(report is { AbortedTooLarge: true }
					? $"\"{sourceName}\" depasse {NandFlattener.MaxComponents} composants\nune fois mis a plat. Recreation annulee."
					: $"Impossible de recreer \"{sourceName}\".");
				return;
			}

			// Laying out (and simulating) a few thousand gates is not instant, so let the user opt out.
			if (flat.SubChips.Length > NandOnlyWarnThreshold)
			{
				string msg = $"\"{newName}\" contiendra {flat.SubChips.Length} composants et {flat.Wires.Length} fils.\nLa mise en page peut prendre un moment.";
				ConfirmationPopup.Open(msg, () => CommitNandOnlyChip(flat, report));
			}
			else CommitNandOnlyChip(flat, report);
		}

		static void CommitNandOnlyChip(ChipDescription flat, NandFlattener.Report report)
		{
			CommitGeneratedChip(flat, NandFlattener.DescribeResult(flat.Name, report));
		}

		// Rebuilds the chip out of the logic packages the user owns, minimising the package count.
		static void OptimiseWithChips()
		{
			string sourceName = interactionContextName;
			GatePaletteMenu.Open(sourceName, selection => RunGateMapping(sourceName, selection));
		}

		static void RunGateMapping(string sourceName, bool[] selection)
		{
			Project p = Project.ActiveProject;
			string newName = MakeDerivedChipName(sourceName, "_IC", p.chipLibrary);
			GateMapper.Result r = GateMapper.Map(sourceName, p.chipLibrary, newName, selection);

			if (r.Error != null)
			{
				InfoPopup.Open(r.Error);
				return;
			}

			// The gate bricks must exist before the mapped chip that references them by name.
			// (CommitGeneratedChip refreshes the project's chip list once, covering these too.)
			foreach (ChipDescription brick in r.BricksToCreate)
			{
				Saver.SaveChip(brick, p.description.ProjectName);
				p.chipLibrary.NotifyChipSaved(brick);
				p.SetStarred(brick.Name, true, false, false);
			}

			CommitGeneratedChip(r.Chip, GateMapper.DescribeResult(newName, r));
		}

		// Saves a generated chip, opens it in its own tab, lays it out and reports.
		static void CommitGeneratedChip(ChipDescription chip, string message)
		{
			Project p = Project.ActiveProject;

			// The chip must exist as a file to be openable as a tab (same rule as the assistant's create_module).
			Saver.SaveChip(chip, p.description.ProjectName);
			p.chipLibrary.NotifyChipSaved(chip);
			p.SetStarred(chip.Name, true, false, false);

			// ProjectDescription.AllCustomChipNames is the ONLY list the loader rebuilds the library
			// from, so it has to be refreshed here: starring alone rewrites the description untouched,
			// and the chip would come back invisible (present on disk, unusable, undeletable).
			p.UpdateAndSaveProjectDescription();

			p.LoadDevChipOrCreateNewIfDoesntExist(chip.Name);

			// Generated chips only get a rough grid position; Clean Up does the real layout.
			CircuitAutoLayout.CleanUp(p.ViewedChip);
			p.SaveFromDescription(DescriptionCreator.CreateChipDescription(p.ViewedChip));

			InfoPopup.Open(message);
		}

		static string MakeNandChipName(string sourceName, ChipLibrary library) => MakeDerivedChipName(sourceName, "_NAND", library);

		static string MakeDerivedChipName(string sourceName, string suffix, ChipLibrary library)
		{
			int maxLength = ChipSaveMenu.MaxLengthChipName.Length;

			string Candidate(string tail) => sourceName.Substring(0, Mathf.Min(sourceName.Length, maxLength - tail.Length)) + tail;

			string name = Candidate(suffix);
			for (int i = 2; library.HasChip(name); i++) name = Candidate(suffix + i);
			return name;
		}

		public readonly struct MenuEntry
		{
			readonly string text;
			readonly Func<string> textFunc; // for entries whose label depends on the current state (DISPLAY NAME / HIDE NAME)
			public string Text => textFunc != null ? textFunc() : text;
			public readonly Action OnPress;
			public readonly Func<bool> IsEnabled;
			public readonly MenuEntry[] SubEntries; // when set: hovering the entry opens these in a second menu beside it

			public MenuEntry(string text, Action onPress, Func<bool> isEnabled)
			{
				this.text = text;
				textFunc = null;
				OnPress = onPress;
				IsEnabled = isEnabled;
				SubEntries = null;
			}

			public MenuEntry(Func<string> textFunc, Action onPress, Func<bool> isEnabled)
			{
				text = null;
				this.textFunc = textFunc;
				OnPress = onPress;
				IsEnabled = isEnabled;
				SubEntries = null;
			}

			public MenuEntry(string text, MenuEntry[] subEntries, Func<bool> isEnabled)
			{
				this.text = text;
				textFunc = null;
				OnPress = null;
				IsEnabled = isEnabled;
				SubEntries = subEntries;
			}
		}
	}
}