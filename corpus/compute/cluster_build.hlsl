// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: builds the view-space bounding box of every cluster of a clustered
// light grid. Tiles divide the screen evenly; depth slices are exponential between near and far.

struct ClusterBounds
{
    float4 Min;
    float4 Max;
};

[[vk::binding(0, 0)]] cbuffer Grid
{
    float4x4 InverseProjection; // clip to view
    uint3 GridSize;
    uint TileSize;               // pixels
    float2 ScreenSize;           // pixels
    float NearPlane;
    float FarPlane;
};

[[vk::binding(1, 0)]] RWStructuredBuffer<ClusterBounds> Clusters;

float3 ViewPointOnNearPlane(float2 pixel)
{
    float2 ndc = pixel / ScreenSize * 2.0 - 1.0;
    float4 view = mul(InverseProjection, float4(ndc, 0.0, 1.0));
    return view.xyz / view.w;
}

// The eye is the origin, so scaling a point scales its depth.
float3 AtDepth(float3 p, float depth)
{
    return p * (depth / -p.z);
}

[shader("compute")]
[numthreads(4, 4, 4)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (any(id >= GridSize))
    {
        return;
    }
    float2 pixelMin = float2(id.xy * TileSize);
    float2 pixelMax = pixelMin + TileSize;
    float3 pMin = ViewPointOnNearPlane(pixelMin);
    float3 pMax = ViewPointOnNearPlane(pixelMax);

    float ratio = FarPlane / NearPlane;
    float sliceNear = NearPlane * pow(ratio, (float)id.z / GridSize.z);
    float sliceFar = NearPlane * pow(ratio, (float)(id.z + 1) / GridSize.z);

    float3 a = AtDepth(pMin, sliceNear);
    float3 b = AtDepth(pMax, sliceNear);
    float3 c = AtDepth(pMin, sliceFar);
    float3 d = AtDepth(pMax, sliceFar);

    ClusterBounds bounds;
    bounds.Min = float4(min(min(a, b), min(c, d)), sliceNear);
    bounds.Max = float4(max(max(a, b), max(c, d)), sliceFar);
    Clusters[id.x + GridSize.x * (id.y + GridSize.y * id.z)] = bounds;
}
