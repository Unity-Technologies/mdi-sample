using System.Collections.Generic;
using System.Diagnostics.Contracts;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

public enum TextureUse
{
    ColorAndCoverage,  // RGB = albedo, A = coverage
    CoverageOnly,      // the texture is a mask; color comes from the material's colormap gradient, so
                       // an impostor matches the procedural blades it replaces across the LOD boundary.
}

// Each LOD takes its own texture, read according to the layer's textureUse:
//   ColorAndCoverage -> RGB is albedo, ALPHA is coverage. Authored detail foliage.
//   CoverageOnly     -> the texture is a mask; color still comes from the colormap gradient, so a
//                       grass impostor shades identically to the blades it replaces.
//   no texture       -> procedural shading, nothing clipped: the mesh's own silhouette is the
//                       coverage
public enum CoverageSource
{
    Alpha,       // RGBA cutout, e.g. a flower bake
    Luminance,   // grayscale mask, e.g. the grass impostor
}

[System.Serializable]
public class FoliageLayer
{
    public string name = "Layer";
    public bool visible = true;
    public Texture2D mask;
    public Vector2 maskOffset = Vector2.zero;
    public TextureUse textureUse = TextureUse.ColorAndCoverage;
    [Tooltip("Which channels of this layer's textures carry coverage. Alpha for an RGBA cutout; " +
             "Luminance for a grayscale mask such as the grass impostor")]
    public CoverageSource coverageFrom = CoverageSource.Alpha;
    public Mesh nearMesh;
    public Texture2D nearTexture;
    public Mesh farMesh;
    public Texture2D impostorTexture;
    [Range(0.0f, 1.0f)]
    public float alphaCutoff = 0.4f;
    public float spacing = 2.0f;
    [Range(0.0f, 4.0f)]
    public float density = 1.0f;
    public float lodDistance = 35.0f;
    [Range(0.0f, 3.0f)]
    public float windScale = 1.0f;
    public Vector3 clumpScale = Vector3.one;
    public Vector3 impostorScale = Vector3.one;
    [Range(0.0f, 1.0f)]
    public float heightVariance = 0.25f;

    [System.NonSerialized] public Vector4[] positions;
}

[ExecuteAlways]
public class GrasslandVolume : MonoBehaviour
{
    // The volume the renderer feature draws. Last-enabled wins (typically one per scene).
    public static GrasslandVolume Active { get; private set; }

    [Header("Scene geometry")]
    public Collider terrainCollider;
    [Tooltip("Baked from every layer's near/impostor textures, one slice per layer per LOD in layer " +
             "order. A single multi-draw call binds one texture for all its sub-draws, so the per-LOD " +
             "textures have to arrive as slices. Build it with Bake Texture Array.")]
    public Texture2DArray layerTextureArray;

    [Header("Foliage")]
    public FoliageLayer grass = new FoliageLayer { name = "Grass", spacing = 2.0f, density = 1.0f, lodDistance = 45.0f, windScale = 1.0f };

    public FoliageLayer[] details = new FoliageLayer[0];

    public float cullRadius = 120.0f;
    public float cullPadding = 5.0f;
    [Tooltip("Number of foliage cells the field is split into, making every (layer, LOD, cell) its " +
             "own indirect sub-draw: 3 layers x 2 LODs x 64 cells = 384 sub-draws in one call. Rounded " +
             "to a square grid. More cells means finer culling granularity and more sub-draws per " +
             "call, at one draw call per cell on the portable path.")]
    [Range(1, 256)] public int cellCount = 64;
    // Toggled via the inspector button; read per-frame by the feature (instant, no rebuild).
    [HideInInspector] public bool gpuCulling = true;
    // Debug: tint clumps by LOD (green = near/LOD0, red = far/LOD1). Instant, read per-frame.
    [HideInInspector] public bool debugLod = false;
    // Debug: on-screen HUD of drawn instance/vertex counts (total + per LOD). Instant, per-frame.
    [HideInInspector] public bool debugHud = false;
    [HideInInspector] public bool debugFrameTiming = false;
    // Let the GPU decide how many of the multi-draw's sub-draws run, via a count buffer. Only has an
    // effect where SystemInfo.supportsMultiDrawIndirectCountBuffer; falls back to submitting every
    // slot, empty ones included. Here to A/B the two on a device — both must render the same field.
    [HideInInspector] public bool mdiCountBuffer = true;
    [Tooltip("Wind: xyz = direction, w = strength (0 = no wind). Per-layer windScale multiplies this.")]
    public Vector4 wind = new Vector4(1.0f, -0.2f, 0.3f, 2.5f);
    public float windWaveLength = 26.0f;
    [Tooltip("How fast (m/s) the gust fronts travel along the wind direction.")]
    public float windWaveSpeed = 7.0f;
    [Range(0.0f, 1.0f)]
    public float windGustiness = 0.7f;
    [Range(0.0f, 1.0f)]
    public float windSway = 0.3f;
    public float windSwaySpeed = 1.6f;
    [Range(0.0f, 1.0f)]
    public float windPatchiness = 0.5f;
    [Range(0.0f, 1.0f)]
    public float windShade = 0.5f;
    public float lodBlend = 12.0f;

    [Header("Quality")]
    public GrassQuality MobileLowQuality;
    [FormerlySerializedAs("MobileQuality")] public GrassQuality MobileHighQuality;
    public GrassQuality DesktopQuality;
    public enum MobileQualityLevel { Low, High, HighMsaa }
    [System.NonSerialized] public MobileQualityLevel mobileQualityLevel = MobileQualityLevel.High;

    [Header("Placement bake (per-cell jitter)")]
    [Range(0.0f, 1.0f)]
    public float positionJitter = 0.35f;
    [Range(0.0f, 1.0f)]
    public float rotationJitter = 1.0f;

    private const int MaskReadResolution = 1024;

    // ---- Exposed to the render feature -------------------------------------------------------
    public int ConfigVersion { get; private set; }
    public int DetailCount => details != null ? details.Length : 0;
    public int LayerCount => 1 + DetailCount;
    public FoliageLayer Layer(int i) => i == 0 ? grass : details[i - 1];

    public Vector4[] LayerPositions(int i) { var l = Layer(i); return l != null ? l.positions : null; }

    public GrassQuality ActiveQuality
    {
        get
        {
            if (PlatformUtils.IsMobileTarget) return mobileQualityLevel == MobileQualityLevel.Low ? MobileLowQuality : MobileHighQuality;
            if (PlatformUtils.IsDesktopTarget) return DesktopQuality;
            return null;
        }
    }

    public float ResolvedLodDistance(FoliageLayer layer)
    {
        var q = ActiveQuality;
        if (q != null && q.lodDistance >= 0.0f) return q.lodDistance;
        return layer != null ? layer.lodDistance : 0.0f;
    }

    public float ResolvedLodBlend
    {
        get { var q = ActiveQuality; return (q != null && q.lodBlend >= 0.0f) ? q.lodBlend : lodBlend; }
    }

    public float ResolvedCullRadius
    {
        get { var q = ActiveQuality; return (q != null && q.cullRadius > 0.0f) ? q.cullRadius : cullRadius; }
    }

    public int ResolvedCellCount
    {
        get { var q = ActiveQuality; return (q != null && q.cellCount > 0) ? q.cellCount : cellCount; }
    }

    public Bounds FieldBounds => terrainCollider != null ? terrainCollider.bounds : new Bounds(transform.position, Vector3.one);
    public Vector2 FieldOriginXZ { get { var c = FieldBounds.center; return new Vector2(c.x, c.z); } }
    public float TerrainExtent { get { var s = FieldBounds.size; return Mathf.Max(0.01f, Mathf.Max(s.x, s.z)); } }
    public float WorldExtentX => TerrainExtent;

    private void OnEnable()
    {
        Active = this;
        EnsurePlacement();     
        ConfigVersion++;
    }

    private void OnDisable()
    {
        if (Active == this) Active = null;
        if (_frameTiming != null) _frameTiming.enabled = false;
        RestoreMsaa();
    }

    private void Update()
    {
        if (transform.hasChanged)
        {
            transform.hasChanged = false;
            ConfigVersion++;
        }
        UpdateAutoQuality();
        WatchResolvedQuality();
        UpdateMsaa();
        UpdateFrameTiming();
    }

    private const int HighMsaaSamples = 2;
    private const int NoMsaaSamples = 1;
    private int _defaultMsaa = -1;

    private static string QualityLevelName(MobileQualityLevel level) =>
        level == MobileQualityLevel.HighMsaa ? "High (MSAA)" : level.ToString();

    private static UniversalRenderPipelineAsset ActiveUrpAsset =>
        GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

    private void UpdateMsaa()
    {
        if (!Application.isPlaying || Application.platform != RuntimePlatform.Android) return;

        var urp = ActiveUrpAsset;
        if (urp == null) return;

        if (_defaultMsaa < 0) _defaultMsaa = urp.msaaSampleCount;

        int wanted = mobileQualityLevel == MobileQualityLevel.HighMsaa ? HighMsaaSamples : NoMsaaSamples;
        if (urp.msaaSampleCount != wanted) urp.msaaSampleCount = wanted;
    }

    private void RestoreMsaa()
    {
        var urp = ActiveUrpAsset;
        if (urp != null && _defaultMsaa >= 0) urp.msaaSampleCount = _defaultMsaa;
        _defaultMsaa = -1;
    }

    private const float AutoQualityWarmupSeconds = 5.0f;
    private const int AutoQualitySampleFrames = 100;
    private const int AutoQualityDiscardSlowest = 10;
    private const double AutoQualityCpuThresholdMs = 35.0;

    private bool _autoQualityDone;
    private float _autoQualityStartTime = -1.0f;
    private readonly List<double> _autoQualitySamples = new List<double>(512);
    private readonly UnityEngine.FrameTiming[] _autoQualityTiming = new UnityEngine.FrameTiming[1];

    private void UpdateAutoQuality()
    {
        if (_autoQualityDone) return;
        if (!Application.isPlaying || Application.platform != RuntimePlatform.Android)
        {
            _autoQualityDone = true;
            return;
        }

        if (_autoQualityStartTime < 0.0f) _autoQualityStartTime = Time.realtimeSinceStartup;
        float elapsed = Time.realtimeSinceStartup - _autoQualityStartTime;
        if (elapsed < AutoQualityWarmupSeconds) return;

        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, _autoQualityTiming) > 0 && _autoQualityTiming[0].cpuFrameTime > 0.0)
            _autoQualitySamples.Add(_autoQualityTiming[0].cpuFrameTime);

        if (_autoQualitySamples.Count < AutoQualitySampleFrames) return;

        _autoQualitySamples.Sort();
        int kept = AutoQualitySampleFrames - AutoQualityDiscardSlowest;
        double sumCpuMs = 0.0;
        for (int i = 0; i < kept; i++) sumCpuMs += _autoQualitySamples[i];
        double averageCpuMs = sumCpuMs / kept;
        double maxCpuMs = _autoQualitySamples[AutoQualitySampleFrames - 1];
        mobileQualityLevel = averageCpuMs >= AutoQualityCpuThresholdMs ? MobileQualityLevel.Low : MobileQualityLevel.High;
        _autoQualityDone = true;
        Debug.Log($"Grassland auto quality: average CPU frame {averageCpuMs:0.00} ms over {kept} of {AutoQualitySampleFrames} frames (slowest dropped, max {maxCpuMs:0.00}), active {QualityLevelName(mobileQualityLevel)}");
        _autoQualitySamples.Clear();
    }

    private FrameTiming _frameTiming;

    private void UpdateFrameTiming()
    {
        if (_frameTiming == null)
        {
            if (!debugFrameTiming) return;
            if (!TryGetComponent(out _frameTiming))
                _frameTiming = gameObject.AddComponent<FrameTiming>();
            _frameTiming.hideFlags = HideFlags.HideAndDontSave;
        }
        _frameTiming.enabled = debugFrameTiming;
    }

    private GrassQuality _watchedQuality;
    private float _watchedLodDistance, _watchedLodBlend, _watchedCullRadius;
    private int _watchedCellCount;

    private void WatchResolvedQuality()
    {
        GrassQuality q = ActiveQuality;
        float lodDistance = ResolvedLodDistance(grass);
        float lodBlendNow = ResolvedLodBlend;
        float cullRadiusNow = ResolvedCullRadius;
        int cellCountNow = ResolvedCellCount;

        if (q == _watchedQuality && lodDistance == _watchedLodDistance && lodBlendNow == _watchedLodBlend
            && cullRadiusNow == _watchedCullRadius && cellCountNow == _watchedCellCount) return;

        _watchedQuality = q;
        _watchedLodDistance = lodDistance;
        _watchedLodBlend = lodBlendNow;
        _watchedCullRadius = cullRadiusNow;
        _watchedCellCount = cellCountNow;
        ConfigVersion++;
    }

    // Debug HUD
    private GUIStyle _hudStyle;
    private void OnGUI()
    {
        float hudTop = 10.0f;
        if (debugHud) DrawStatsHud(ref hudTop);
        if (debugFrameTiming) DrawFrameTimingHud(ref hudTop);
        if (debugLod) DrawLodLegend();
        if (Application.platform == RuntimePlatform.Android) DrawQualityToggle();
    }

    private GUIStyle _buttonStyle;

    private void DrawQualityToggle()
    {
        if (_buttonStyle == null)
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, padding = new RectOffset(10, 10, 8, 8) };

        EnsureHudStyle();
        InitHudTextures();

        const float margin = 10.0f, gap = 6.0f;
        GUIContent content = new GUIContent("Toggle Quality");
        Vector2 size = _buttonStyle.CalcSize(content);
        GUIContent active = new GUIContent($"Active: {QualityLevelName(mobileQualityLevel)}");
        Vector2 activeSize = _hudStyle.CalcSize(active);
        float scale = HudScale(3.0f);
        Vector2 pivot = new Vector2(Screen.width - margin, margin);

        Matrix4x4 prev = GUI.matrix;
        Color prevContent = GUI.contentColor;
        GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), pivot);
        if (GUI.Button(new Rect(pivot.x - size.x, pivot.y, size.x, size.y), content, _buttonStyle))
        {
            mobileQualityLevel = (MobileQualityLevel)(((int)mobileQualityLevel + 1) % 3);
            _autoQualityDone = true;
        }

        Rect box = new Rect(pivot.x - activeSize.x, pivot.y + size.y + gap, activeSize.x, activeSize.y);
        Fill(box, new Color(0.05f, 0.06f, 0.08f, 0.72f));
        GUI.contentColor = Color.white;
        GUI.Label(box, active, _hudStyle);

        GUI.matrix = prev;
        GUI.contentColor = prevContent;
    }

    private void EnsureHudStyle()
    {
        if (_hudStyle != null) return;
        _hudStyle = new GUIStyle
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 12,
            richText = true,
            padding = new RectOffset(10, 10, 8, 8),
        };
        _hudStyle.normal.textColor = Color.white;
    }

    private static readonly Color Lod0Color = new Color(0.1f, 0.85f, 0.1f);
    private static readonly Color Lod1Color = new Color(0.9f, 0.1f, 0.1f);
    private static Texture2D _lodRamp, _lodSolid;
    private GUIStyle _legendStyle;

    private const float HudReferenceShortEdge = 1080.0f;

    private static float HudScale(float baseScale)
    {
        float shortEdge = Mathf.Min(Screen.width, Screen.height);
        return baseScale * Mathf.Clamp(shortEdge / HudReferenceShortEdge, 0.5f, 4.0f);
    }


    private static void InitHudTextures()
    {
        if (_lodSolid == null)
        {
            _lodSolid = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            _lodSolid.SetPixel(0, 0, Color.white);
            _lodSolid.Apply();
        }
        if (_lodRamp == null)
        {
            const int w = 64;
            _lodRamp = new Texture2D(w, 1) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            for (int x = 0; x < w; x++) _lodRamp.SetPixel(x, 0, Color.Lerp(Lod0Color, Lod1Color, (x + 0.5f) / w));
            _lodRamp.Apply();
        }
    }

    private void DrawLodLegend()
    {
        InitHudTextures();
        if (_legendStyle == null)
        {
            _legendStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true };
            _legendStyle.normal.textColor = Color.black;
            _legendStyle.hover.textColor = Color.black;
            _legendStyle.active.textColor = Color.black;
            _legendStyle.focused.textColor = Color.black;
        }

        float scale = HudScale(2.0f);
        float screenW = Screen.width / scale, screenH = Screen.height / scale;
        const float margin = 14f, barH = 14f, rowH = 34f;
        int rows = LayerCount;
        float x0 = margin + 96f;                       // leave a gutter for the layer name
        float barW = Mathf.Max(40f, screenW - x0 - margin - 46f);
        float y = screenH - margin - rows * rowH;

        Matrix4x4 prev = GUI.matrix;
        Color prevContent = GUI.contentColor;
        GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), Vector2.zero);
        GUI.contentColor = Color.black;

        for (int i = 0; i < rows; i++, y += rowH)
        {
            var layer = Layer(i);
            if (layer == null) continue;
            float radius = Mathf.Max(0.01f, ResolvedCullRadius);
            float PxAt(float d) => x0 + Mathf.Clamp01(d / radius) * barW;

            float blend = Mathf.Max(0.0f, ResolvedLodBlend);
            float half = blend * 0.5f;
            float lodDistance = ResolvedLodDistance(layer);

            float nearEnd = Mathf.Clamp(lodDistance - half, 0.0f, radius);
            float farStart = Mathf.Clamp(lodDistance + half, 0.0f, radius);

            // GUI.color multiplies the bar textures; contentColor above keeps the text black either way.
            GUI.color = layer.visible ? Color.white : new Color(1, 1, 1, 0.35f);
            Fill(new Rect(PxAt(0), y, PxAt(nearEnd) - PxAt(0), barH), Lod0Color);
            if (farStart > nearEnd)
                GUI.DrawTexture(new Rect(PxAt(nearEnd), y, PxAt(farStart) - PxAt(nearEnd), barH), _lodRamp);
            Fill(new Rect(PxAt(farStart), y, PxAt(radius) - PxAt(farStart), barH), Lod1Color);
            GUI.color = Color.white;

            string name = string.IsNullOrEmpty(layer.name) ? (i == 0 ? "Grass" : $"Detail {i - 1}") : layer.name;
            string label = $"<b>{name}</b>" + (layer.visible ? "" : " <i>(hidden)</i>");
            GUI.Label(new Rect(margin, y - 3f, x0 - margin - 4f, 20f), label, _legendStyle);
            GUI.Label(new Rect(PxAt(radius) + 4f, y - 3f, 60f, 20f), $"<b>{radius:0} m</b>", _legendStyle);
            GUI.Label(new Rect(x0, y + barH, barW, 18f),
                      $"<b>{lodDistance:0.#} m</b> switch" + (blend > 0.01f ? $"  ±{half:0.#} fade" : "  (hard)"),
                      _legendStyle);
        }

        GUI.matrix = prev;
        GUI.contentColor = prevContent;
    }

    private static void Fill(Rect r, Color c)
    {
        if (r.width <= 0.0f) return;
        Color prev = GUI.color;
        GUI.color = new Color(c.r, c.g, c.b, c.a * prev.a);
        GUI.DrawTexture(r, _lodSolid);
        GUI.color = prev;
    }

    private void DrawStatsHud(ref float top)
    {
        int i0 = GrasslandStats.Instances[0], i1 = GrasslandStats.Instances[1];
        long v0 = GrasslandStats.Vertices[0], v1 = GrasslandStats.Vertices[1];
        int ic = GrasslandStats.CulledInstances;
        long vc = GrasslandStats.CulledVertices;
        string txt =
            "<b>Grass renderer statistics (disable stats when profiling)</b>\n" +
            $"LOD0 (near) : {i0,8:N0} instances   {v0,13:N0} vertices\n" +
            $"LOD1 (far)    : {i1,8:N0} instances   {v1,13:N0} vertices\n" +
            $"Culled           : {ic,8:N0} instances   {vc,13:N0} vertices\n" +
            $"Sub-draws     : {GrasslandStats.SubDraws,8:N0} drawn   " +
            $"{GrasslandStats.CulledDraws,8:N0} culled";

        DrawHudBox(txt, ref top);
    }

    private void DrawFrameTimingHud(ref float top)
    {
        string summary = _frameTiming != null && _frameTiming.isActiveAndEnabled ? _frameTiming.Summary : "";
        DrawHudBox("<b>Avg frame time (last 100 frames)</b>\n" + summary, ref top);
    }

    private void DrawHudBox(string txt, ref float top)
    {
        InitHudTextures();
        EnsureHudStyle();

        // CalcSize is independent of the GUI matrix, so the unscaled box can be measured first and the
        // scale capped to whatever actually fits across. Without this the box is simply clipped at the
        // right edge on a narrow screen — a portrait phone has a 1080 short edge but only 1080px of
        // width, where this text wants roughly 1150 at the resolution-derived scale.
        const float margin = 10.0f, gap = 6.0f;
        Vector2 size = _hudStyle.CalcSize(new GUIContent(txt));
        float fitScale = (Screen.width - margin * 2.0f) / Mathf.Max(size.x, 1.0f);
        float scale = Mathf.Min(HudScale(3.0f), fitScale);

        Matrix4x4 prev = GUI.matrix;
        Color prevContent = GUI.contentColor;
        GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), new Vector2(margin, top));

        Rect box = new Rect(margin, top, size.x, size.y);
        Fill(box, new Color(0.05f, 0.06f, 0.08f, 0.72f));
        GUI.contentColor = Color.white;
        GUI.Label(box, txt, _hudStyle);

        GUI.matrix = prev;
        GUI.contentColor = prevContent;
        top += size.y * scale + gap;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        cullRadius = Mathf.Max(1.0f, cullRadius);
        cullPadding = Mathf.Max(0.0f, cullPadding);
        int dim = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(Mathf.Max(1, cellCount))), 1, 16);
        cellCount = dim * dim;
        lodBlend = Mathf.Max(0.0f, lodBlend);
        for (int i = 0; i < LayerCount; i++)
        {
            var l = Layer(i);
            if (l == null) continue;
            l.spacing = Mathf.Max(0.05f, l.spacing);
            l.lodDistance = Mathf.Clamp(l.lodDistance, 1.0f, cullRadius);
        }
        if (isActiveAndEnabled) ConfigVersion++;
    }
#endif

    // "Bake Placement" button entry point 
    public void BakePlacement()
    {
        if (terrainCollider == null) return;

        Bounds b = terrainCollider.bounds;
        float topY = b.max.y + 10.0f;             // start rays above the terrain
        float castDist = b.size.y + 20.0f;

        for (int i = 0; i < LayerCount; i++)
        {
            var layer = Layer(i);
            if (layer == null) continue;
            layer.positions = ScatterLayer(layer, b, topY, castDist);
        }
        ConfigVersion++;
    }

    private void EnsurePlacement()
    {
        if (terrainCollider == null) return;
        for (int i = 0; i < LayerCount; i++)
        {
            var l = Layer(i);
            if (l != null && l.positions == null) { BakePlacement(); return; }
        }
    }

    private Vector4[] ScatterLayer(FoliageLayer layer, Bounds b, float topY, float castDist)
    {
        // No mask = no placement (keeps an unconfigured layer empty rather than blanketing the field).
        if (layer.mask == null) return System.Array.Empty<Vector4>();

        float spacing = Mathf.Max(0.05f, layer.spacing);
        int nx = Mathf.Max(1, Mathf.CeilToInt(b.size.x / spacing));
        int nz = Mathf.Max(1, Mathf.CeilToInt(b.size.z / spacing));

        Texture2D readable = MakeReadableCopy(layer.mask, MaskReadResolution);

        float jit = Mathf.Clamp01(positionJitter);
        float rot = Mathf.Clamp01(rotationJitter);
        var list = new List<Vector4>();

        for (int j = 0; j < nz; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                uint cell = (uint)(j * nx + i);

                float wx = b.min.x + (i + 0.5f) * spacing + (Hash01(cell * 3u + 1u) * 2f - 1f) * spacing * 0.5f * jit;
                float wz = b.min.z + (j + 0.5f) * spacing + (Hash01(cell * 3u + 2u) * 2f - 1f) * spacing * 0.5f * jit;

                float u = Mathf.InverseLerp(b.min.x, b.max.x, wx) + layer.maskOffset.x;
                float v = Mathf.InverseLerp(b.min.z, b.max.z, wz) + layer.maskOffset.y;
                float m = readable.GetPixelBilinear(u, v).r;
                if (Hash01(cell * 3u + 3u) >= m * layer.density) continue;

                // Conform to the terrain surface; a candidate with no terrain under it is dropped.
                var ray = new Ray(new Vector3(wx, topY, wz), Vector3.down);
                if (!terrainCollider.Raycast(ray, out RaycastHit hit, castDist)) continue;

                // .w carries the yaw in radians; the shader rotates the clump by it.
                float yaw = Hash01(cell * 7u + 5u) * 6.2831853f * rot;
                list.Add(new Vector4(hit.point.x, hit.point.y, hit.point.z, yaw));
            }
        }

        SafeDestroy(ref readable);
        return list.ToArray();
    }

    // Deterministic hash in [0,1) for stable per-cell jitter / rejection.
    private static float Hash01(uint n)
    {
        n ^= 2747636419u; n *= 2654435769u;
        n ^= n >> 16; n *= 2654435769u;
        n ^= n >> 16; n *= 2654435769u;
        return (n & 0xffffffu) / 16777216.0f;
    }

    private static void SafeDestroy(ref Texture2D tex)
    {
        if (tex == null) return;
        if (Application.isPlaying) Destroy(tex);
        else DestroyImmediate(tex);
        tex = null;
    }

    // GPU-blit a texture into a readable RGBAFloat copy so it can be sampled on the CPU
    private static Texture2D MakeReadableCopy(Texture2D src, int size)
    {
        size = Mathf.Max(2, size);
        var prev = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;

        var tex = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tex.Apply(false, false);

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return tex;
    }
}

// Drawn-this-frame count published by the render feature
public static class GrasslandStats
{
    public const int Lods = 2;
    public static readonly int[] Instances = new int[Lods];
    public static readonly long[] Vertices = new long[Lods];

    // Culled baked instances that produced no draw this frame, and the vertices they would have rendered
    public static int CulledInstances;
    public static long CulledVertices;

    // Sub-draw culling: cells that kept instances, and cells that had none.
    public static int SubDraws;
    public static int CulledDraws;

    public static void Reset()
    {
        for (int i = 0; i < Lods; i++) { Instances[i] = 0; Vertices[i] = 0; }
        CulledInstances = 0;
        CulledVertices = 0;
        SubDraws = 0;
        CulledDraws = 0;
    }
}
