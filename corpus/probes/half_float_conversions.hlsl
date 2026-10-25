// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: VSMain vs
// f16tof32 and f32tof16 (packed vertex attributes).
struct VertexInput
{
    float3 Position : POSITION;
    uint2 PackedNormal : NORMAL0;
};

float4 VSMain(VertexInput input) : SV_Position
{
    float3 n = float3(f16tof32(input.PackedNormal.x & 0xFFFF), f16tof32(input.PackedNormal.x >> 16), f16tof32(input.PackedNormal.y));
    uint packedX = f32tof16(n.x);
    return float4(input.Position + n * packedX, 1.0);
}
