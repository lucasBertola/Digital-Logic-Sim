using System;
using DLS.Game;
using Seb.Helpers;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// "Optimise with chips": pick which logic packages you actually own, then the mapper rebuilds the
	// circuit out of them while minimising how many packages you have to open.
	public static class GatePaletteMenu
	{
		const float menuWidth = 48;
		const float rowSpacing = 0.15f;

		static string targetChipName = string.Empty;
		static Action<bool[]> onConfirm;
		static bool[] snapshot;

		public static void Open(string chipName, Action<bool[]> confirm)
		{
			targetChipName = chipName;
			onConfirm = confirm;
			snapshot = (bool[])GatePackages.Selected.Clone();
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.GatePalette);
		}

		public static void Reset()
		{
			targetChipName = string.Empty;
			onConfirm = null;
			snapshot = null;
		}

		public static void DrawMenu()
		{
			MenuHelper.DrawBackgroundOverlay();

			bool[] selection = GatePackages.Selected;
			ButtonTheme theme = MenuHelper.Theme.ButtonTheme;
			float rowHeight = DrawSettings.ButtonHeight;
			int rows = GatePackages.All.Length;

			float totalHeight = (rows + 3) * (rowHeight + rowSpacing) + 2.5f;
			Vector2 pos = UI.Centre + new Vector2(-menuWidth / 2, totalHeight / 2);

			using (UI.BeginBoundsScope(true))
			{
				Draw.ID panelID = UI.ReservePanel();

				MenuHelper.DrawLeftAlignTextWithBackground($"BOITIERS DISPONIBLES   ({targetChipName})", pos,
					new Vector2(menuWidth, rowHeight), Anchor.TopLeft, Color.white, ColHelper.MakeCol(0.18f), true);
				pos.y -= rowHeight + 0.6f;

				for (int i = 0; i < rows; i++)
				{
					GatePackage package = GatePackages.All[i];
					string label = (selection[i] ? "[X]   " : "[  ]   ") + package.Label;

					if (UI.Button(label, theme, pos, new Vector2(menuWidth, rowHeight), true, false, false, Anchor.TopLeft, true, 1f))
					{
						selection[i] = !selection[i];
					}

					pos.y -= rowHeight + rowSpacing;
				}

				pos.y -= 0.6f;
				int bulk = MenuHelper.DrawButtonTriplet("TOUT", "RIEN", "NAND SEULS", pos, menuWidth, false);
				if (bulk == 0 || bulk == 1)
				{
					for (int i = 0; i < selection.Length; i++) selection[i] = bulk == 0;
				}
				else if (bulk == 2)
				{
					// only the single-gate NAND: the objective becomes the plain gate count, which is
					// what "as few NAND as possible" means, and the result is built from bare NANDs.
					for (int i = 0; i < selection.Length; i++) selection[i] = i == GatePackages.UnitNandIndex;
				}

				pos.y -= rowHeight + 0.6f;

				bool canConfirm = GatePackages.AnySelected(selection);
				MenuHelper.CancelConfirmResult button = MenuHelper.DrawCancelConfirmButtons(pos, menuWidth, false, true, true, canConfirm);

				MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

				if (button == MenuHelper.CancelConfirmResult.Cancel)
				{
					if (snapshot != null) GatePackages.Selected = snapshot;
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
				else if (button == MenuHelper.CancelConfirmResult.Confirm && canConfirm)
				{
					Action<bool[]> confirm = onConfirm;
					bool[] chosen = (bool[])selection.Clone();
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
					confirm?.Invoke(chosen);
				}
			}
		}
	}
}
