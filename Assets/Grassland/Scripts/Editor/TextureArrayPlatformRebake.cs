using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public class TextureArrayPlatformRebake : IActiveBuildTargetChanged
{
    public int callbackOrder => 0;

    public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget)
    {
        if (Application.isBatchMode) return;
        EditorApplication.delayCall += Rebake;
    }

    private static void Rebake()
    {
        var volume = GrasslandVolumeEditor.FindVolume();
        if (volume == null)
        {
            Debug.LogWarning("[TextureArrayBake] Build target changed but no GrasslandVolume is loaded. " +
                             "Open MDI-Demo and run Grassland > Bake Texture Array before building.");
            return;
        }
        if (TextureArrayBake.Bake(volume)) SceneView.RepaintAll();
    }
}
