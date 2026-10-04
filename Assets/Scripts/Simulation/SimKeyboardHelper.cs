using System.Collections.Generic;
using Seb.Helpers;
using UnityEngine;

namespace DLS.Simulation
{
	public static class SimKeyboardHelper
	{
		public static readonly KeyCode[] ValidInputKeys =
		{
			KeyCode.A, KeyCode.B, KeyCode.C, KeyCode.D, KeyCode.E, KeyCode.F, KeyCode.G,
			KeyCode.H, KeyCode.I, KeyCode.J, KeyCode.K, KeyCode.L, KeyCode.M, KeyCode.N,
			KeyCode.O, KeyCode.P, KeyCode.Q, KeyCode.R, KeyCode.S, KeyCode.T, KeyCode.U,
			KeyCode.V, KeyCode.W, KeyCode.X, KeyCode.Y, KeyCode.Z,

			KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
			KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9
		};

		// Arrow keys (user, 2026-10-04): a KEY bound to an arrow stores the arrow's Unicode character (saved as its code
		// in InternalData[0], like a letter); polled by KeyCode, the arrows do not depend on the keyboard layout.
		public const char Left = '\u2190', Up = '\u2191', Right = '\u2192', Down = '\u2193';
		static readonly (KeyCode key, char c)[] ArrowKeys = { (KeyCode.LeftArrow, Left), (KeyCode.UpArrow, Up), (KeyCode.RightArrow, Right), (KeyCode.DownArrow, Down) };

		public static bool IsArrow(char c) => c >= Left && c <= Down;

		// the space bar: bound as ' ', shown "SPACE"
		public const char Space = ' ';
		public static string KeyLabel(char c) => c == Space ? "SPACE" : c.ToString();

		// a binding as Claude reads it and writes it back to bind_keys: the letter / digit, UP / DOWN / LEFT / RIGHT, SPACE
		public static string KeyName(char c) => c switch
		{
			Left => "LEFT",
			Up => "UP",
			Right => "RIGHT",
			Down => "DOWN",
			Space => "SPACE",
			_ => c.ToString()
		};

		// a key named by Claude / a user: an arrow name, SPACE / ESPACE, else '\0'
		public static char SpecialKeyFromName(string name)
		{
			char a = ArrowFromName(name);
			if (a != '\0') return a;
			string n = name.Trim().ToUpperInvariant();
			return n is "SPACE" or "ESPACE" ? Space : '\0';
		}

		// the arrow pressed this frame, or '\0'
		public static char ArrowDownThisFrame()
		{
			foreach ((KeyCode key, char c) in ArrowKeys) if (InputHelper.IsKeyDownThisFrame(key)) return c;
			return '\0';
		}

		// "UP" / "HAUT" / "\u2191"... -> the arrow character, else '\0'
		public static char ArrowFromName(string name) => name.Trim().ToUpperInvariant() switch
		{
			"LEFT" or "GAUCHE" or "\u2190" => Left,
			"UP" or "HAUT" or "\u2191" => Up,
			"RIGHT" or "DROITE" or "\u2192" => Right,
			"DOWN" or "BAS" or "\u2193" => Down,
			_ => '\0'
		};

		// direction of an arrow character (x right, y up), zero for anything else
		public static Vector2 ArrowDirection(char c) => c switch
		{
			Left => Vector2.left,
			Up => Vector2.up,
			Right => Vector2.right,
			Down => Vector2.down,
			_ => Vector2.zero
		};

		static readonly HashSet<char> KeyLookup = new();
		static bool HasAnyInput;

		// When non-null, replaces the real keyboard: used by the offline QA harness (CircuitTester) so a
		// test sequence can "press" KEY chips deterministically. Always cleared when the test ends.
		[System.ThreadStatic] static HashSet<char> virtualKeys;

		// Held as if pressed on the real keyboard (the player's rate test presses Space to start the user's game)
		public static char InjectedKey;

		// Bumped whenever the held keys may have changed: KEY gates re-run only then (not every step)
		public static int Version;

		public static void SetVirtualKeys(HashSet<char> keys)
		{
			lock (KeyLookup)
			{
				virtualKeys = keys;
				Version++;
			}
		}

		// Call from Main Thread
		public static void RefreshInputState()
		{
			lock (KeyLookup)
			{
				KeyLookup.Clear();
				HasAnyInput = false;
				Version++;
				if (InjectedKey != default(char)) { KeyLookup.Add(InjectedKey); HasAnyInput = true; }

				if (!InputHelper.AnyKeyOrMouseHeldThisFrame) return; // early exit if no key held
				if (InputHelper.CtrlIsHeld || InputHelper.ShiftIsHeld || InputHelper.AltIsHeld) return; // don't trigger key chips if modifier is held

				foreach (KeyCode key in ValidInputKeys)
				{
					// KEY chips are bound by the typed character, but Unity reports keys by physical (US) position:
					// poll the key that produces this character under the current keyboard layout (AZERTY...).
					if (InputHelper.IsKeyHeld(DLS.Game.KeyboardShortcuts.Physical(key)))
					{
						char keyChar = char.ToUpper((char)key);
						KeyLookup.Add(keyChar);
						HasAnyInput = true;
					}
				}
				foreach ((KeyCode key, char c) in ArrowKeys)
					if (InputHelper.IsKeyHeld(key)) { KeyLookup.Add(c); HasAnyInput = true; }
				if (InputHelper.IsKeyHeld(KeyCode.Space)) { KeyLookup.Add(Space); HasAnyInput = true; }
			}
		}

		// Call from Sim Thread
		public static bool KeyIsHeld(char key)
		{
			bool isHeld;

			lock (KeyLookup)
			{
				if (virtualKeys != null) isHeld = virtualKeys.Contains(key);
				else isHeld = HasAnyInput && KeyLookup.Contains(key);
			}

			return isHeld;
		}
	}
}