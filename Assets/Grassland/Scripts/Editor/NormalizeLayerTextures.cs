using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Forces every layer texture onto identical import settings
public static class NormalizeLayerTextures
{
    public const int Size = 1024;


    // A single format is required for a given platform
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

                if (tex.width != tex.height) nonSquare.Add($"{System.IO.Path.GetFileName(path)} ({tex.width}x{tex.height})");

                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);

                bool dirty = false;
                if (importer.maxTextureSize != Size) { importer.maxTextureSize = Size; dirty = true; }

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
