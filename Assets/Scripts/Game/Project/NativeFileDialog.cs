using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DLS.Game
{
	// The Windows "Open" dialog (comdlg32 GetOpenFileName): Unity has no file picker in a player build. Modal: the main
	// thread waits while it is open (the simulation thread keeps running). Returns null when cancelled or not on Windows.
	public static class NativeFileDialog
	{
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		class OpenFileName
		{
			public int lStructSize = Marshal.SizeOf(typeof(OpenFileName));
			public IntPtr hwndOwner = IntPtr.Zero;
			public IntPtr hInstance = IntPtr.Zero;
			public string lpstrFilter;
			public string lpstrCustomFilter;
			public int nMaxCustFilter;
			public int nFilterIndex;
			public string lpstrFile;
			public int nMaxFile;
			public string lpstrFileTitle;
			public int nMaxFileTitle;
			public string lpstrInitialDir;
			public string lpstrTitle;
			public int Flags;
			public short nFileOffset;
			public short nFileExtension;
			public string lpstrDefExt;
			public IntPtr lCustData = IntPtr.Zero;
			public IntPtr lpfnHook = IntPtr.Zero;
			public string lpTemplateName;
			public IntPtr pvReserved = IntPtr.Zero;
			public int dwReserved;
			public int FlagsEx;
		}

		[DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

		[DllImport("user32.dll")]
		static extern IntPtr GetActiveWindow();

		const int OFN_NOCHANGEDIR = 0x8, OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;

		static string lastDirectory; // the folder of the last file opened (this session)

		public static string OpenFile(string title)
		{
			if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
			try
			{
				var ofn = new OpenFileName
				{
					hwndOwner = GetActiveWindow(),
					lpstrFilter = "Text files (*.txt, *.hex, *.mem)\0*.txt;*.hex;*.mem\0All files (*.*)\0*.*\0\0",
					nFilterIndex = 1,
					lpstrFile = new string('\0', 2048),
					nMaxFile = 2048,
					lpstrFileTitle = new string('\0', 512),
					nMaxFileTitle = 512,
					lpstrInitialDir = lastDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
					lpstrTitle = title,
					Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
				};
				if (!GetOpenFileName(ofn)) return null;
				string path = ofn.lpstrFile.TrimEnd('\0');
				int nul = path.IndexOf('\0');
				if (nul >= 0) path = path.Substring(0, nul);
				if (path.Length == 0) return null;
				lastDirectory = Path.GetDirectoryName(path);
				return path;
			}
			catch (Exception e)
			{
				UnityEngine.Debug.LogWarning("File dialog failed: " + e.Message);
				return null;
			}
		}
	}
}
