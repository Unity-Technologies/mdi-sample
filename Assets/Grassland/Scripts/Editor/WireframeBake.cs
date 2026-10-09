using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Prepares a mesh for the Debug/LOD Preview shader
public static class WireframeBake
{
    private const string OutputFolder = "Assets/Grassland/Mesh/Wireframe";

    [MenuItem("Grassland/Bake Wireframe Barycentrics")]
    private static void Bake()
    {
        var filters = Selection.GetFiltered<MeshFilter>(SelectionMode.Deep);
        if (filters.Length == 0)
        {
            EditorUtility.DisplayDialog("Bake Wireframe Barycentrics",
                "Select one or more objects with a MeshFilter first.", "OK");
            return;
        }

        Directory.CreateDirectory(OutputFolder);
        int done = 0;
        var skipped = new List<string>();

        foreach (var filter in filters)
        {
            Mesh src = filter.sharedMesh;
            if (src == null) { skipped.Add($"{filter.name}: no mesh"); continue; }
            if (!src.isReadable)
            {
                // Imported meshes are GPU-only unless this is ticked
                skipped.Add($"{filter.name}: '{src.name}' is not readable — tick Read/Write Enabled on its importer");
                continue;
            }

            Mesh baked = Unweld(src);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{OutputFolder}/{src.name}_wire.asset");
            AssetDatabase.CreateAsset(baked, path);

            Undo.RecordObject(filter, "Bake Wireframe Barycentrics");
            filter.sharedMesh = baked;
            EditorUtility.SetDirty(filter);
            done++;
            Debug.Log($"[WireframeBake] {filter.name}: {src.triangles.Length / 3} tris -> {baked.vertexCount} verts, saved to {path}", filter);
        }

        AssetDatabase.SaveAssets();
        if (skipped.Count > 0)
            Debug.LogWarning("[WireframeBake] skipped:\n  " + string.Join("\n  ", skipped));
        Debug.Log($"[WireframeBake] baked {done} mesh(es).");
    }

    private static Mesh Unweld(Mesh src)
    {
        Vector3[] srcVerts = src.vertices;
        Vector3[] srcNormals = src.normals;
        Vector2[] srcUV = src.uv;
        int[] tris = src.triangles;
        int n = tris.Length;

        var verts = new Vector3[n];
        var normals = srcNormals.Length == srcVerts.Length ? new Vector3[n] : null;
        var uvs = srcUV.Length == srcVerts.Length ? new Vector2[n] : null;
        var bary = new List<Vector3>(n);
        var indices = new int[n];

        // One vertex per index, each corner tagged with the barycentric basis vector it owns.
        for (int i = 0; i < n; i++)
        {
            int s = tris[i];
            verts[i] = srcVerts[s];
            if (normals != null) normals[i] = srcNormals[s];
            if (uvs != null) uvs[i] = srcUV[s];
            int corner = i % 3;
            bary.Add(corner == 0 ? new Vector3(1, 0, 0)
                   : corner == 1 ? new Vector3(0, 1, 0)
                                 : new Vector3(0, 0, 1));
            indices[i] = i;
        }

        var mesh = new Mesh
        {
            name = src.name + "_wire",
            indexFormat = n > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16,
        };
        mesh.vertices = verts;
        if (normals != null) mesh.normals = normals;
        if (uvs != null) mesh.uv = uvs;
        mesh.SetUVs(1, bary);                      // UV1 == TEXCOORD1 in the shader
        mesh.SetTriangles(indices, 0, true);
        if (normals == null) mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
