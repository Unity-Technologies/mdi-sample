using System.IO;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

[InitializeOnLoad]
public static class DefaultBuildProfile
{
    private const string AndroidProfileGuid = "3ef2aef6ff4e142f3853fe74f28fed26";
    private const string MarkerPath = "Library/DefaultBuildProfileApplied";

    static DefaultBuildProfile()
    {
        if (Application.isBatchMode || File.Exists(MarkerPath)) return;
        EditorApplication.delayCall += Apply;
    }

    private static void Apply()
    {
        File.WriteAllText(MarkerPath, AndroidProfileGuid);

        string path = AssetDatabase.GUIDToAssetPath(AndroidProfileGuid);
        var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(path);
        if (profile == null || BuildProfile.GetActiveBuildProfile() == profile) return;

        BuildProfile.SetActiveBuildProfile(profile);
        Debug.Log($"Activated the default build profile: {path}");
    }
}
