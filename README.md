# Unity Multi Draw Indirect

Multi Draw Indirect (MDI) can be used to submit multiple indirect draws with a single API call. Each command references an array of indirect argument buffers, specifying the number of drawn instances, vertices and vertex buffer data.

Indirect draw buffers can be accessed on the GPU via compute shaders. This is used to implement GPU-driven rendering techniques such as compute-based culling and LOD selection. MDI can be used to efficiently render a large amount of instances and geometry with lower CPU and GPU overhead:

<p align="center">
  <img width="100%" src=".github/images/mdi-demo-stats.gif" alt="mdi demo">
</p>


## Requirements
- Unity 6000.7 or later, including `SystemInfo.supportsMultiDrawIndirectCountBuffer`

## Platform Support

MDI support is limited to the Unity Graphics Kernel (UGK) targeting Vulkan.

| | `supportsMultiDrawIndirect` | `supportsMultiDrawIndirectCountBuffer` |
| ------------------------------ |:---:|:---:|
| Vulkan on UGK                  | with the device's `multiDrawIndirect` feature | additionally with `VK_KHR_draw_indirect_count` |
| Anything else (Metal, GLES, …) | no  | no  |

UGK is opt-in in Unity 6.7 beta. Pass the `-force-ugk` flag when running the build Android Player.

- Android: `adb shell am start -n <package>/com.unity3d.player.UnityPlayerActivity -e unity "-force-ugk"`

Where the device reports no MDI support, the sample falls back to one indirect draw per foliage cell and LOD, so
it still runs (and looks identical) everywhere.

## How to use

1. Open the "MDI-Demo" scene (`\Assets\Scenes\MDI-Demo.unity`)
2. Hit play

The "Debug HUD" toggle on the GrasslandVolume shows the number of drawn (and culled) sub-draws and instances.

### Rebake the texture array when switching platforms

The foliage textures are packed into one baked `Texture2DArray` (`Assets/Grassland/Textures/Grass_LayerTextures.asset`),
which keeps the compressed format of the build target that was active when it was baked. It is not recompressed per
platform, so an array baked for Android (ASTC) does not sample on most desktop GPUs, and the textured foliage (the
flowers) is clipped away.

The project rebakes it automatically whenever the active platform changes, as long as the MDI-Demo scene is open.
If the scene was not open when you switched, open it and run **Grassland > Bake Texture Array** before building.

On first open the project switches to the Android build profile.


## Demo project

This sample project implements a "GPU-driven" grass renderer feature using indirect rendering. 

The renderer feature injects a scriptable render pass which draws a large number of foliage instances. All instances, foliage types and LOD levels are rendered using a single Multi Draw Indirect (MDI) call and a shared vertex/index and position buffer:   

'ProceduralRenderingFeature.cs':
```
  mpb.SetBuffer(vertexBufferUID, sharedVertex);
  mpb.SetBuffer(uvBufferUID, sharedUV);
  mpb.SetBuffer(culledPositionsBufferUID, culledPositions);
  mpb.SetBuffer(drawParamsUID, drawParamsBuffer);
  mpb.SetInt(instanceCapacityUID, instanceCapacity);

  cmd.DrawProceduralIndirect(sharedIndex, identity, material, PassMDI, MeshTopology.Triangles, drawArgs, 0, maxDrawCount, properties: _mdiMpb);
```

The argument buffer is written on the GPU, one thread per sub-draw. The instance count comes from what
culling emitted into that sub-draw's region; the geometry range is shared by every cell of a (layer, LOD):

'ComputePrepass.compute':
```
  uint layerLod = drawId / max(cellCount, 1u);

  uint o = drawId * ARGS_PER_DRAW;
  drawArgs[o + ARG_INDEX_COUNT_PER_INSTANCE] = indexCountPerInstance[layerLod];
  drawArgs[o + ARG_INSTANCE_COUNT]           = min(culledCounts[drawId], instanceCapacity);
  drawArgs[o + ARG_START_INDEX]              = startIndex[layerLod];
  drawArgs[o + ARG_BASE_VERTEX_INDEX]        = 0u;  
  drawArgs[o + ARG_START_INSTANCE]           = 0u;
```

Where the device also reports `supportsMultiDrawIndirectCountBuffer`, the sample hands the draw a count
buffer instead. A compute pass packs the live slots to the front of a second argument buffer and writes
how many there are; the draw treats `maxDrawCount` as a ceiling only and clamps it to the count the GPU
produced, so empty slots are never submitted and the number of draws never travels through the CPU:

'ComputePrepass.compute':
```
  uint src = drawId * ARGS_PER_DRAW;
  if (drawArgs[src + ARG_INDEX_COUNT_PER_INSTANCE] == 0u ||
      drawArgs[src + ARG_INSTANCE_COUNT] == 0u) return;   // no geometry or no instances

  uint slot;
  InterlockedAdd(mdiDrawCount[0], 1u, slot);

  uint dst = slot * ARGS_PER_DRAW;
  for (uint i = 0u; i < ARGS_PER_DRAW; i++)
      mdiArgs[dst + i] = drawArgs[src + i];
```

Additional compute kernels are used to apply LOD-selection and culling on the GPU, with a high degree of parallelism and without round-trips to the CPU.

<p align="center">
  <img width="100%" src=".github/images/gpu-culling.gif" alt="gpu culling">
</p>

### Sub-draw and instance culling

Ordinary indirect rendering can only cull *instances*. The draw call itself is always recorded and the GPU culling pass must test against each instance, increasing the GPU overhead. Multi Draw Indirect can cull entire *sub-draws* to further optimize our GPU-driven renderers.

In this example, the grassy field is split into a 2-dimensional grid of foliage cells. Each cell is culled and submitted using a separate sub-draw which can be culled independently.

'ComputePrepass.compute':
```
  uint CellFromPosition(float3 pos)
  {
      int cx = clamp((int)floor((pos.x - gridParams.x) * gridParams.z), 0, (int)cellDim - 1);
      int cz = clamp((int)floor((pos.z - gridParams.y) * gridParams.w), 0, (int)cellDim - 1);
      return (uint)(cz * (int)cellDim + cx);
  }

  // The clump's cell picks the sub-draw within its (layer, LOD).
  uint cell = CellFromPosition(pos.xyz);
  uint nearDraw = drawIdNear * cellCount + cell;
  uint farDraw  = drawIdFar  * cellCount + cell;
```

A camera looking across the field typically sees a fraction of the cells, so most of the sub-draws are dropped before the call is issued. 

<p align="center">
  <img width="100%" src=".github/images/subdraw-culling.png" alt="sub-draw culling">
</p>

Sub-draw chunking is controlled by the `cellCount` property which defines the number of foliage cells. A larger number of cells will produce more sub-draws with finer culling granularity. Culling runs in two levels. Each cell is first classified as fully outside / inside the frustum, or straddling an edge:

'ComputePrepass.compute':
```
  [unroll]
  for (int p = 0; p < 6; p++)
  {
      float3 n = frustumPlanes[p].xyz;
      float dist = dot(n, centre) + frustumPlanes[p].w;
      float r = dot(extent, abs(n));
      if (dist + r < -frustumPadding)
      {
          cellVisible[cell] = CELL_OUTSIDE;   // wholly behind this plane
          return;
      }
      if (dist - r < -frustumPadding) state = CELL_PARTIAL;
  }
```

A clump in a rejected cell is discarded on one buffer read, and a clump in a fully visible cell needs no test at all. Only cells crossing the edge of the frustum apply per-instance culling tests:

```
  uint cell = CellFromPosition(pos.xyz);
  uint cellState = cellVisible[cell];
  if (cellState == CELL_OUTSIDE) return;

  bool visible = (cellState == CELL_INSIDE);
  if (!visible)
  {
      // Only cells straddling an edge get here, so only they pay for per-clump tests.
      visible = distSq < cullRadius * cullRadius;
      ...
  }
```

A surviving clump is appended to the sub-draw's position buffer and the instance count is also incremented:

```
  void EmitInstance(uint drawId, float4 pos)
  {
      uint slot;
      InterlockedAdd(culledCounts[drawId], 1u, slot);
      if (slot < instanceCapacity)
          culledPositions[drawId * instanceCapacity + slot] = pos;
  }
```

A final compaction kernel drops every cell that came out empty, so they never reach the draw call:

```
  uint src = drawId * ARGS_PER_DRAW;
  if (drawArgs[src + ARG_INDEX_COUNT_PER_INSTANCE] == 0u ||
      drawArgs[src + ARG_INSTANCE_COUNT] == 0u) return;   // no geometry or no instances

  uint slot;
  InterlockedAdd(mdiDrawCount[0], 1u, slot);

  uint dst = slot * ARGS_PER_DRAW;
  for (uint i = 0u; i < ARGS_PER_DRAW; i++)
      mdiArgs[dst + i] = drawArgs[src + i];
```

### LOD selection

Even with GPU culling applied, a large amount of dense foliage clumps will drastically increase the vertex and pixel shading overhead. Impostor LODs are generated for each foliage type to reduce vertex density for distant renderers:

<p align="center">
  <img width="100%" src=".github/images/LODs.png" alt="LODs">
</p>

Similarly to culling, per-instance LOD selection is applied in parallel (on the GPU) via compute. The LOD level is first calculated based on camera distance. We then alter the vertex buffer offset and vertex count in the indirect draw argument buffer. Pointing the GPU to the LOD level's vertex data.

LOD cross-fade is also applied to avoid "pop-in" and improve quality. We first define an LOD fade radius.  Any instance within the fade radius is rendered twice (once per LOD level). Alpha dithering is then used to fade between the LOD levels:

```
  if (visible)
  {
      float dist = sqrt(distSq);
      float hb = lodBlend * 0.5;
      if (dist < lodDistance - hb) {  // draw near LOD 
          EmitInstance(drawIdNear, pos);      
      }       
      else if (dist < lodDistance + hb) {  // draw both LODs, fade via raster shader
          EmitInstance(drawIdNear, pos); 
          EmitInstance(drawIdFar, pos); 
      }
      else {  // draw far LOD
          EmitInstance(drawIdFar, pos);
      }                               
  }
```

'ParticleShaderCore.hlsl' (vertex stage):
```
  float2 lodFade = float2(0.0, 0.0);
  if (lodBandWidth > FADE_RANGE)
  {
      float d = distance(inst.xz, lodCenter.xz);
      float hb = lodBandWidth * 0.5;
      if (d > lodDistance - hb && d < lodDistance + hb)
      {
          // 0 at the near edge of the band, 1 at the far edge
          float t = saturate((d - (lodDistance - hb)) / lodBandWidth);
          lodFade = float2(t, lodLevel < 0.5 ? 0.0 : 1.0);
      }
  }
  o.lodFade = lodFade;
```

The pixel stage discards a fraction of each LOD using a Bayer dithering pattern and the pixel's screen position.

'ParticleShaderCore.hlsl' (pixel stage):
```
  static const float _Bayer4x4[16] = {
       0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
      12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
       3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
      15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0
  };

  float LodDitherThreshold(float2 pixel)
  {
      int2 p = int2(fmod(pixel, 4.0));
      return _Bayer4x4[p.y * 4 + p.x] + (0.5 / 16.0);
  }

  fragdata frag(v2f i)
  {
      if (lodBandWidth > 1e-4)
      {
          float thr = LodDitherThreshold(i.vertex.xy);
          float t = i.lodFade.x;

          // near LOD keeps pixels where thr >= t, far LOD keeps the complement
          clip(i.lodFade.y < 0.5 ? (thr - t) : (t - thr));
      }
      ...
  }
```

<p align="center">
  <img width="100%" src=".github/images/lod-debug-1.png" alt="LOD debug">
</p>

Applying GPU culling and LOD selection decreases the number of drawn instances by ~80% and number of rendered vertices by ~55%. Offloading the culling and LOD computation to the GPU also reduces the app's CPU overhead.

| GPU Culling   | Sub-draws  | Instances   | Vertex count (total) | Vertex count (LOD0) | Vertex count (LOD1) | 
| ------------- |:-----------|:-----------:|:--------------------:|:-------------------:| -------------------:| 
| Off           | 209        | 337,746     | 22,732,312           | 20,097,384          | 2,634,928           |
| On            | 78         | 64,478      | 10,551,417           | 10,068,521          | 482,896             |

<p align="center">
  <img width="100%" src=".github/images/debug-hud.png" alt="debug hud">
</p>

## Credits

1. "Stylized daisy" by "hildegunstdermythenmetz": https://sketchfab.com/3d-models/stylized-daisy-54045596253e478993ca96572cb1bf6b. License (CC BY 4.0): https://creativecommons.org/licenses/by/4.0/

2. "Blue poppy inflated version" by "XCL": https://sketchfab.com/3d-models/blue-poppy-inflated-version-8fbc9d61acf144b9a35410eb8ce120a7. License (CC BY 4.0): https://creativecommons.org/licenses/by/4.0/

Both assets were altered (re-modelled and/or re-textured) in Blender to fit the project's needs. 

## Frame times

| GPU         | Quality     | GPU time | CPU time |
| ----------- |:------------|---------:|---------:|
| Xclipse 540 | High (MSAA) | 57 ms    | 59 ms    |
| Xclipse 540 | High        | 42 ms    | 43 ms    |
| Xclipse 540 | Low         | 27 ms    | 28 ms    |
| Xclipse 960 | High (MSAA) | 18 ms    | 26 ms    |
| Xclipse 960 | High        | 24 ms    | 33 ms    |
| Xclipse 960 | Low         | 13 ms    | 16 ms    |
