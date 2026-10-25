// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: prefilters an environment cube map for one roughness by GGX importance
// sampling (split-sum approximation). Samples come from a Hammersley sequence; the source mip level is
// chosen from each sample's probability density so sparse samples read blurrier texels. One thread per
// output texel, the dispatch's z is the cube face.

[[vk::binding(0, 0)]] cbuffer Prefilter
{
    float Roughness;
    uint SampleCount;
    float SourceFaceSize; // texels along one edge of the source's top level
    uint OutputFaceSize;
};

[[vk::binding(1, 0)]] TextureCube<float4> Environment;
[[vk::binding(2, 0)]] SamplerState LinearClamp;
[[vk::binding(3, 0)]] [[vk::image_format("rgba16f")]] RWTexture2DArray<float4> Output;

static const float PI = 3.14159265358979;

// Direction through a texel centre of a cube face, with the face axes of the Vulkan specification.
float3 FaceDirection(uint face, float2 uv)
{
    float sc = uv.x * 2.0 - 1.0;
    float tc = uv.y * 2.0 - 1.0;
    float3 d;
    switch (face)
    {
        case 0: d = float3(1.0, -tc, -sc); break;
        case 1: d = float3(-1.0, -tc, sc); break;
        case 2: d = float3(sc, 1.0, tc); break;
        case 3: d = float3(sc, -1.0, -tc); break;
        case 4: d = float3(sc, -tc, 1.0); break;
        default: d = float3(-sc, -tc, -1.0); break;
    }
    return normalize(d);
}

float2 Hammersley(uint i, uint count)
{
    return float2((float)i / (float)count, (float)reversebits(i) * 2.3283064365386963e-10);
}

// Half vector distributed by the GGX normal distribution around +z.
float3 ImportanceSampleGGX(float2 xi, float alpha)
{
    float phi = 2.0 * PI * xi.x;
    float cosTheta = sqrt((1.0 - xi.y) / (1.0 + (alpha * alpha - 1.0) * xi.y));
    float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
    return float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);
}

float DistributionGGX(float NoH, float alpha)
{
    float a2 = alpha * alpha;
    float d = NoH * NoH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= OutputFaceSize || id.y >= OutputFaceSize)
    {
        return;
    }
    float2 uv = (float2(id.xy) + 0.5) / (float)OutputFaceSize;
    float3 N = FaceDirection(id.z, uv);
    float3 up = abs(N.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
    float3 tangent = normalize(cross(up, N));
    float3 bitangent = cross(N, tangent);
    float alpha = Roughness * Roughness;
    float texelSolidAngle = 4.0 * PI / (6.0 * SourceFaceSize * SourceFaceSize);

    float3 sum = 0.0;
    float weight = 0.0;
    for (uint i = 0; i < SampleCount; ++i)
    {
        float3 H = ImportanceSampleGGX(Hammersley(i, SampleCount), alpha);
        float3 Hw = tangent * H.x + bitangent * H.y + N * H.z;
        float3 L = normalize(2.0 * dot(N, Hw) * Hw - N); // V = N
        float NoL = saturate(dot(N, L));
        if (NoL > 0.0)
        {
            float NoH = saturate(dot(N, Hw));
            float pdf = DistributionGGX(NoH, alpha) * 0.25; // D * NoH / (4 * VoH) with V = N
            float sampleSolidAngle = 1.0 / (max((float)SampleCount * pdf, 1e-6));
            float mip = max(0.5 * log2(sampleSolidAngle / texelSolidAngle) + 1.0, 0.0);
            sum += Environment.SampleLevel(LinearClamp, L, mip).rgb * NoL;
            weight += NoL;
        }
    }
    Output[uint3(id.xy, id.z)] = float4(sum / max(weight, 1e-4), 1.0);
}
