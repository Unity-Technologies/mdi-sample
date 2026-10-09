#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

public static class PlatformUtils
{
    public static bool IsEditor => Application.isEditor;

    public static bool IsPlayer => !Application.isEditor;

    public static RuntimePlatform CurrentRuntimePlatform => Application.platform;

#if UNITY_EDITOR
    public static BuildTarget CurrentBuildTarget => EditorUserBuildSettings.activeBuildTarget;
#endif

    public static bool IsMobileTarget
    {
        get
        {
#if UNITY_EDITOR
            return CurrentBuildTarget == BuildTarget.Android || CurrentBuildTarget == BuildTarget.iOS;
#else
            return Application.platform == RuntimePlatform.Android
                || Application.platform == RuntimePlatform.IPhonePlayer;
#endif
        }
    }

    public static bool IsDesktopTarget
    {
        get
        {
#if UNITY_EDITOR
            return CurrentBuildTarget == BuildTarget.StandaloneWindows64
                || CurrentBuildTarget == BuildTarget.StandaloneWindows
                || CurrentBuildTarget == BuildTarget.StandaloneOSX;
#else
            return Application.platform == RuntimePlatform.WindowsPlayer
                || Application.platform == RuntimePlatform.OSXPlayer;
#endif
        }
    }
}
