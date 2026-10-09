using UnityEditor;
using UnityEngine;

// Inspector for GrasslandVolume: a "Bake Placement" button (scatters foliage onto the assigned
// terrain from the masks, avoiding the mask-out meshes) and a GPU-culling toggle. Placement is
// baked on-demand (or on enable) rather than on every inspector edit.
[CustomEditor(typeof(GrasslandVolume))]
public class GrasslandVolumeEditor : Editor
{
    // Grouped in the inspector only — the fields stay where they are on the component, so existing
    // serialized values are untouched. A group is drawn where its first property appears.
    private class PropGroup
    {
        public string Title;
        public string Key;
        public string[] Props;
    }

    private static readonly PropGroup[] Groups =
    {
        new PropGroup
        {
            Title = "Culling", Key = "GrasslandVolume.CullingFoldout",
            Props = new[] { "cullRadius", "cullPadding", "cellCount" },
        },
        new PropGroup
        {
            Title = "Wind", Key = "GrasslandVolume.WindFoldout",
            Props = new[]
            {
                "wind", "windWaveLength", "windWaveSpeed", "windGustiness",
                "windSway", "windSwaySpeed", "windPatchiness", "windShade",
            },
        },
    };

    // Menu items act on the selected volume, falling back to the one in the open scene — with a single
    // volume per scene the selection rarely matters, but it disambiguates when there is more than one.
    internal static GrasslandVolume FindVolume()
    {
        foreach (var go in Selection.gameObjects)
        {
            var v = go.GetComponent<GrasslandVolume>();
            if (v != null) return v;
        }
        return Object.FindFirstObjectByType<GrasslandVolume>();
    }

    public override void OnInspectorGUI()
    {
        DrawGroupedInspector();

        var volume = (GrasslandVolume)target;
        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(volume.terrainCollider == null))
        {
            if (GUILayout.Button("Bake Placement", GUILayout.Height(28)))
            {
                Undo.RegisterFullObjectHierarchyUndo(volume.gameObject, "Bake Placement");
                volume.BakePlacement();
                EditorUtility.SetDirty(volume);
                SceneView.RepaintAll();
            }
        }

        if (GUILayout.Button($"GPU Culling: {(volume.gpuCulling ? "ON" : "OFF")}  (click to toggle)"))
        {
            Undo.RecordObject(volume, "Toggle GPU Culling");
            volume.gpuCulling = !volume.gpuCulling;
            EditorUtility.SetDirty(volume);
            SceneView.RepaintAll();
        }

        // Only actionable where the device reports the capability; shown disabled elsewhere so it is
        // clear the sample asked and the answer was no, rather than that it never looked.
        using (new EditorGUI.DisabledScope(!SystemInfo.supportsMultiDrawIndirectCountBuffer))
        {
            string countState = SystemInfo.supportsMultiDrawIndirectCountBuffer
                ? (volume.mdiCountBuffer ? "ON" : "OFF")
                : "unsupported on this device";
            if (GUILayout.Button($"MDI GPU count buffer: {countState}  (click to toggle)"))
            {
                Undo.RecordObject(volume, "Toggle MDI Count Buffer");
                volume.mdiCountBuffer = !volume.mdiCountBuffer;
                EditorUtility.SetDirty(volume);
                SceneView.RepaintAll();
            }
        }

        if (GUILayout.Button($"Debug LOD: {(volume.debugLod ? "ON (green=LOD0, red=LOD1)" : "OFF")}  (click to toggle)"))
        {
            Undo.RecordObject(volume, "Toggle Debug LOD");
            volume.debugLod = !volume.debugLod;
            EditorUtility.SetDirty(volume);
            SceneView.RepaintAll();
        }

        if (GUILayout.Button($"Debug HUD (counts): {(volume.debugHud ? "ON" : "OFF")}  (click to toggle)"))
        {
            Undo.RecordObject(volume, "Toggle Debug HUD");
            volume.debugHud = !volume.debugHud;
            EditorUtility.SetDirty(volume);
            SceneView.RepaintAll();
        }

        if (GUILayout.Button($"Debug frame timing: {(volume.debugFrameTiming ? "ON" : "OFF")}  (click to toggle)"))
        {
            Undo.RecordObject(volume, "Toggle Debug Frame Timing");
            volume.debugFrameTiming = !volume.debugFrameTiming;
            EditorUtility.SetDirty(volume);
            SceneView.RepaintAll();
        }

        if (volume.terrainCollider == null)
            EditorGUILayout.HelpBox("Assign a Terrain Collider (the field) to enable placement.", MessageType.Warning);

        bool anyTexture = false;
        for (int i = 0; i < volume.LayerCount && !anyTexture; i++)
        {
            var l = volume.Layer(i);
            anyTexture = l != null && (l.nearTexture != null || l.impostorTexture != null);
        }
        if (anyTexture && volume.layerTextureArray == null)
            EditorGUILayout.HelpBox("Layer textures are assigned but no Layer Texture Array is baked — " +
                                    "those draws fall back to procedural shading. Hit Bake Texture Array.",
                                    MessageType.Warning);

        // An impostor mesh with no impostor texture draws as solid unclipped quads — easy to miss and
        // easy to fix, so call it out.
        var unclipped = new System.Text.StringBuilder();
        for (int i = 0; i < volume.LayerCount; i++)
        {
            var l = volume.Layer(i);
            if (l == null || l.farMesh == null || l.impostorTexture != null) continue;
            string n = string.IsNullOrEmpty(l.name) ? (i == 0 ? "Grass" : $"Detail {i - 1}") : l.name;
            unclipped.Append(unclipped.Length == 0 ? n : ", " + n);
        }
        if (unclipped.Length > 0)
            EditorGUILayout.HelpBox(
                $"Far mesh but no Impostor Texture on: {unclipped}. Those planes draw as solid " +
                "unclipped quads — assign an RGBA bake (RGB = color, A = coverage).", MessageType.Warning);

    }

    private void DrawGroupedInspector()
    {
        serializedObject.Update();

        var drawn = new System.Collections.Generic.HashSet<string>();
        SerializedProperty it = serializedObject.GetIterator();
        bool enterChildren = true;

        while (it.NextVisible(enterChildren))
        {
            enterChildren = false;

            if (it.propertyPath == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(it);
                continue;
            }

            PropGroup group = null;
            foreach (var g in Groups)
                if (System.Array.IndexOf(g.Props, it.propertyPath) >= 0) { group = g; break; }

            if (group == null)
            {
                EditorGUILayout.PropertyField(it, true);
                continue;
            }

            if (!drawn.Add(group.Title)) continue;

            bool open = SessionState.GetBool(group.Key, true);
            bool next = EditorGUILayout.Foldout(open, group.Title, true, EditorStyles.foldoutHeader);
            if (next != open) SessionState.SetBool(group.Key, next);
            if (!next) continue;

            EditorGUI.indentLevel++;
            foreach (string name in group.Props)
            {
                SerializedProperty p = serializedObject.FindProperty(name);
                if (p != null) EditorGUILayout.PropertyField(p, true);
            }
            EditorGUI.indentLevel--;
        }

        serializedObject.ApplyModifiedProperties();
    }
}
