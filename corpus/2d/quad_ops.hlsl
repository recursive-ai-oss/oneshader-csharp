// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: quad operations in a pixel shader. A smooth function of the uv is read from the
// other pixels of the 2x2 quad (across x, across y, diagonally, and from lane 0), and the differences are
// compared with the fine derivative, which they must equal in size.

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

// Three vertices cover the whole target; no vertex buffer.
[shader("vertex")]
VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    output.Uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(output.Uv * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float2 uv = input.Uv;
    float f = uv.x * uv.x * 3.0 + uv.y * uv.x * 2.0 + uv.y;

    float acrossX = QuadReadAcrossX(f);
    float acrossY = QuadReadAcrossY(f);
    float diagonal = QuadReadAcrossDiagonal(f);
    float corner = QuadReadLaneAt(f, 0);

    float dx = abs(acrossX - f);
    float dy = abs(acrossY - f);
    // The swap across x moves by exactly one fine derivative in x (up to rounding), and likewise for y.
    bool agrees = abs(dx - abs(ddx_fine(f))) < 1.0e-4 && abs(dy - abs(ddy_fine(f))) < 1.0e-4;

    // 2x2 blocks from the quad's first pixel, and the three differences.
    float blocks = frac(corner * 3.0);
    return float4(blocks, saturate(dx * 8.0), agrees ? saturate(abs(diagonal - f) * 8.0) : 0.0, 1.0);
}
