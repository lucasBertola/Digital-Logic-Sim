using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace DLS.SaveSystem
{
	// Projects shipped WITH the app (StreamingAssets/BundledProjects/<name>, copied there at build time by
	// BuildTools from the developer's save folder, listed in BundledProjects.txt at the repo root).
	// On the main menu:
	//   - no local project of that name           -> installed silently;
	//   - a local project with the same content    -> nothing (the marker is written so it is not asked again);
	//   - a local project that differs, and this bundle version was not seen yet -> the user is asked: replace
	//     (theirs goes to "Deleted Projects", nothing is lost) or keep (the marker is written: not asked again
	//     for this bundle version; a NEW version of the bundle asks again).
	// The marker is <project>/.bundle.hash = the hash of the bundle it was installed from / declined.
	public static class BundledProjects
	{
		public const string BundleFolderName = "BundledProjects";
		public const string HashFileName = "bundle.hash";   // in the bundle
		public const string MarkerFileName = ".bundle.hash"; // in the local project

		public static string BundleRoot => Path.Combine(Application.streamingAssetsPath, BundleFolderName);

		public enum Action { Nothing, Install, Ask }

		// Pure decision (unit-tested): what to do for one bundled project.
		public static Action Decide(string bundleHash, bool localExists, string localContentHash, string localMarkerHash)
		{
			if (string.IsNullOrEmpty(bundleHash)) return Action.Nothing;
			if (!localExists) return Action.Install;
			if (localContentHash == bundleHash) return Action.Nothing;          // same thing already there
			if (localMarkerHash == bundleHash) return Action.Nothing;           // this bundle version was already offered and declined
			if (localMarkerHash != null && localContentHash == localMarkerHash) return Action.Install; // untouched since it was installed: silent update
			return Action.Ask;                                                  // the user modified it: their call
		}

		public class Pending
		{
			public string name;
			public string bundlePath;
			public string bundleHash;
		}

		// Installs what can be installed silently and returns the projects that need the user's answer.
		public static List<Pending> CheckOnStartup()
		{
			var toAsk = new List<Pending>();
			try
			{
				if (!Directory.Exists(BundleRoot)) return toAsk;
				foreach (string dir in Directory.GetDirectories(BundleRoot))
				{
					string name = Path.GetFileName(dir);
					string bundleHash = ReadText(Path.Combine(dir, HashFileName)) ?? HashDirectory(dir);
					bool localExists = Loader.ProjectExists(name);
					string localPath = SavePaths.GetProjectPath(name);
					string localHash = localExists ? HashDirectory(localPath) : null;
					string marker = localExists ? ReadText(Path.Combine(localPath, MarkerFileName)) : null;
					switch (Decide(bundleHash, localExists, localHash, marker))
					{
						case Action.Install:
							if (localExists) Directory.Delete(localPath, true); // untouched since install: replaced in place
							Install(name, dir, bundleHash);
							break;
						case Action.Nothing:
							if (localExists && localHash == bundleHash && marker != bundleHash) WriteMarker(localPath, bundleHash);
							break;
						case Action.Ask:
							toAsk.Add(new Pending { name = name, bundlePath = dir, bundleHash = bundleHash });
							break;
					}
					// the shipped RUN FAST decisions reach a local copy that is kept too (its own entries win: they fit its chips)
					if (Loader.ProjectExists(name)) DLS.Game.FastCacheFile.MergeMissing(Path.Combine(dir, FastCacheFileName), Path.Combine(localPath, FastCacheFileName));
				}
			}
			catch (Exception e) { Debug.LogWarning("BundledProjects: " + e.Message); }
			return toAsk;
		}

		public static void Replace(Pending p)
		{
			if (Loader.ProjectExists(p.name)) Saver.DeleteProject(p.name); // moved to "Deleted Projects"
			Install(p.name, p.bundlePath, p.bundleHash);
		}

		public static void Keep(Pending p)
		{
			if (Loader.ProjectExists(p.name)) WriteMarker(SavePaths.GetProjectPath(p.name), p.bundleHash);
		}

		static void Install(string name, string bundleDir, string bundleHash)
		{
			string target = SavePaths.GetProjectPath(name);
			CopyDirectory(bundleDir, target, HashFileName);
			WriteMarker(target, bundleHash);
			Debug.Log($"BundledProjects: installed \"{name}\"");
		}

		static void WriteMarker(string projectPath, string hash)
		{
			try { File.WriteAllText(Path.Combine(projectPath, MarkerFileName), hash); } catch (Exception) { /* ignore */ }
		}

		static string ReadText(string path)
		{
			try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; } catch (Exception) { return null; }
		}

		static void CopyDirectory(string from, string to, string skipFile)
		{
			Directory.CreateDirectory(to);
			foreach (string f in Directory.GetFiles(from))
			{
				string fn = Path.GetFileName(f);
				if (fn == skipFile || !IsContentFile(fn)) continue;
				File.Copy(f, Path.Combine(to, fn), true);
			}
			foreach (string d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)), skipFile);
		}

		// What is NOT part of a project's content for bundling: the user's deleted chips, their Ask Claude conversation
		// (private, and not a circuit), markers, hashes, Unity .meta files.
		public const string DeletedChipsFolder = "Deleted Chips";
		public const string ConversationFile = "AskClaudeConversation.json";
		public const string KeyFileName = "anthropic_key.txt"; // the Anthropic key typed in the app (AskClaude.KeyPath)

		// Never in a release, wherever they are (user, 2026-10-06: the Claude conversation history must never ship;
		// the key neither). BuildTools fails the build and publierRelease.bat refuses to zip if one is found.
		public static bool IsPrivateFile(string fileName) => fileName == ConversationFile || fileName == KeyFileName;

		public static List<string> PrivateFilesUnder(string root)
		{
			if (!Directory.Exists(root)) return new List<string>();
			return Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => IsPrivateFile(Path.GetFileName(f))).ToList();
		}
		// RUN FAST decisions: shipped with the project, but a cache, not content: it grows whenever RUN FAST meets a new
		// module, which must not make the local copy look "modified by the user" (KEEP / REPLACE question)
		public const string FastCacheFileName = "FastModels.json";
		public static bool IsContentFile(string relativePath)
		{
			string n = Path.GetFileName(relativePath);
			if (n == MarkerFileName || n == HashFileName || IsPrivateFile(n) || n.EndsWith(".meta") || n.StartsWith(".")) return false;
			string p = relativePath.Replace('\\', '/');
			return !p.StartsWith(DeletedChipsFolder + "/") && !p.Contains("/" + DeletedChipsFolder + "/");
		}

		// Content hash of a project folder: relative paths + bytes of every content file, sorted.
		public static string HashDirectory(string root)
		{
			using SHA1 sha = SHA1.Create();
			var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
				.Where(f => IsContentFile(f.Substring(root.Length).TrimStart('\\', '/')) && Path.GetFileName(f) != FastCacheFileName)
				.Select(f => f.Substring(root.Length).Replace('\\', '/').TrimStart('/'))
				.OrderBy(f => f, StringComparer.Ordinal)
				.ToList();
			var sb = new StringBuilder();
			foreach (string rel in files)
			{
				byte[] bytes = File.ReadAllBytes(Path.Combine(root, rel));
				sb.Append(rel).Append(':').Append(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")).Append('\n');
			}
			return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").ToLowerInvariant();
		}
	}
}
