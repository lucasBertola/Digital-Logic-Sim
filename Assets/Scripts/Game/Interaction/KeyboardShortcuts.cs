using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Seb.Helpers;
using UnityEngine;

namespace DLS.Game
{
	public static class KeyboardShortcuts
	{
		// When a non-blocking on-screen text field is focused (e.g. the Ask Claude panel), game/scene
		// shortcuts are suppressed so typing doesn't delete elements, step the sim, save, etc.
		public static bool TextInputActive;

		// ---- Main Menu shortcuts
		public static bool MainMenu_NewProjectShortcutTriggered => CtrlShortcutTriggered(KeyCode.N);
		public static bool MainMenu_OpenProjectShortcutTriggered => CtrlShortcutTriggered(KeyCode.O);
		public static bool MainMenu_SettingsShortcutTriggered => CtrlShortcutTriggered(KeyCode.S);
		public static bool MainMenu_QuitShortcutTriggered => CtrlShortcutTriggered(KeyCode.Q);

		// ---- Bottom Bar Menu shortcuts ----
		public static bool SaveShortcutTriggered => CtrlShortcutTriggered(KeyCode.S);
		public static bool LibraryShortcutTriggered => CtrlShortcutTriggered(KeyCode.L);
		public static bool PreferencesShortcutTriggered => CtrlShortcutTriggered(KeyCode.P);
		public static bool CreateNewChipShortcutTriggered => CtrlShortcutTriggered(KeyCode.N);
		public static bool QuitToMainMenuShortcutTriggered => CtrlShortcutTriggered(KeyCode.Q);
		public static bool SearchShortcutTriggered => CtrlShortcutTriggered(KeyCode.F);


		// ---- Misc shortcuts ----
		public static bool DuplicateShortcutTriggered => !TextInputActive && MultiModeHeld && InputHelper.IsKeyDownThisFrame(Physical(KeyCode.D));
		public static bool ToggleGridShortcutTriggered => CtrlShortcutTriggered(KeyCode.G);
		public static bool ResetCameraShortcutTriggered => CtrlShortcutTriggered(KeyCode.R);
		public static bool UndoShortcutTriggered => CtrlShortcutTriggered(KeyCode.Z);
		public static bool RedoShortcutTriggered => CtrlShiftShortcutTriggered(KeyCode.Z);

		// ---- Single key shortcuts ----
		public static bool CancelShortcutTriggered => InputHelper.IsKeyDownThisFrame(KeyCode.Escape);
		public static bool ConfirmShortcutTriggered => !TextInputActive && (InputHelper.IsKeyDownThisFrame(KeyCode.Return) || InputHelper.IsKeyDownThisFrame(KeyCode.KeypadEnter));
		public static bool DeleteShortcutTriggered => !TextInputActive && (InputHelper.IsKeyDownThisFrame(KeyCode.Backspace) || InputHelper.IsKeyDownThisFrame(KeyCode.Delete));
		public static bool SimNextStepShortcutTriggered => !TextInputActive && InputHelper.IsKeyDownThisFrame(KeyCode.Space) && !InputHelper.CtrlIsHeld;
		public static bool SimPauseToggleShortcutTriggered => CtrlShortcutTriggered(KeyCode.Space);

		// ---- Dev shortcuts ----
		public static bool OpenSaveDataFolderShortcutTriggered => InputHelper.IsKeyDownThisFrame(Physical(KeyCode.O)) && InputHelper.CtrlIsHeld && InputHelper.ShiftIsHeld && InputHelper.AltIsHeld;

		// ---- Modifiers ----
		public static bool SnapModeHeld => InputHelper.CtrlIsHeld;

		// In "Multi-mode", placed chips will be duplicated once placed to allow placing again; selecting a chip will add it to the current selection; etc.
		public static bool MultiModeHeld => InputHelper.AltIsHeld || InputHelper.ShiftIsHeld;
		public static bool StraightLineModeHeld => InputHelper.ShiftIsHeld;
		public static bool StraightLineModeTriggered => InputHelper.IsKeyDownThisFrame(KeyCode.LeftShift);
		public static bool CameraActionKeyHeld => InputHelper.AltIsHeld;
		public static bool TakeFirstFromCollectionModifierHeld => InputHelper.CtrlIsHeld || InputHelper.AltIsHeld || InputHelper.ShiftIsHeld;

		// ---- Helpers ----
		static bool CtrlShortcutTriggered(KeyCode key) => !TextInputActive && InputHelper.IsKeyDownThisFrame(Physical(key)) && InputHelper.CtrlIsHeld && !(InputHelper.AltIsHeld || InputHelper.ShiftIsHeld);
		static bool CtrlShiftShortcutTriggered(KeyCode key) => !TextInputActive && InputHelper.IsKeyDownThisFrame(Physical(key)) && InputHelper.CtrlIsHeld && InputHelper.ShiftIsHeld && !(InputHelper.AltIsHeld);
		static bool ShiftShortcutTriggered(KeyCode key) => !TextInputActive && InputHelper.IsKeyDownThisFrame(Physical(key)) && InputHelper.ShiftIsHeld && !(InputHelper.AltIsHeld || InputHelper.CtrlIsHeld);

		// ---- Keyboard-layout independence ----
		// Unity's legacy input identifies keys by PHYSICAL position, named after the US layout: on an AZERTY
		// keyboard the key labelled Z sits where the US W is, so pressing Ctrl+Z reports KeyCode.W and the
		// undo shortcut never fired (Ctrl+N worked, N is in the same place on both). Physical() maps the
		// letter we mean to the KeyCode Unity will actually report under the current Windows layout.
		static readonly Dictionary<KeyCode, KeyCode> physicalCache = new();
		static IntPtr physicalCacheLayout;

		// US scan codes of the letter keys (what Unity names its KeyCodes after).
		static readonly Dictionary<uint, KeyCode> scanCodeToUsKey = new()
		{
			{ 0x10, KeyCode.Q }, { 0x11, KeyCode.W }, { 0x12, KeyCode.E }, { 0x13, KeyCode.R }, { 0x14, KeyCode.T }, { 0x15, KeyCode.Y }, { 0x16, KeyCode.U }, { 0x17, KeyCode.I }, { 0x18, KeyCode.O }, { 0x19, KeyCode.P },
			{ 0x1E, KeyCode.A }, { 0x1F, KeyCode.S }, { 0x20, KeyCode.D }, { 0x21, KeyCode.F }, { 0x22, KeyCode.G }, { 0x23, KeyCode.H }, { 0x24, KeyCode.J }, { 0x25, KeyCode.K }, { 0x26, KeyCode.L },
			{ 0x2C, KeyCode.Z }, { 0x2D, KeyCode.X }, { 0x2E, KeyCode.C }, { 0x2F, KeyCode.V }, { 0x30, KeyCode.B }, { 0x31, KeyCode.N }, { 0x32, KeyCode.M }
		};

		public static KeyCode Physical(KeyCode logical)
		{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
			if (logical < KeyCode.A || logical > KeyCode.Z) return logical;
			try
			{
				IntPtr layout = GetKeyboardLayout(0);
				if (layout != physicalCacheLayout)
				{
					physicalCache.Clear();
					physicalCacheLayout = layout;
				}

				if (physicalCache.TryGetValue(logical, out KeyCode cached)) return cached;

				KeyCode result = logical;
				char ch = (char)('a' + (logical - KeyCode.A));
				short vk = VkKeyScanExW(ch, layout);
				if (vk != -1)
				{
					uint scanCode = MapVirtualKeyExW((uint)(vk & 0xFF), 0 /* MAPVK_VK_TO_VSC */, layout);
					if (scanCodeToUsKey.TryGetValue(scanCode, out KeyCode physical)) result = physical;
				}

				physicalCache[logical] = result;
				return result;
			}
			catch
			{
				return logical;
			}
#else
			return logical;
#endif
		}

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
		[DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint idThread);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScanExW(char ch, IntPtr dwhkl);
		[DllImport("user32.dll")] static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr dwhkl);
#endif
	}
}