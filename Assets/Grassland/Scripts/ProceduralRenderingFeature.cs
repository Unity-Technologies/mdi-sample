using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;


public class ProceduralRenderingFeature : ScriptableRendererFeature
{
    [Tooltip("Foliage material (indirect ParticleShader). All other config is on the GrasslandVolume.")]
    [SerializeField] private Material material;
    private ProceduralPass proceduralRenderPass;

    public override void Create()
    {
        if (material == null) return;
        proceduralRenderPass = new ProceduralPass(material);
        proceduralRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderData)
    {
        if (proceduralRenderPass == null) return;
        if ((renderData.cameraData.cameraType == CameraType.Game) || (renderData.cameraData.cameraType == CameraType.SceneView))
            renderer.EnqueuePass(proceduralRenderPass);
    }

    protected override void Dispose(bool disposing)
    {
        if (proceduralRenderPass != null) proceduralRenderPass.DisposeResources();
    }
}

class ProceduralPass : ScriptableRenderPass
{
    // Mirrors DrawParams in ParticleShader. Field order and types must match exactly: this is read as
    // a StructuredBuffer element, so the two layouts are only kept in sync by hand.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DrawParams
    {
        public Vector4 clumpScale;
        public Vector4 coverageChannel;
        public float modelHeight;
        public float windScale;
        public float heightVariance;
        public float lodDistance;
        public float lodLevel;
        public float alphaCutoff;
        public float textured;
        public float textureSlice;
        public float coverageOnly;

        // Explicit padding to 80 bytes.
        // A struct containing float4 gets 16-byte alignment, and SPIR-V rounds the struct size up to a multiple of 16.
        public float pad0, pad1, pad2;

        public const int Stride = (4 + 4 + 12) * sizeof(float);   // 80 bytes = 5 x float4
    }

    private const int LODS = 2;              // near (dense) + far (sparse) per layer

    private Material material;

    private GrasslandVolume volume;
    private int builtVersion = -1;
    private bool initialized;
    private float worldScale = 1.0f;

    // GPU state for one foliage layer: its baked positions + per-LOD geometry/cull/draw buffers.
    private class LayerGPU
    {
        public ComputeBuffer positions;     // world x,y,z + yaw (from the volume's placement bake)
        public int count;
        public int maxCellCount;            // most instances landing in any one grid cell
        public int cullGroups;
        public float lodDistance;
        public float windScale;
        public Vector3[] clumpScale = new Vector3[LODS];   // per LOD: the far one carries impostorScale
        public float heightVariance;
        public float alphaCutoff;

        public float coverageOnly;                          // 1 = textures are masks, color from gradient
        public Vector4[] coverageChannel = new Vector4[LODS];

        public bool[] active = new bool[LODS];
        public Texture2D[] texture = new Texture2D[LODS];   // per-LOD texture (null = procedural)
        public float[] height = new float[LODS];
        public int[] vcount = new int[LODS];                // per-LOD mesh vertex count (for the debug HUD)
        public int[] indexStart = new int[LODS];   // range into the shared index buffer
        public int[] indexCount = new int[LODS];
        public ComputeBuffer tally;         // debug HUD: would-be per-LOD counts, [0]=near [1]=far
        public MaterialPropertyBlock[] mpb = new MaterialPropertyBlock[LODS];
    }
    private LayerGPU[] layers = new LayerGPU[0];

    private ComputeBuffer sharedVertex;
    private ComputeBuffer sharedUV;
    private GraphicsBuffer sharedIndex;

    private ComputeBuffer culledPositions;
    private ComputeBuffer culledWind;
    private ComputeBuffer culledCounts;
    private ComputeBuffer drawArgs;        // 5 uints per draw, written on the GPU by WriteDrawArgs
    private ComputeBuffer indexCountPerInstance;   // static per draw: indices in its mesh
    private ComputeBuffer startIndex;              // static per draw: its range in the shared buffer

    private const int ArgsPerDraw = 5;
    private const int ArgInstanceCount = 1;
    private const int ArgsStride = ArgsPerDraw * sizeof(uint);   // 20 bytes
    private const int PassPortable = 0;
    private const int PassMDI = 1;

    // Multi-draw indirect support, reported by the Unity Graphics Kernel.
    // Two tiers:
    //   supportsMultiDrawIndirect            — one draw call submits all sub-draws
    //   supportsMultiDrawIndirectCountBuffer — the GPU also controls how many subdraws are submitted
    // When neither is supported, submit one indirect draw per (layer, LOD, cell) for portability
    private bool UseMultiDraw => SystemInfo.supportsMultiDrawIndirect &&
                                 SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan;
    private bool SupportsCountBuffer => UseMultiDraw && SystemInfo.supportsMultiDrawIndirectCountBuffer;
    private bool UseCountBuffer => SupportsCountBuffer && (volume == null || volume.mdiCountBuffer);
    private int instanceCapacity;
    // One argument slot per (layer, LOD, cell); the max drawCount for the multi-draw.
    private int maxDrawCount;

    // Sub-draw culling.
    // - Ordinary indirect rendering can only cull instances, not draws
    // - Multi-draw can cull entire sub-draws
    private int cellDim = 1;
    private int cells = 1;
    private float gridOriginX, gridOriginZ, invCellX, invCellZ;
    private Vector4 cellExtents;   // xy = cell size (x, z), zw = the field's min/max Y
    private ComputeBuffer cellVisible;
    private ComputeBuffer preCullCounts;   // debug HUD: per sub-draw counts ignoring visibility
    private MaterialPropertyBlock _mdiMpb;   // single block shared by the one multi-draw call

    private ComputeBuffer mdiArgs;
    private ComputeBuffer mdiDrawCount;
    private ComputeBuffer drawIdTable;
    private static readonly uint[] _drawCountZero = { 0u };

    // One entry per (layer, LOD), indexed by LayerLodId — not by sub-draw id, which fans out per cell
    private ComputeBuffer drawParamsBuffer;
    private DrawParams[] _drawParams = System.Array.Empty<DrawParams>();

    // Shader property IDs
    private int vertexBufferUID, uvBufferUID, culledPositionsBufferUID;
    private int positionsBufferUID;
    private int numInstancesUID, cullOriginUID, cullRadiusUID, cullEnabledUID, lodDistanceUID, lodBlendUID;
    private int frustumPlanesUID, frustumPaddingUID;
    private int lodCenterUID, lodBandWidthUID, debugLodUID;
    private int modelHeightUID, worldScaleUID, timeUID, windUID, windScaleUID, clumpScaleUID, heightVarianceUID, lodLevelUID;
    private int layerTexUID, alphaCutoffUID, texturedUID, coverageOnlyUID, coverageChannelUID;
    private int drawParamsUID, drawIdUID;
    private int windWaveLengthUID, windWaveSpeedUID, windGustinessUID, windSwayUID, windSwaySpeedUID, windShadeUID, windPatchinessUID;
    private int lodTallyUID, debugHUDUID, preCullCountsUID;
    private int culledPositionsUID, culledWindUID, culledWindBufferUID, culledCountsUID, drawIdNearUID, drawIdFarUID, instanceCapacityUID;
    private int drawArgsUID, indexCountPerInstanceUID, startIndexUID, drawCountUID;
    private int mdiArgsUID, mdiDrawCountUID, drawIdTableUID;
    private int gridParamsUID, cellDimUID, cellCountUID, cellVisibleUID, cellExtentsUID;

    private ComputeShader computeShader;
    private int kSubdrawCull, kInstanceCull, kWriteArgs, kCompactArgs;
    private Matrix4x4 identity = Matrix4x4.identity;

    private uint[] _countsZero = System.Array.Empty<uint>();
    private uint[] _argsReadback = System.Array.Empty<uint>();
    private uint[] _preCullReadback = System.Array.Empty<uint>();
    private static readonly uint[] _tallyZero = { 0u, 0u };   // scratch for indirect-args readback (debug HUD)
    private uint[] _tallyReadback = System.Array.Empty<uint>();
    private AsyncGPUReadbackRequest _argsRequest, _preCullRequest;
    private AsyncGPUReadbackRequest[] _tallyRequests = System.Array.Empty<AsyncGPUReadbackRequest>();
    private bool[] _tallyRequested = System.Array.Empty<bool>();
    private bool _statsPending;
    private readonly System.Collections.Generic.List<Vector3> _verts = new();
    private readonly System.Collections.Generic.List<Vector2> _uvs = new();
    private readonly System.Collections.Generic.List<int> _indices = new();
    private int[] _cellPopulation = System.Array.Empty<int>();   // scratch for the capacity count
    private readonly Plane[] _frustum = new Plane[6];  // scratch for frustum culling
    private readonly Vector4[] _frustumV4 = new Vector4[6];

    public ProceduralPass(Material material)
    {
        this.material = material;
        computeShader = (ComputeShader)Resources.Load("ComputePrepass");
        kSubdrawCull = computeShader.FindKernel("SubdrawCulling");
        kInstanceCull = computeShader.FindKernel("InstanceCulling");
        kWriteArgs = computeShader.FindKernel("WriteDrawArgs");
        kCompactArgs = computeShader.FindKernel("CompactDrawArgs");
        CacheShaderIDs();
    }

    private void CacheShaderIDs()
    {
        vertexBufferUID = Shader.PropertyToID("vertexBuffer");
        uvBufferUID = Shader.PropertyToID("uvBuffer");
        culledPositionsBufferUID = Shader.PropertyToID("culledPositionsBuffer");
        positionsBufferUID = Shader.PropertyToID("positionsBuffer");
        numInstancesUID = Shader.PropertyToID("numInstances");
        cullOriginUID = Shader.PropertyToID("cullOrigin");
        cullRadiusUID = Shader.PropertyToID("cullRadius");
        cullEnabledUID = Shader.PropertyToID("cullEnabled");
        lodDistanceUID = Shader.PropertyToID("lodDistance");
        lodBlendUID = Shader.PropertyToID("lodBlend");
        frustumPlanesUID = Shader.PropertyToID("frustumPlanes");
        frustumPaddingUID = Shader.PropertyToID("frustumPadding");
        lodCenterUID = Shader.PropertyToID("lodCenter");
        lodBandWidthUID = Shader.PropertyToID("lodBandWidth");
        debugLodUID = Shader.PropertyToID("_DebugLod");
        modelHeightUID = Shader.PropertyToID("modelHeight");
        worldScaleUID = Shader.PropertyToID("worldScale");
        timeUID = Shader.PropertyToID("time");
        windUID = Shader.PropertyToID("wind");
        windScaleUID = Shader.PropertyToID("windScale");
        windWaveLengthUID = Shader.PropertyToID("windWaveLength");
        windWaveSpeedUID = Shader.PropertyToID("windWaveSpeed");
        windGustinessUID = Shader.PropertyToID("windGustiness");
        windSwayUID = Shader.PropertyToID("windSway");
        windSwaySpeedUID = Shader.PropertyToID("windSwaySpeed");
        windShadeUID = Shader.PropertyToID("windShade");
        windPatchinessUID = Shader.PropertyToID("windPatchiness");
        clumpScaleUID = Shader.PropertyToID("clumpScale");
        heightVarianceUID = Shader.PropertyToID("heightVariance");
        lodLevelUID = Shader.PropertyToID("lodLevel");
        layerTexUID = Shader.PropertyToID("_LayerTexArray");
        alphaCutoffUID = Shader.PropertyToID("_AlphaCutoff");
        texturedUID = Shader.PropertyToID("textured");
        drawParamsUID = Shader.PropertyToID("drawParams");
        drawIdUID = Shader.PropertyToID("drawId");
        coverageOnlyUID = Shader.PropertyToID("coverageOnly");
        coverageChannelUID = Shader.PropertyToID("coverageChannel");

        lodTallyUID = Shader.PropertyToID("lodTally");
        preCullCountsUID = Shader.PropertyToID("preCullCounts");
        debugHUDUID = Shader.PropertyToID("debugHUD");
        culledPositionsUID = Shader.PropertyToID("culledPositions");
        culledWindUID = Shader.PropertyToID("culledWind");
        culledWindBufferUID = Shader.PropertyToID("culledWindBuffer");
        culledCountsUID = Shader.PropertyToID("culledCounts");
        drawIdNearUID = Shader.PropertyToID("drawIdNear");
        drawIdFarUID = Shader.PropertyToID("drawIdFar");
        instanceCapacityUID = Shader.PropertyToID("instanceCapacity");
        drawArgsUID = Shader.PropertyToID("drawArgs");
        indexCountPerInstanceUID = Shader.PropertyToID("indexCountPerInstance");
        startIndexUID = Shader.PropertyToID("startIndex");
        drawCountUID = Shader.PropertyToID("drawCount");
        mdiArgsUID = Shader.PropertyToID("mdiArgs");
        mdiDrawCountUID = Shader.PropertyToID("mdiDrawCount");
        drawIdTableUID = Shader.PropertyToID("drawIdTable");
        gridParamsUID = Shader.PropertyToID("gridParams");
        cellDimUID = Shader.PropertyToID("cellDim");
        cellCountUID = Shader.PropertyToID("cellCount");
        cellVisibleUID = Shader.PropertyToID("cellVisible");
        cellExtentsUID = Shader.PropertyToID("cellExtents");
    }

    public void Initialize(GrasslandVolume v)
    {
        volume = v;
        worldScale = v.WorldExtentX;

        // Grid derived from the field's own bounds so the cells cover exactly the planted area.
        cellDim = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(Mathf.Max(1, v.ResolvedCellCount))), 1, 16);
        cells = cellDim * cellDim;
        Bounds field = v.FieldBounds;
        gridOriginX = field.min.x;
        gridOriginZ = field.min.z;
        invCellX = cellDim / Mathf.Max(1e-4f, field.size.x);
        invCellZ = cellDim / Mathf.Max(1e-4f, field.size.z);
        cellExtents = new Vector4(field.size.x / cellDim, field.size.z / cellDim,
                                  field.min.y, field.max.y);

        int n = v.LayerCount;
        layers = new LayerGPU[n];
        _verts.Clear(); _uvs.Clear(); _indices.Clear();
        for (int li = 0; li < n; li++)
        {
            var layer = v.Layer(li);
            var layerData = new LayerGPU();
            layers[li] = layerData;
            if (layer == null) { layerData.count = 0; continue; }

            Vector4[] arr = v.LayerPositions(li) ?? System.Array.Empty<Vector4>();
            layerData.count = arr.Length;
            layerData.lodDistance = v.ResolvedLodDistance(layer);
            layerData.windScale = layer.windScale;
            layerData.clumpScale[0] = layer.clumpScale;
            layerData.clumpScale[1] = Vector3.Scale(layer.clumpScale, layer.impostorScale);
            layerData.heightVariance = layer.heightVariance;
            layerData.alphaCutoff = layer.alphaCutoff;
            layerData.coverageOnly = layer.textureUse == TextureUse.CoverageOnly ? 1.0f : 0.0f;

            layerData.tally = new ComputeBuffer(2, sizeof(uint), ComputeBufferType.Structured);
            layerData.positions = new ComputeBuffer(Mathf.Max(1, layerData.count), sizeof(float) * 4);
            if (layerData.count > 0) layerData.positions.SetData(arr);

            if (_cellPopulation.Length < cells) _cellPopulation = new int[cells];
            System.Array.Clear(_cellPopulation, 0, cells);
            for (int i = 0; i < arr.Length; i++) _cellPopulation[CellOf(arr[i])]++;
            for (int c = 0; c < cells; c++)
                layerData.maxCellCount = Mathf.Max(layerData.maxCellCount, _cellPopulation[c]);
            layerData.cullGroups = Mathf.Max(1, Mathf.CeilToInt(layerData.count / 64.0f));

            for (int L = 0; L < LODS; L++)
            {
                layerData.mpb[L] = new MaterialPropertyBlock();

                var mesh = L == 0 ? layer.nearMesh : layer.farMesh;
                layerData.texture[L] = L == 0 ? layer.nearTexture : layer.impostorTexture;
                layerData.coverageChannel[L] = CoverageChannel(layer.coverageFrom);
                layerData.active[L] = mesh != null && mesh.vertexCount > 0;
                if (!layerData.active[L]) continue;

                Vector3[] verts = mesh.vertices;
                int[] indices = mesh.triangles;
                if (indices.Length == 0) { layerData.active[L] = false; continue; }

                layerData.height[L] = Mathf.Max(0.01f, mesh.bounds.size.y);
                int vcount = verts.Length;
                layerData.vcount[L] = vcount;
                Vector2[] uvs = (mesh.uv != null && mesh.uv.Length == vcount) ? mesh.uv : new Vector2[vcount];

                // Append to the shared geometry, offsetting indices by this mesh's vertex base.
                int vertexBase = _verts.Count;
                layerData.indexStart[L] = _indices.Count;
                layerData.indexCount[L] = indices.Length;
                _verts.AddRange(verts);
                _uvs.AddRange(uvs);
                for (int i = 0; i < indices.Length; i++) _indices.Add(indices[i] + vertexBase);
            }
        }

        BuildSharedGeometry();
        BuildDrawParams();
        BuildInstanceBuffers();
    }

    // LayerLodId: per (layer, LOD) data (draw params, texture slice, index range).
    // DrawId: per sub-draw, one per cell of a LayerLodId.
    private static int LayerLodId(int layerIndex, int lod) => layerIndex * LODS + lod;
    private int DrawId(int layerIndex, int lod, int cell) => LayerLodId(layerIndex, lod) * cells + cell;

    // Which cell a world position falls in. Mirrored by CellOf() in ComputePrepass.compute
    private int CellOf(Vector4 p)
    {
        int cx = Mathf.Clamp(Mathf.FloorToInt((p.x - gridOriginX) * invCellX), 0, cellDim - 1);
        int cz = Mathf.Clamp(Mathf.FloorToInt((p.z - gridOriginZ) * invCellZ), 0, cellDim - 1);
        return cz * cellDim + cx;
    }

    private void BuildDrawParams()
    {
        if (drawParamsBuffer != null) { drawParamsBuffer.Release(); drawParamsBuffer = null; }

        int count = Mathf.Max(1, layers.Length * LODS);
        if (_drawParams.Length != count) _drawParams = new DrawParams[count];

        for (int li = 0; li < layers.Length; li++)
        {
            var layer = layers[li];
            for (int L = 0; L < LODS; L++)
            {
                var d = new DrawParams();
                if (layer != null)
                {
                    d.clumpScale = layer.clumpScale[L];
                    d.coverageChannel = layer.coverageChannel[L];
                    d.modelHeight = Mathf.Max(0.01f, layer.height[L]);
                    d.windScale = layer.windScale;
                    d.heightVariance = layer.heightVariance;
                    d.lodDistance = layer.lodDistance;
                    d.lodLevel = L;
                    d.alphaCutoff = layer.alphaCutoff;
                    // Without a baked array, fall back to procedural shading.
                    d.textured = (volume.layerTextureArray != null && layer.texture[L] != null) ? 1.0f : 0.0f;
                    d.textureSlice = LayerLodId(li, L);
                    d.coverageOnly = layer.coverageOnly;
                }
                _drawParams[LayerLodId(li, L)] = d;
            }
        }

        drawParamsBuffer = new ComputeBuffer(count, DrawParams.Stride, ComputeBufferType.Structured);
        drawParamsBuffer.SetData(_drawParams);
    }

    // One vertex/uv/index buffer for every layer and LOD. MDI takes one index buffer for all sub-draws.
    private void BuildSharedGeometry()
    {
        ReleaseSharedGeometry();
        ReleaseInstanceBuffers();
        if (_indices.Count == 0) return;

        sharedVertex = new ComputeBuffer(_verts.Count, sizeof(float) * 3);
        sharedUV = new ComputeBuffer(_verts.Count, sizeof(float) * 2);
        sharedIndex = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.Index,
                                         _indices.Count, sizeof(int));
        sharedVertex.SetData(_verts);
        sharedUV.SetData(_uvs);
        sharedIndex.SetData(_indices);
    }

    // Regions, counters and the args buffer
    private void BuildInstanceBuffers()
    {
        ReleaseInstanceBuffers();

        maxDrawCount = Mathf.Max(1, layers.Length * LODS * cells);
        instanceCapacity = 1;
        foreach (var layer in layers)
            if (layer != null) instanceCapacity = Mathf.Max(instanceCapacity, layer.maxCellCount);

        culledPositions = new ComputeBuffer(maxDrawCount * instanceCapacity, sizeof(float) * 4);
        culledWind = new ComputeBuffer(maxDrawCount * instanceCapacity, sizeof(uint) * 2);
        culledCounts = new ComputeBuffer(maxDrawCount, sizeof(uint));
        drawArgs = new ComputeBuffer(maxDrawCount * ArgsPerDraw, sizeof(uint),
                                     ComputeBufferType.IndirectArguments | ComputeBufferType.Structured);
        int layerLods = Mathf.Max(1, layers.Length * LODS);
        indexCountPerInstance = new ComputeBuffer(layerLods, sizeof(uint));
        startIndex = new ComputeBuffer(layerLods, sizeof(uint));
        drawIdTable = new ComputeBuffer(maxDrawCount, sizeof(uint), ComputeBufferType.Structured);
        cellVisible = new ComputeBuffer(cells, sizeof(uint), ComputeBufferType.Structured);
        preCullCounts = new ComputeBuffer(maxDrawCount, sizeof(uint), ComputeBufferType.Structured);

        if (SupportsCountBuffer)
        {
            mdiArgs = new ComputeBuffer(maxDrawCount * ArgsPerDraw, sizeof(uint),
                                        ComputeBufferType.IndirectArguments | ComputeBufferType.Structured);

            mdiDrawCount = new ComputeBuffer(1, sizeof(uint),
                                             ComputeBufferType.IndirectArguments | ComputeBufferType.Structured);
        }

        var indexCounts = new uint[layerLods];
        var startIndices = new uint[layerLods];
        for (int li = 0; li < layers.Length; li++)
        {
            var layer = layers[li];
            for (int L = 0; L < LODS; L++)
            {
                int id = LayerLodId(li, L);
                indexCounts[id] = (layer != null && layer.active[L]) ? (uint)layer.indexCount[L] : 0u;
                startIndices[id] = (layer != null) ? (uint)layer.indexStart[L] : 0u;
            }
        }
        indexCountPerInstance.SetData(indexCounts);
        startIndex.SetData(startIndices);

        _countsZero = new uint[maxDrawCount];
        _argsReadback = new uint[maxDrawCount * ArgsPerDraw];
        _preCullReadback = new uint[maxDrawCount];
    }

    private void FlushStatsReadback()
    {
        if (!_statsPending) return;
        AsyncGPUReadback.WaitAllRequests();
        _statsPending = false;
    }

    private void ReleaseInstanceBuffers()
    {
        FlushStatsReadback();
        void Rel(ref ComputeBuffer b) { if (b != null) { b.Release(); b = null; } }
        Rel(ref culledPositions); Rel(ref culledWind); Rel(ref culledCounts); Rel(ref drawArgs);
        Rel(ref indexCountPerInstance); Rel(ref startIndex);
        Rel(ref mdiArgs); Rel(ref mdiDrawCount); Rel(ref drawIdTable); Rel(ref cellVisible);
        Rel(ref preCullCounts);
    }

    private void ReleaseSharedGeometry()
    {
        if (sharedVertex != null) { sharedVertex.Release(); sharedVertex = null; }
        if (sharedUV != null) { sharedUV.Release(); sharedUV = null; }
        if (sharedIndex != null) { sharedIndex.Release(); sharedIndex = null; }
    }

    private class PassData
    {
        public TextureHandle color;
        public TextureHandle depth;
        public Camera cam;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var v = GrasslandVolume.Active;
        if (v == null || material == null) return;

        if (!initialized || v != volume || v.ConfigVersion != builtVersion)
        {
            if (initialized) DisposeResources();
            Initialize(v);
            builtVersion = v.ConfigVersion;
            initialized = true;
        }

        // Debug HUD: read last frame's drawn instance counts back from the indirect args buffers.
        if (v.debugHud) UpdateStats();

        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

        using (var builder = renderGraph.AddUnsafePass<PassData>("ProceduralDraw", out var passData))
        {
            passData.color = resourceData.activeColorTexture;
            passData.depth = resourceData.activeDepthTexture;
            passData.cam = cameraData.camera;

            builder.UseTexture(passData.color, AccessFlags.ReadWrite);
            builder.UseTexture(passData.depth, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) => ExecutePass(data, ctx));
        }
    }

    private void ExecutePass(PassData data, UnsafeGraphContext ctx)
    {
        if (sharedIndex == null) return;
        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
        cmd.SetRenderTarget(data.color, data.depth);

        Vector3 camPos = data.cam != null ? data.cam.transform.position : Vector3.zero;

        // GPU cull and LOD uniforms
        cmd.SetComputeVectorParam(computeShader, cullOriginUID, new Vector4(camPos.x, camPos.y, camPos.z, 0.0f));
        cmd.SetComputeFloatParam(computeShader, cullRadiusUID, volume.ResolvedCullRadius);
        cmd.SetComputeIntParam(computeShader, cullEnabledUID, volume.gpuCulling ? 1 : 0);
        cmd.SetComputeFloatParam(computeShader, lodBlendUID, volume.ResolvedLodBlend);

        // Camera frustum planes for GPU culling
        if (data.cam != null)
        {
            GeometryUtility.CalculateFrustumPlanes(data.cam, _frustum);
            for (int p = 0; p < 6; p++)
            {
                Plane frstm = _frustum[p];
                _frustumV4[p] = new Vector4(frstm.normal.x, frstm.normal.y, frstm.normal.z, frstm.distance);
            }
            cmd.SetComputeVectorArrayParam(computeShader, frustumPlanesUID, _frustumV4);
            cmd.SetComputeFloatParam(computeShader, frustumPaddingUID, volume.cullPadding);
        }

        cmd.SetComputeFloatParam(computeShader, timeUID, Time.time);
        cmd.SetComputeVectorParam(computeShader, windUID, volume.wind);
        cmd.SetComputeFloatParam(computeShader, windWaveLengthUID, Mathf.Max(0.1f, volume.windWaveLength));
        cmd.SetComputeFloatParam(computeShader, windWaveSpeedUID, volume.windWaveSpeed);
        cmd.SetComputeFloatParam(computeShader, windGustinessUID, Mathf.Clamp01(volume.windGustiness));
        cmd.SetComputeFloatParam(computeShader, windSwayUID, Mathf.Clamp01(volume.windSway));
        cmd.SetComputeFloatParam(computeShader, windSwaySpeedUID, volume.windSwaySpeed);
        cmd.SetComputeFloatParam(computeShader, windPatchinessUID, Mathf.Clamp01(volume.windPatchiness));

        // Chunk grid, shared by the culling and draw-argument kernels.
        cmd.SetComputeVectorParam(computeShader, gridParamsUID,
                                  new Vector4(gridOriginX, gridOriginZ, invCellX, invCellZ));
        cmd.SetComputeIntParam(computeShader, cellDimUID, cellDim);
        cmd.SetComputeIntParam(computeShader, cellCountUID, cells);
        cmd.SetComputeVectorParam(computeShader, cellExtentsUID, cellExtents);

        // Cell culling: renderers in a rejected cell are discarded, renderers in a fully visible cell skip tests
        cmd.SetComputeBufferParam(computeShader, kSubdrawCull, cellVisibleUID, cellVisible);
        cmd.DispatchCompute(computeShader, kSubdrawCull, Mathf.CeilToInt(cells / 64.0f), 1, 1);

        cmd.SetBufferData(culledCounts, _countsZero);
        if (volume.debugHud) cmd.SetBufferData(preCullCounts, _countsZero);

        for (int li = 0; li < layers.Length; li++)
        {
            var layer = layers[li];
            if (layer == null || layer.count == 0 || !IsVisible(li)) continue;

            cmd.SetComputeIntParam(computeShader, numInstancesUID, layer.count);
            cmd.SetComputeFloatParam(computeShader, lodDistanceUID, layer.lodDistance);
            cmd.SetComputeBufferParam(computeShader, kInstanceCull, positionsBufferUID, layer.positions);
            cmd.SetComputeBufferParam(computeShader, kInstanceCull, culledPositionsUID, culledPositions);
            cmd.SetComputeBufferParam(computeShader, kInstanceCull, culledWindUID, culledWind);
            cmd.SetComputeBufferParam(computeShader, kInstanceCull, culledCountsUID, culledCounts);
            cmd.SetComputeBufferParam(computeShader, kInstanceCull, cellVisibleUID, cellVisible);
            // The kernel turns these into per-cell draw ids itself.
            cmd.SetComputeIntParam(computeShader, drawIdNearUID, LayerLodId(li, 0));
            cmd.SetComputeIntParam(computeShader, drawIdFarUID, LayerLodId(li, 1));
            cmd.SetComputeIntParam(computeShader, instanceCapacityUID, instanceCapacity);
    
            bool tally = volume.debugHud;
            if (tally) cmd.SetBufferData(layer.tally, _tallyZero);
            cmd.SetComputeIntParam(computeShader, debugHUDUID, tally ? 1 : 0);
            cmd.SetComputeBufferParam(computeShader, kInstanceCull, lodTallyUID, layer.tally);
            cmd.SetComputeBufferParam(computeShader, kInstanceCull, preCullCountsUID, preCullCounts);
            cmd.DispatchCompute(computeShader, kInstanceCull, layer.cullGroups, 1, 1);

        }

        cmd.SetComputeBufferParam(computeShader, kWriteArgs, culledCountsUID, culledCounts);
        cmd.SetComputeBufferParam(computeShader, kWriteArgs, drawArgsUID, drawArgs);
        cmd.SetComputeBufferParam(computeShader, kWriteArgs, indexCountPerInstanceUID, indexCountPerInstance);
        cmd.SetComputeBufferParam(computeShader, kWriteArgs, startIndexUID, startIndex);
        cmd.SetComputeIntParam(computeShader, instanceCapacityUID, instanceCapacity);
        cmd.SetComputeIntParam(computeShader, drawCountUID, maxDrawCount);
        cmd.SetComputeBufferParam(computeShader, kWriteArgs, drawIdTableUID, drawIdTable);
        cmd.DispatchCompute(computeShader, kWriteArgs, Mathf.CeilToInt(maxDrawCount / 64.0f), 1, 1);

        if (UseCountBuffer)
        {
            cmd.SetBufferData(mdiDrawCount, _drawCountZero);
            cmd.SetComputeBufferParam(computeShader, kCompactArgs, drawArgsUID, drawArgs);
            cmd.SetComputeBufferParam(computeShader, kCompactArgs, mdiArgsUID, mdiArgs);
            cmd.SetComputeBufferParam(computeShader, kCompactArgs, mdiDrawCountUID, mdiDrawCount);
            cmd.SetComputeBufferParam(computeShader, kCompactArgs, drawIdTableUID, drawIdTable);
            cmd.DispatchCompute(computeShader, kCompactArgs, Mathf.CeilToInt(maxDrawCount / 64.0f), 1, 1);
        }

        // Draw uniforms - scalars and the texture array are shared by every draw
        material.SetInt(instanceCapacityUID, instanceCapacity);
        material.SetInt(cellCountUID, cells);
        if (volume.layerTextureArray != null) material.SetTexture(layerTexUID, volume.layerTextureArray);
        material.SetFloat(worldScaleUID, worldScale);
        material.SetFloat(windShadeUID, Mathf.Clamp01(volume.windShade));
        material.SetVector(lodCenterUID, new Vector4(camPos.x, camPos.y, camPos.z, 0.0f));
        material.SetFloat(lodBandWidthUID, volume.ResolvedLodBlend);
        material.SetFloat(debugLodUID, volume.debugLod ? 1.0f : 0.0f);

        if (UseMultiDraw) DrawMulti(cmd);
        else DrawPerLayer(cmd);
    }

    // Every buffer the shader reads, bound through a property block
    private void BindBuffers(MaterialPropertyBlock mpb)
    {
        mpb.SetBuffer(vertexBufferUID, sharedVertex);
        mpb.SetBuffer(uvBufferUID, sharedUV);
        mpb.SetBuffer(culledPositionsBufferUID, culledPositions);
        mpb.SetBuffer(culledWindBufferUID, culledWind);
        mpb.SetBuffer(drawParamsUID, drawParamsBuffer);
        mpb.SetBuffer(drawIdTableUID, drawIdTable);
        mpb.SetInt(instanceCapacityUID, instanceCapacity);
    }

    private void DrawPerLayer(CommandBuffer cmd)
    {
        for (int li = 0; li < layers.Length; li++)
        {
            var layer = layers[li];
            if (layer == null || layer.count == 0 || !IsVisible(li)) continue;

            for (int L = 0; L < LODS; L++)
            {
                if (!layer.active[L]) continue;
                var mpb = layer.mpb[L];
                BindBuffers(mpb);

                for (int c = 0; c < cells; c++)
                {
                    int id = DrawId(li, L, c);
                    mpb.SetInt(drawIdUID, id);
                    cmd.DrawProceduralIndirect(sharedIndex, identity, material, PassPortable, MeshTopology.Triangles,
                                               bufferWithArgs: drawArgs,
                                               argsOffset: id * ArgsStride,
                                               properties: mpb);
                }
            }
        }
    }

    private void DrawMulti(CommandBuffer cmd)
    {
        _mdiMpb ??= new MaterialPropertyBlock();
        BindBuffers(_mdiMpb);

        if (UseCountBuffer)
        {
            cmd.DrawProceduralIndirect(sharedIndex, identity, material, PassMDI, MeshTopology.Triangles,
                                       bufferWithArgs: mdiArgs, argsOffset: 0, drawCount: (uint)maxDrawCount,
                                       countBuffer: mdiDrawCount, countBufferOffset: 0,
                                       properties: _mdiMpb);
        }
        else
        {
            // Every subdraw is submitted, empty draws included
            cmd.DrawProceduralIndirect(sharedIndex, identity, material, PassMDI, MeshTopology.Triangles,
                                       bufferWithArgs: drawArgs, argsOffset: 0, drawCount: (uint)maxDrawCount,
                                       properties: _mdiMpb);
        }
    }

    private static Vector4 CoverageChannel(CoverageSource source)
    {
        return source == CoverageSource.Luminance
            ? new Vector4(1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f, 0.0f)
            : new Vector4(0, 0, 0, 1);
    }

    // Layer visibility
    private bool IsVisible(int li)
    {
        if (volume == null || li >= volume.LayerCount) return true;
        var lv = volume.Layer(li);
        return lv == null || lv.visible;
    }

    // Debug HUD
    private void UpdateStats()
    {
        if (layers == null || drawArgs == null || preCullCounts == null) return;

        if (_statsPending && StatsReadbackDone())
        {
            CollectStatsReadback();
            _statsPending = false;
        }

        if (!_statsPending) RequestStatsReadback();
    }

    private bool StatsReadbackDone()
    {
        if (!_argsRequest.done || !_preCullRequest.done) return false;
        for (int i = 0; i < _tallyRequested.Length; i++)
            if (_tallyRequested[i] && !_tallyRequests[i].done) return false;
        return true;
    }

    private void RequestStatsReadback()
    {
        if (_tallyRequests.Length != layers.Length)
        {
            _tallyRequests = new AsyncGPUReadbackRequest[layers.Length];
            _tallyRequested = new bool[layers.Length];
        }
        if (_tallyReadback.Length != layers.Length * LODS)
            _tallyReadback = new uint[layers.Length * LODS];

        _argsRequest = AsyncGPUReadback.Request(drawArgs);
        _preCullRequest = AsyncGPUReadback.Request(preCullCounts);
        for (int i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            _tallyRequested[i] = layer != null && layer.tally != null;
            if (_tallyRequested[i]) _tallyRequests[i] = AsyncGPUReadback.Request(layer.tally);
        }
        _statsPending = true;
    }

    private void CollectStatsReadback()
    {
        if (_argsRequest.hasError || _preCullRequest.hasError) return;

        var args = _argsRequest.GetData<uint>();
        var pre = _preCullRequest.GetData<uint>();
        int argsCount = Mathf.Min(args.Length, _argsReadback.Length);
        for (int k = 0; k < argsCount; k++) _argsReadback[k] = args[k];
        int preCount = Mathf.Min(pre.Length, _preCullReadback.Length);
        for (int k = 0; k < preCount; k++) _preCullReadback[k] = pre[k];

        System.Array.Clear(_tallyReadback, 0, _tallyReadback.Length);
        for (int i = 0; i < _tallyRequested.Length; i++)
        {
            if (!_tallyRequested[i] || _tallyRequests[i].hasError) continue;
            var tally = _tallyRequests[i].GetData<uint>();
            int n = Mathf.Min(tally.Length, LODS);
            for (int L = 0; L < n; L++) _tallyReadback[i * LODS + L] = tally[L];
        }

        ComputeStats();
    }

    private void ComputeStats()
    {
        GrasslandStats.Reset();

        for (int i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer == null || layer.count == 0 || !IsVisible(i)) continue;

            for (int L = 0; L < LODS; L++)
            {
                if (!layer.active[L]) continue;
                int inst = 0;
                for (int c = 0; c < cells; c++)
                {
                    int cellInstances = (int)_argsReadback[DrawId(i, L, c) * ArgsPerDraw + ArgInstanceCount];
                    inst += cellInstances;
                    if (cellInstances > 0) GrasslandStats.SubDraws++;
                    else if (_preCullReadback[DrawId(i, L, c)] > 0) GrasslandStats.CulledDraws++;
                }
                GrasslandStats.Instances[L] += inst;
                GrasslandStats.Vertices[L] += (long)inst * layer.vcount[L];

                int missing = Mathf.Max(0, (int)_tallyReadback[i * LODS + L] - inst);
                GrasslandStats.CulledInstances += missing;
                GrasslandStats.CulledVertices += (long)missing * layer.vcount[L];
            }
        }
    }

    public void DisposeResources()
    {
        FlushStatsReadback();
        void Rel(ComputeBuffer b) { if (b != null) b.Release(); }
        if (layers != null)
            foreach (var layer in layers)
            {
                if (layer == null) continue;
                Rel(layer.positions); layer.positions = null;
                Rel(layer.tally); layer.tally = null;
                for (int L = 0; L < LODS; L++)
                {
                    layer.active[L] = false;
                }
            }
        ReleaseSharedGeometry();
        if (drawParamsBuffer != null) { drawParamsBuffer.Release(); drawParamsBuffer = null; }
        layers = new LayerGPU[0];
        initialized = false;
    }
}
