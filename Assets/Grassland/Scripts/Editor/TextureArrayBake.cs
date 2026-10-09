using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Packs a GrasslandVolume's per-LOD textures into a Texture2DArray, one slice per (layer, LOD) in
// draw order: slice = layerIndex * 2 + lod, matching DrawId in ProceduralRenderingFeature.
//
// Baked as an asset rather than assembled at runtime so the slices stay compressed. A runtime
// RenderTexture array would be uncompressed — roughly four times the memory for the same content.
public static class TextureArrayBake
{
    private const int Lods = 2;
    private const string OutputFolder = "Assets/Grassland/Textures";

    [MenuItem("Grassland/Bake Texture Array", false, 101)]
    private static void MenuBake()
    {
        if (Bake(GrasslandVolumeEditor.FindVolume())) SceneView.RepaintAll();
    }

    [MenuItem("Grassland/Bake Texture Array", true)]
    private static bool MenuBakeValidate() => GrasslandVolumeEditor.FindVolume() != null;

    public static bool Bake(GrasslandVolume volume)
    {
        if (volume == null) return false;

        int slices = volume.LayerCount * Lods;
        var sources = new List<Texture2D>(slices);
        for (int i = 0; i < volume.LayerCount; i++)
        {
            var layer = volume.Layer(i);
            sources.Add(layer?.nearTexture);
            sources.Add(layer?.impostorTexture);
        }

        // Every slice shares one size and format, so the first assigned texture sets the contract and
        // the rest must match it. Reporting all mismatches at once beats fixing them one bake at a time.
        Texture2D reference = sources.Find(t => t != null);
        if (reference == null)
        {
            EditorUtility.DisplayDialog("Bake Texture Array",
                "No layer textures are assigned, so there is nothing to pack.", "OK");
            return false;
        }

        var problems = new List<string>();
        for (int s = 0; s < sources.Count; s++)
        {
            Texture2D t = sources[s];
            if (t == null) continue;
            if (t.width != reference.width || t.height != reference.height)
                problems.Add($"slice {s} '{t.name}' is {t.width}x{t.height}, expected {reference.width}x{reference.height}");
            else if (t.format != reference.format)
                problems.Add($"slice {s} '{t.name}' is {t.format}, expected {reference.format}");
            else if (t.mipmapCount != reference.mipmapCount)
                problems.Add($"slice {s} '{t.name}' has {t.mipmapCount} mips, expected {reference.mipmapCount}");
        }
        if (problems.Count > 0)
        {
            Debug.LogError("[TextureArrayBake] every slice must share size, format and mip count:\n  " +
                           string.Join("\n  ", problems));
            EditorUtility.DisplayDialog("Bake Texture Array",
                "Slices must all share size, format and mip count. See the Console for the list.", "OK");
            return false;
        }

        var array = new Texture2DArray(reference.width, reference.height, slices,
                                       reference.format, reference.mipmapCount > 1, linear: false)
        {
            name = volume.gameObject.name + "_LayerTextures",
            wrapMode = reference.wrapMode,
            filterMode = reference.filterMode,
            anisoLevel = reference.anisoLevel,
        };

        for (int s = 0; s < slices; s++)
        {
            if (sources[s] == null) continue;   // unused slot: left as-is, never sampled (textured = 0)
            for (int mip = 0; mip < reference.mipmapCount; mip++)
                Graphics.CopyTexture(sources[s], 0, mip, array, s, mip);
        }
        array.Apply(updateMipmaps: false, makeNoLongerReadable: false);

        Directory.CreateDirectory(OutputFolder);
        string path = $"{OutputFolder}/{array.name}.asset";
        var existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
        if (existing != null)
        {
            EditorUtility.CopySerialized(array, existing);
            Object.DestroyImmediate(array);
            array = existing;
            EditorUtility.SetDirty(array);
        }
        else
        {
            AssetDatabase.CreateAsset(array, path);
        }
        AssetDatabase.SaveAssets();

        Undo.RecordObject(volume, "Bake Texture Array");
        volume.layerTextureArray = array;
        EditorUtility.SetDirty(volume);

        // The array is a serialized asset: its pixel data is frozen at whatever format the sources were
        // LOADED as, and it is never re-imported per platform. So the active build target at bake time
        // decides what every platform gets. Baking on desktop and shipping to Android is how a BC
        // format reaches a device that has no BC support — which the graphics kernel crashes on rather
        // than rejecting. Name the target in the log so a mismatch is visible instead of silent.
        var target = EditorUserBuildSettings.activeBuildTarget;
        Debug.Log($"[TextureArrayBake] {slices} slices at {reference.width}x{reference.height} " +
                  $"{reference.format}, {reference.mipmapCount} mips -> {path}\n" +
                  $"Baked for active build target '{target}'. Re-bake after switching platform: this " +
                  $"asset is not re-imported per platform.", array);
        return true;
    }
}
