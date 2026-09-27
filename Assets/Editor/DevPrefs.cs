using UnityEditor;
using UnityEngine;

// One-shot editor settings for unattended runs (the bench, the build scripts, the release script).
//   Unity.exe -projectPath <proj> -executeMethod DevPrefs.DisableSafeModeDialog -quit
// With "Show Enter Safe Mode Dialog" on (the default), a script compilation error makes the editor stop on a
// modal "Enter Safe Mode?" question, so an unattended command line run hangs until someone clicks. With it off,
// the editor enters Safe Mode by itself; -executeMethod then fails (the method is not compiled) and the editor
// quits, and the calling script reports the compile errors from the log instead of waiting forever.
// The preference is per user (EditorPrefs), so this needs to run once per machine.
public static class DevPrefs
{
    const string EnterSafeModeDialogKey = "EnterSafeModeDialog"; // UnityEditor.CoreModule preference

    public static void DisableSafeModeDialog()
    {
        EditorPrefs.SetBool(EnterSafeModeDialogKey, false);
        Debug.Log($"DevPrefs: {EnterSafeModeDialogKey} = {EditorPrefs.GetBool(EnterSafeModeDialogKey, true)} (Safe Mode dialog disabled: compile errors no longer block unattended runs)");
        EditorApplication.Exit(0);
    }

    [MenuItem("Tools/Disable Safe Mode dialog (unattended runs)")]
    public static void DisableSafeModeDialogMenu()
    {
        EditorPrefs.SetBool(EnterSafeModeDialogKey, false);
        EditorUtility.DisplayDialog("Safe Mode dialog", "Disabled: on a compile error the editor enters Safe Mode without asking.", "OK");
    }
}
