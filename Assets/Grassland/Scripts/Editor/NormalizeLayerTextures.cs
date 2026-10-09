using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Forces every layer texture onto identical import settings. Texture2DArray slices must agree on
// size, format and mip count, and Graphics.CopyTexture (used by TextureArrayBake) refuses anything
// else — so rather than chase mismatches one bake at a time, normalise them all in one pass.
//
// Import settings only: the source files are untouched, and everything here is reversible from the
// texture's own inspector.
public static class NormalizeLayerTextures
{
    public const int Size = 1024;

    // One format PER PLATFORM, not one format for all of them. Pinning DXT5 on the default platform
    // (which is what this used to do) hands a desktop block-compressed format to Android, where it is
    // not a valid texture format at all — the importer flags it, and the Unity Graphics Kernel then
    // crashes uploading it: SIGSEGV in CopyUploadData, because the staging allocation is sized from a
    // different format description than the source data is laid out for.
    //
    // A single format is still required WITHIN a platform: Texture2DArray slices must agree, and
    // Unity's automatic compression would otherwise pick DXT1 for the grass impostor (its alpha is
    // uniformly opaque) and DXT5 for the rest.
    private static readonly (string platform, TextureImporterFormat format)[] Formats =
    {
        ("DefaultTexturePlatform", TextureImporterFormat.DXT5),     // desktop / editor
        ("Standalone",             TextureImporterFormat.DXT5),
        ("Android",                TextureImporterFormat.ASTC_6x6),
        ("iPhone",                 TextureImporterFormat.ASTC_6x6),
    };

    [MenuItem("Grassland/Normalize Layer Textures", false, 100)]
    private static void MenuNormalize() => Normalize(GrasslandVolumeEditor.FindVolume());

    [MenuItem("Grassland/Normalize Layer Textures", true)]
    private static bool MenuNormalizeValidate() => GrasslandVolumeEditor.FindVolume() != null;

    public static bool Normalize(GrasslandVolume volume)
    {
        if (volume == null) return false;

        var textures = new List<Texture2D>();
        for (int i = 0; i < volume.LayerCount; i++)
        {
            var layer = volume.Layer(i);
            if (layer == null) continue;
            if (layer.nearTexture != null) textures.Add(layer.nearTexture);
            if (layer.impostorTexture != null) textures.Add(layer.impostorTexture);
        }
        if (textures.Count == 0)
        {
            EditorUtility.DisplayDialog("Normalize Layer Textures", "No layer textures are assigned.", "OK");
            return false;
        }

        var changed = new List<string>();
        var nonSquare = new List<string>();

        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (Texture2D tex in textures)
            {
                string path = AssetDatabase.GetAssetPath(tex);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                // maxTextureSize clamps the LONGEST side and keeps aspect, so a non-square source can
                // never land on Size x Size. Those have to be fixed in the file itself.
                if (tex.width != tex.height) nonSquare.Add($"{System.IO.Path.GetFileName(path)} ({tex.width}x{tex.height})");

                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);

                bool dirty = false;
                if (importer.maxTextureSize != Size) { importer.maxTextureSize = Size; dirty = true; }

                // Every texture keeps its alpha channel even where the coverage actually lives in RGB —
                // those layers should be set to CoverageSource.Luminance, since the alpha they carry is
                // uniformly opaque and clipping against it would discard nothing.
                if (importer.alphaSource != TextureImporterAlphaSource.FromInput)
                { importer.alphaSource = TextureImporterAlphaSource.FromInput; dirty = true; }

                foreach ((string name, TextureImporterFormat format) in Formats)
                {
                    TextureImporterPlatformSettings platform = name == "DefaultTexturePlatform"
                        ? importer.GetDefaultPlatformTextureSettings()
                        : importer.GetPlatformTextureSettings(name);

                    if (platform.overridden && platform.format == format && platform.maxTextureSize == Size)
                        continue;

                    platform.overridden = true;
                    platform.format = format;
                    platform.maxTextureSize = Size;
                    importer.SetPlatformTextureSettings(platform);
                    dirty = true;
                }
                if (importer.textureCompression != TextureImporterCompression.Compressed)
                { importer.textureCompression = TextureImporterCompression.Compressed; dirty = true; }
                if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; dirty = true; }
                // Foliage is cut out, so the alpha has to survive as coverage and the color has to be
                // dilated into the transparent texels or the cutout edges fringe.
                if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; dirty = true; }
                if (!settings.mipMapsPreserveCoverage)
                {
                    settings.mipMapsPreserveCoverage = true;
                    settings.alphaTestReferenceValue = 0.4f;
                    importer.SetTextureSettings(settings);
                    dirty = true;
                }

                if (dirty)
                {
                    importer.SaveAndReimport();
                    changed.Add(System.IO.Path.GetFileName(path));
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        Debug.Log($"[NormalizeLayerTextures] {textures.Count} texture(s) checked, {changed.Count} reimported at " +
                  $"max {Size}px" + (changed.Count > 0 ? ": " + string.Join(", ", changed) : "."));
        if (nonSquare.Count > 0)
            Debug.LogWarning("[NormalizeLayerTextures] these are not square, so they will import as " +
                             "non-square and the array bake will reject them — resize the source files:\n  " +
                             string.Join("\n  ", nonSquare));
        return true;
    }
}
