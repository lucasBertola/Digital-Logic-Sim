using System.Collections.Generic;
using System;
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
	public static class BottomBarUI
	{
		public const float barHeight = 3;
		const float padY = 0.3f;
		const float buttonSpacing = 0.25f;
		const float buttonHeight = barHeight - padY * 2;

		const string shortcutTextCol = "<color=#666666ff>";

		static readonly string[] menuButtonNames =
		{
			$"NEW CHIP     {shortcutTextCol}Ctrl+N",
			$"SAVE CHIP    {shortcutTextCol}Ctrl+S",
			$"FIND CHIP    {shortcutTextCol}Ctrl+F",
			$"LIBRARY      {shortcutTextCol}Ctrl+L",
			$"PREFS        {shortcutTextCol}Ctrl+P",
			$"EXPORT (LLM)",
			$"TRUTH TABLE",
			$"ASK CLAUDE",
			$"QUIT         {shortcutTextCol}Ctrl+Q"
		};

		const int NewChipButtonIndex = 0;
		const int SaveChipButtonIndex = 1;
		const int FindChipButtonIndex = 2;
		const int LibraryButtonIndex = 3;
		const int OptionsButtonIndex = 4;
		const int ExportButtonIndex = 5;
		const int TruthTableButtonIndex = 6;
		const int AskClaudeButtonIndex = 7;
		const int QuitButtonIndex = 8;

		// ---- Export confirmation toast ----
		static string toastMsg;
		static float toastEndTime;

		// ---- State ----
		static float scrollX;
		static float chipBarTotalWidthLastFrame;
		static bool isDraggingChipBar;
		static float mouseDragPrev;
		static bool closeActiveCollectionMultiModeExit;

		static int toggleMenuFrame;
		static int collectionInteractFrame;
		static ChipCollection activeCollection;
		static Vector2 collectionPopupBottomLeft;
		// true: popup opened from the bottom bar (panel pinned to the bar); false: opened at the mouse by a right-click in the scene
		static bool collectionPopupAnchoredToBar = true;
		const string SceneRightClickCollectionName = "IN/OUT";

		// Rows of the open collection popup: its chips, plus "linked" collections shown as sub-menus
		// (the IN/OUT popup lists MERGE/SPLIT and BUS; hovering one opens its chips beside the popup).
		struct PopupItem
		{
			public string chipName;      // a chip to place (null for a sub-menu row)
			public ChipCollection sub;   // the linked collection (null for a chip row)
			public string Label => sub != null ? sub.Name + "  >" : chipName;
		}
		static readonly List<PopupItem> popupItems = new();
		static ChipCollection hoverSub;      // sub-menu currently open (hover)
		static Bounds2D hoverSubAnchor;      // bounds of the row that opened it
		static string subPressedChip;        // chip clicked in the sub-menu this frame

		static readonly Dictionary<string, string[]> LinkedCollections = new(StringComparer.OrdinalIgnoreCase)
		{
			{ "IN/OUT", new[] { "MERGE/SPLIT", "BUS" } }
		};

		static void BuildPopupItems(ChipCollection collection)
		{
			popupItems.Clear();
			if (LinkedCollections.TryGetValue(collection.Name, out string[] linked))
			{
				foreach (string name in linked)
				{
					if (TryGetChipCollectionByName(name, out ChipCollection sub) && sub.Chips.Count > 0 && sub != collection)
						popupItems.Add(new PopupItem { sub = sub });
				}
			}
			foreach (string chip in collection.Chips) popupItems.Add(new PopupItem { chipName = chip });
			hoverSub = null;
		}
		static Bounds2D barBounds_ScreenSpace;

		static bool MenuButtonsAndShortcutsEnabled => Project.ActiveProject.CanEditViewedChip;

		public static void DrawUI(Project project)
		{
			DrawBottomBar(project);
			TruthTableView.Draw();
			AskClaudeMenu.Draw();
			if (AskClaude.Waiting && KeyboardShortcuts.CancelShortcutTriggered) AskClaude.Cancel();
			if (AskClaude.ConsumeCancelled()) ShowToast("Claude: cancelled, changes reverted");
			if (AskClaude.ConsumeQuickTurnFinished(out string quickSummary)) ShowToast(quickSummary);
			DrawClaudeBusyIndicator();
			DrawToast();

			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup)
			{
				DrawPopupMenu();
			}

			if (UIDrawer.ActiveMenu is UIDrawer.MenuType.BottomBarMenuPopup or UIDrawer.MenuType.None)
			{
				HandleKeyboardShortcuts();
			}
		}

		static void DrawPopupMenu()
		{
			ButtonTheme theme = DrawSettings.ActiveUITheme.MenuPopupButtonTheme;
			float menuWidth = Draw.CalculateTextBoundsSize(menuButtonNames[0].AsSpan(), theme.fontSize, theme.font).x + 1;
			Vector2 pos = new(buttonSpacing, barHeight + buttonSpacing);
			Vector2 size = new(menuWidth, buttonHeight);
			Draw.ID panelID = UI.ReservePanel();

			using (UI.BeginBoundsScope(true))
			{
				for (int i = menuButtonNames.Length - 1; i >= 0; i--)
				{
					bool buttonEnabled = MenuButtonsAndShortcutsEnabled || i is QuitButtonIndex or OptionsButtonIndex;
					string text = menuButtonNames[i];
					if (UI.Button(text, theme, pos, size, buttonEnabled, false, false, Anchor.BottomLeft))
					{
						ButtonPressed(i);
					}

					pos = UI.PrevBounds.TopLeft;
				}

				Bounds2D uiBounds = UI.GetCurrentBoundsScope();
				UI.ModifyPanel(panelID, uiBounds.Centre, uiBounds.Size + Vector2.one * (buttonSpacing * 2), Color.white);
			}

			// Close if clicked nothing or pressed esc
			if (UIDrawer.ActiveMenu is UIDrawer.MenuType.BottomBarMenuPopup)
			{
				if (InputHelper.IsAnyMouseButtonDownThisFrame_IgnoreConsumed() && Time.frameCount != toggleMenuFrame)
				{
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}

				if (KeyboardShortcuts.CancelShortcutTriggered)
				{
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
			}

			void ButtonPressed(int i)
			{
				if (i == NewChipButtonIndex) CreateNewChip();
				else if (i == SaveChipButtonIndex) SaveChip();
				else if (i == FindChipButtonIndex) OpenSearchMenu();
				else if (i == LibraryButtonIndex) OpenLibraryMenu();
				else if (i == OptionsButtonIndex) OpenPreferencesMenu();
				else if (i == ExportButtonIndex) ExportForLLM();
				else if (i == TruthTableButtonIndex) TruthTableView.Toggle();
				else if (i == AskClaudeButtonIndex) AskClaudeMenu.Open();
				else if (i == QuitButtonIndex) ExitToMainMenu();
			}
		}

		static void DrawBottomBar(Project project)
		{
			Bounds2D bounds_UISpace = new(Vector2.zero, new Vector2(UI.Width, barHeight));
			barBounds_ScreenSpace = UI.UIToScreenSpace(bounds_UISpace);

			bool inOtherMenu = !(UIDrawer.ActiveMenu is UIDrawer.MenuType.BottomBarMenuPopup or UIDrawer.MenuType.None);
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			// If mouse is over context menu, then ignore inputs (don't want to actually disable the buttons because the grey-out effect is distracting here)
			// Also if middle mouse is held, as may be dragging the bar so don't want to select buttons
			bool ignoreInputs = ContextMenu.HasFocus() || InputHelper.IsMouseHeld(MouseButton.Middle);
			bool isRightClick = InputHelper.IsMouseDownThisFrame(MouseButton.Right);
			if (closeActiveCollectionMultiModeExit && !KeyboardShortcuts.MultiModeHeld)
			{
				closeActiveCollectionMultiModeExit = false;
				activeCollection = null;
			}

			UI.DrawPanel(bounds_UISpace, theme.StarredBarCol);

			// Menu toggle button
			Vector2 menuButtonPos = new(buttonSpacing, padY);
			Vector2 menuButtonSize = new(1.5f, barHeight - padY * 2);
			bool menuButtonEnabled = !inOtherMenu;

			if (UI.Button("MENU", theme.MenuButtonTheme, menuButtonPos, menuButtonSize, menuButtonEnabled, true, false, Anchor.BottomLeft, ignoreInputs: ignoreInputs))
			{
				UIDrawer.ToggleBottomPopupMenu();
				toggleMenuFrame = Time.frameCount;
			}


			// Chips
			ButtonTheme buttonTheme = theme.ChipButton;

			using (UI.CreateMaskScopeMinMax(new Vector2(UI.PrevBounds.Right + buttonSpacing, 0), new Vector2(UI.Width, barHeight)))
			{
				bool chipButtonsEnabled = !inOtherMenu && project.CanEditViewedChip;

				// -- Chip bar drag/scroll input --
				if (MouseIsOverBar())
				{
					const float scrollSensitivity = 2;
					scrollX += Maths.AbsoluteMax(InputHelper.MouseScrollDelta.x, InputHelper.MouseScrollDelta.y) * -scrollSensitivity;
					if (InputHelper.IsMouseDownThisFrame(MouseButton.Middle))
					{
						isDraggingChipBar = true;
						mouseDragPrev = UI.ScreenToUISpace(InputHelper.MousePos).x;
					}
				}

				if (isDraggingChipBar)
				{
					float mouseDragNew = UI.ScreenToUISpace(InputHelper.MousePos).x;
					scrollX += mouseDragNew - mouseDragPrev;
					mouseDragPrev = mouseDragNew;
					if (InputHelper.IsMouseUpThisFrame(MouseButton.Middle))
					{
						isDraggingChipBar = false;
					}
				}

				// -- Draw --
				float chipButtonsRegionStartX = UI.PrevBounds.Right + buttonSpacing;
				float chipButtonRegionWidth = UI.Width - chipButtonsRegionStartX;

				scrollX = Mathf.Clamp(scrollX, Mathf.Min(0, chipButtonRegionWidth - chipBarTotalWidthLastFrame), 0);
				float buttonPosX = chipButtonsRegionStartX + scrollX;
				float firstButtonLeft = buttonPosX;

				for (int i = 0; i < project.description.StarredList.Count; i++)
				{
					StarredItem starred = project.description.StarredList[i];
					bool isToggledOpenCollection = activeCollection != null && ChipDescription.NameMatch(starred.Name, activeCollection.Name);
					string buttonName = starred.GetDisplayStringForBottomBar(isToggledOpenCollection);

					float textOffsetX = 0;

					Vector2 buttonPos = new(buttonPosX, padY);
					Vector2 buttonSize = new(0.5f, buttonHeight);

					if (starred.IsCollection)
					{
						textOffsetX = -0.2f;
						buttonSize.x += -0.5f;
					}

					bool canAdd = starred.IsCollection || project.ViewedChip.CanAddSubchip(buttonName);

					if (UI.Button(buttonName, buttonTheme, buttonPos, buttonSize, chipButtonsEnabled && canAdd, true, false, Anchor.BottomLeft, textOffsetX: textOffsetX, ignoreInputs: ignoreInputs))
					{
						if (starred.IsCollection)
						{
							ChipCollection newActiveCollection = GetChipCollectionByName(starred.Name);
							// Take first item from collection without opening
							if (newActiveCollection.Chips.Count > 0 && KeyboardShortcuts.TakeFirstFromCollectionModifierHeld)
							{
								project.controller.StartPlacing(newActiveCollection.Chips[0]);
								activeCollection = null;
							}
							// Open collection in popup (or close it if it is the one already open)
							else if (newActiveCollection == activeCollection)
							{
								activeCollection = null;
							}
							else
							{
								OpenCollectionPopup(newActiveCollection, new Vector2(UI.PrevBounds.Left, barHeight), anchoredToBar: true);
							}
						}
						else
						{
							project.controller.StartPlacing(project.chipLibrary.GetChipDescriptionForSim(starred.Name)); // live version (unsaved edits included)
							activeCollection = null;
						}
					}
					else if (isRightClick && UI.MouseInsideBounds(UI.PrevBounds))
					{
						ContextMenu.OpenBottomBarContextMenu(starred.Name, starred.IsCollection, false);
					}

					Bounds2D chipBtnBounds = UI.PrevBounds;

					// Red * on chips that have unsaved changes.
					if (!starred.IsCollection && project.IsChipDirty(starred.Name))
					{
						UI.DrawText("*", theme.FontBold, theme.FontSizeRegular * 1.35f, chipBtnBounds.TopRight + new Vector2(-0.12f, 0.05f), Anchor.TopRight, new Color(1f, 0.4f, 0.4f));
					}

					buttonPosX += chipBtnBounds.Width + buttonSpacing;
				}

				// Record total width of all buttons to be used as scroll bounds for the next frame
				chipBarTotalWidthLastFrame = UI.PrevBounds.Right - firstButtonLeft + buttonSpacing;
			}


			DrawCollectionsPopup();
		}

		// Opens the IN/OUT collection at a point of the screen (used by the empty-space context menu). It is the very
		// same popup as the one opened from the bottom bar: same drawing, same shift-click multi-placement, same closing rules.
		public static void OpenInOutPopupAt(Vector2 uiPos)
		{
			if (!Project.ActiveProject.CanEditViewedChip) return;
			if (!TryGetChipCollectionByName(SceneRightClickCollectionName, out ChipCollection collection) || collection.Chips.Count == 0) return;

			// Keep the whole list on screen when possible (it grows upward from the anchor)
			int n = collection.Chips.Count;
			float totalHeight = n * buttonHeight + (n + 1) * buttonSpacing;
			float minY = barHeight + buttonSpacing * 2;
			float y = Mathf.Clamp(uiPos.y, minY, Mathf.Max(minY, UI.Height - totalHeight));
			OpenCollectionPopup(collection, new Vector2(uiPos.x, y), anchoredToBar: false);
		}

		static void OpenCollectionPopup(ChipCollection collection, Vector2 bottomLeft, bool anchoredToBar)
		{
			collectionPopupBottomLeft = bottomLeft;
			collectionPopupAnchoredToBar = anchoredToBar;
			activeCollection = collection;
			BuildPopupItems(collection);
			collectionInteractFrame = Time.frameCount;
			closeActiveCollectionMultiModeExit = false;
		}


		static void DrawCollectionsPopup()
		{
			if (activeCollection == null || activeCollection.Chips.Count <= 0) return;
			if (popupItems.Count == 0) BuildPopupItems(activeCollection);

			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Project project = Project.ActiveProject;

			int firstButtonIndex = popupItems.Count - 1;
			int pressedIndex = -1;
			subPressedChip = null;
			Vector2 layoutOrigin = collectionPopupBottomLeft + new Vector2(0, 0);
			bool expandLeft = layoutOrigin.x > UI.HalfWidth;
			bool isFirstPartial = true;
			bool openedContextMenu = false;

			while (firstButtonIndex >= 0)
			{
				Bounds2D collectionBounds = default;
				int numButtonsToDraw = 0;

				// Layout pass: calculate draw bounds (stop before going past top of screen)
				using (UI.BeginBoundsScope(draw: false))
				{
					Vector2 buttonLayoutPos = layoutOrigin;

					for (int i = firstButtonIndex; i >= 0; i--)
					{
						string chipName = popupItems[i].Label;
						UI.Button(chipName, DrawSettings.ActiveUITheme.ChipButton, buttonLayoutPos, new Vector2(0, buttonHeight), false, true, false, Anchor.BottomLeft, false, 0);
						buttonLayoutPos = UI.PrevBounds.TopLeft + Vector2.up * buttonSpacing;

						// Stop if approaching top of screen (we'll draw the rest of the collection starting on a new line)
						if (buttonLayoutPos.y > UI.Height - 0.1f) break;

						collectionBounds = UI.GetCurrentBoundsScope();
						numButtonsToDraw++;
					}
				}

				if (expandLeft && !isFirstPartial)
				{
					collectionBounds = Bounds2D.Translate(collectionBounds, Vector2.left * collectionBounds.Width);
				}
				else if (isFirstPartial)
				{
					// Don't let the first column run off the right edge of the screen
					float overflow = collectionBounds.Right + buttonSpacing * 2 - UI.Width;
					if (overflow > 0) collectionBounds = Bounds2D.Translate(collectionBounds, Vector2.left * overflow);
				}

				// Draw the collections (or as much as fit vertically), as well as a background panel
				Bounds2D panelBounds = Bounds2D.Grow(collectionBounds, buttonSpacing * 2);
				if (collectionPopupAnchoredToBar) panelBounds = new Bounds2D(new Vector2(panelBounds.Min.x, barHeight), panelBounds.Max);
				UI.DrawPanel(panelBounds, theme.StarredBarCol);
				int buttonIndex = DrawCollectionsPopupPartial(collectionBounds.BottomLeft, collectionBounds.Width, firstButtonIndex, numButtonsToDraw, ref openedContextMenu);
				if (buttonIndex != -1) pressedIndex = buttonIndex;

				// Prepare for next part of the collection (if not all did fit on the screen)
				firstButtonIndex -= numButtonsToDraw;
				layoutOrigin = new Vector2(expandLeft ? panelBounds.Left : panelBounds.Right, collectionPopupBottomLeft.y);
				isFirstPartial = false;
			}

			// Sub-menu (MERGE/SPLIT, BUS...) beside the row being hovered
			if (hoverSub != null) DrawSubCollectionPopup(ref openedContextMenu);

			if (!openedContextMenu)
			{
				string chosen = subPressedChip ?? (pressedIndex != -1 ? popupItems[pressedIndex].chipName : null);
				if (chosen != null)
				{
					project.controller.StartPlacing(project.chipLibrary.GetChipDescriptionForSim(chosen));
					if (KeyboardShortcuts.MultiModeHeld)
					{
						closeActiveCollectionMultiModeExit = true;
					}
					else
					{
						activeCollection = null;
					}
				}
				else if (KeyboardShortcuts.CancelShortcutTriggered || (InputHelper.IsAnyMouseButtonDownThisFrame_IgnoreConsumed() && Time.frameCount != collectionInteractFrame) || UIDrawer.ActiveMenu != UIDrawer.MenuType.None)
				{
					activeCollection = null;
				}
			}
		}

		static int DrawCollectionsPopupPartial(Vector2 bottomLeftCurr, float maxWidth, int startIndex, int count, ref bool openedContextMenu)
		{
			int pressedIndex = -1;
			int endIndex = startIndex - count + 1;
			ButtonTheme theme = DrawSettings.ActiveUITheme.ChipButton;
			DevChipInstance viewedChip = Project.ActiveProject.ViewedChip;
			bool ignoreInputs = ContextMenu.HasFocus();

			// Draw pop-up buttons
			for (int i = startIndex; i >= endIndex; i--)
			{
				const float offsetX = 0.55f;
				PopupItem item = popupItems[i];

				if (item.sub != null)
				{
					// Sub-menu row: hovering it opens the linked collection beside the popup
					UI.Button(item.Label, theme, bottomLeftCurr, new Vector2(maxWidth, buttonHeight), true, false, false, Anchor.BottomLeft, true, offsetX, ignoreInputs);
					if (UI.MouseInsideBounds(UI.PrevBounds))
					{
						hoverSub = item.sub;
						hoverSubAnchor = UI.PrevBounds;
					}
				}
				else
				{
					string chipName = item.chipName;
					bool enabled = viewedChip.CanAddSubchip(chipName);
					if (UI.Button(chipName, theme, bottomLeftCurr, new Vector2(maxWidth, buttonHeight), enabled, false, false, Anchor.BottomLeft, true, offsetX, ignoreInputs))
					{
						pressedIndex = i;
					}
					else if (InputHelper.IsMouseDownThisFrame(MouseButton.Right) && UI.MouseInsideBounds(UI.PrevBounds))
					{
						ContextMenu.OpenBottomBarContextMenu(chipName, false, true);
						openedContextMenu = true;
					}

					if (UI.MouseInsideBounds(UI.PrevBounds)) hoverSub = null; // hovering a chip row closes the sub-menu
				}

				bottomLeftCurr = UI.PrevBounds.TopLeft + Vector2.up * buttonSpacing;
			}

			return pressedIndex;
		}

		// The chips of a linked collection, in a column beside the hovered row (to its right, or to its left
		// when there is no room). Same look and behaviour as the popup itself (click = place, shift-click =
		// several, right-click = context menu).
		static void DrawSubCollectionPopup(ref bool openedContextMenu)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			ButtonTheme btn = theme.ChipButton;
			DevChipInstance viewedChip = Project.ActiveProject.ViewedChip;
			bool ignoreInputs = ContextMenu.HasFocus();
			const float offsetX = 0.55f;

			// width = widest chip name
			float width = 0;
			using (UI.BeginBoundsScope(draw: false))
			{
				foreach (string chip in hoverSub.Chips)
				{
					UI.Button(chip, btn, Vector2.zero, new Vector2(0, buttonHeight), false, true, false, Anchor.BottomLeft, false, 0);
					width = Mathf.Max(width, UI.PrevBounds.Width);
				}
			}

			int n = hoverSub.Chips.Count;
			float totalH = n * buttonHeight + (n - 1) * buttonSpacing;
			bool right = hoverSubAnchor.Right + buttonSpacing * 3 + width <= UI.Width;
			float x = right ? hoverSubAnchor.Right + buttonSpacing * 3 : hoverSubAnchor.Left - buttonSpacing * 3 - width;
			float bottom = Mathf.Clamp(hoverSubAnchor.Bottom, buttonSpacing * 2, Mathf.Max(buttonSpacing * 2, UI.Height - totalH - buttonSpacing * 2));

			Bounds2D colBounds = Bounds2D.CreateFromCentreAndSize(new Vector2(x + width / 2f, bottom + totalH / 2f), new Vector2(width, totalH));
			UI.DrawPanel(Bounds2D.Grow(colBounds, buttonSpacing * 2), theme.StarredBarCol);

			Vector2 pos = new(x, bottom);
			for (int i = n - 1; i >= 0; i--)
			{
				string chipName = hoverSub.Chips[i];
				bool enabled = viewedChip.CanAddSubchip(chipName);
				if (UI.Button(chipName, btn, pos, new Vector2(width, buttonHeight), enabled, false, false, Anchor.BottomLeft, true, offsetX, ignoreInputs))
				{
					subPressedChip = chipName;
				}
				else if (InputHelper.IsMouseDownThisFrame(MouseButton.Right) && UI.MouseInsideBounds(UI.PrevBounds))
				{
					ContextMenu.OpenBottomBarContextMenu(chipName, false, true);
					openedContextMenu = true;
				}
				pos = UI.PrevBounds.TopLeft + Vector2.up * buttonSpacing;
			}
		}

		static ChipCollection GetChipCollectionByName(string name)
		{
			if (TryGetChipCollectionByName(name, out ChipCollection c)) return c;
			throw new Exception("Failed to find collection with name: " + name);
		}

		static bool TryGetChipCollectionByName(string name, out ChipCollection collection)
		{
			foreach (ChipCollection c in Project.ActiveProject.description.ChipCollections)
			{
				if (ChipDescription.NameMatch(c.Name, name))
				{
					collection = c;
					return true;
				}
			}

			collection = null;
			return false;
		}

		static bool MouseIsOverBar() => InputHelper.MouseInBounds_ScreenSpace(barBounds_ScreenSpace);

		static void ExitToMainMenu()
		{
			if (Project.ActiveProject.AnyUnsavedChanges()) UnsavedChangesPopup.OpenPopup(ExitIfTrue);
			else ExitIfTrue(true);

			static void ExitIfTrue(bool exit)
			{
				if (exit)
				{
					Project.ActiveProject.NotifyExit();
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.MainMenu);
				}
			}
		}

		static void OpenSaveMenu() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipSave);

		// SAVE = save EVERYTHING at once. If the current chip was never saved (unnamed), name it first.
		static void SaveChip()
		{
			Project p = Project.ActiveProject;
			if (!p.CanEditViewedChip) return;
			if (!p.ChipHasBeenSavedBefore)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipSave);
			}
			else
			{
				p.SaveAllOpenChips();
				ShowToast("Tout sauvegarde.");
			}
		}
		static void OpenSearchMenu() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.Search);
		static void OpenLibraryMenu() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipLibrary);
		static void OpenPreferencesMenu() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.Preferences);

		// Export the currently-edited chip (+ its sub-circuits) as LLM-friendly text into the clipboard.
		static void ExportForLLM()
		{
			Project p = Project.ActiveProject;
			try
			{
				ChipDescription desc = DescriptionCreator.CreateChipDescription(p.ViewedChip);
				string text = CircuitExporter.ExportChipWithDeps(desc, p.chipLibrary, p.description.ProjectName);
				InputHelper.CopyToClipboard(text);
				string label = string.IsNullOrEmpty(desc.Name) ? "le circuit courant" : $"\"{desc.Name}\"";
				ShowToast($"Copié dans le presse-papier : {label} + ses sous-circuits ({text.Length} caractères)");
			}
			catch (Exception e)
			{
				ShowToast("Échec de l'export : " + e.Message);
			}

			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}

		public static void CleanUp()
		{
			Project p = Project.ActiveProject;
			if (p.CanEditViewedChip)
			{
				CircuitAutoLayout.CleanUp(p.ViewedChip);
				ShowToast("Circuit reorganise (Clean Up).");
			}

			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}

		static void ShowToast(string msg)
		{
			toastMsg = msg;
			toastEndTime = Time.time + 4f;
		}

		// Small "Claude is working" pill at the top of the screen while a request runs and the chat panel is
		// closed (i.e. a command typed in the quick bar).
		static void DrawClaudeBusyIndicator()
		{
			if (!AskClaude.Waiting || AskClaudeMenu.IsOpen) return;
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;

			int dots = (int)(Time.time * 2f) % 4;
			string label = "Claude is working" + new string('.', dots);
			float textW = Draw.CalculateTextBoundsSize("Claude is working...", theme.FontSizeRegular, theme.FontRegular).x;
			const float h = 2.2f, spinnerW = 2.4f, pad = 1.0f;
			float w = pad + spinnerW + textW + pad;
			Vector2 centre = new(UI.Width / 2f, UI.Height - 0.5f - h / 2f);
			UI.DrawPanel(centre, new Vector2(w, h), new Color(0.08f, 0.08f, 0.1f, 0.9f));

			// spinner: 8 dots, one lit after the other
			Vector2 spin = centre + Vector2.left * (w / 2f - pad - spinnerW / 2f);
			float t = Time.time * 8f;
			for (int i = 0; i < 8; i++)
			{
				float a = i * Mathf.PI * 2f / 8f;
				float k = ((i - t) % 8f + 8f) % 8f / 8f; // 0 = lit, 1 = faded
				Color c = Color.Lerp(new Color(0.55f, 0.8f, 1f), new Color(0.25f, 0.3f, 0.38f), k);
				UI.DrawPanel(spin + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.6f, Vector2.one * 0.28f, c);
			}

			UI.DrawText(label, theme.FontRegular, theme.FontSizeRegular, spin + Vector2.right * (spinnerW / 2f), Anchor.TextCentreLeft, Color.white);
		}

		static void DrawToast()
		{
			if (string.IsNullOrEmpty(toastMsg) || Time.time > toastEndTime) return;
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Vector2 pos = new(UI.Width / 2f, barHeight + 1.2f);
			UI.DrawText(toastMsg, theme.FontRegular, theme.FontSizeRegular, pos + new Vector2(0.03f, -0.03f), Anchor.CentreBottom, new Color(0, 0, 0, 0.75f));
			UI.DrawText(toastMsg, theme.FontRegular, theme.FontSizeRegular, pos, Anchor.CentreBottom, Color.white);
		}

		static void CreateNewChip()
		{
			// Free navigation: no prompt. Create a blank chip and immediately prompt for its name so it
			// gets saved and appears in the bottom bar.
			Project.ActiveProject.BeginNewChip();
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipSave);
		}

		static void HandleKeyboardShortcuts()
		{
			if (MenuButtonsAndShortcutsEnabled)
			{
				// Space = Claude command bar (when the sim is paused, Space keeps stepping the simulation)
				if (KeyboardShortcuts.QuickAskShortcutTriggered && UIDrawer.ActiveMenu == UIDrawer.MenuType.None && !Project.ActiveProject.simPaused) QuickAskBar.Open();
				if (KeyboardShortcuts.CreateNewChipShortcutTriggered) CreateNewChip();
				if (KeyboardShortcuts.SaveShortcutTriggered) SaveChip();
				if (KeyboardShortcuts.LibraryShortcutTriggered) OpenLibraryMenu();
			}

			if (KeyboardShortcuts.PreferencesShortcutTriggered) OpenPreferencesMenu();
			if (KeyboardShortcuts.QuitToMainMenuShortcutTriggered) ExitToMainMenu();
		}

		public static void Reset()
		{
			scrollX = 0;
			chipBarTotalWidthLastFrame = 0;
			isDraggingChipBar = false;
			activeCollection = null;
			collectionPopupAnchoredToBar = true;
			popupItems.Clear();
			hoverSub = null;
		}
	}
}