// Component-wise built-ins on matrices: no target has them on matrix types, so each row goes through as a vector.
cbuffer C { float3x3 M; float3x3 N; float2x2 K; };

[shader("pixel")]
float4 main() : SV_Target
{
    float3x3 a = saturate(M) + lerp(M, N, 0.5) + clamp(M, 0, 1) + pow(abs(M), N) + sin(M);
    float3x3 b = smoothstep(M, N, M) + mad(M, N, M) + max(M, 0.5) + rcp(M);
    float2x2 c = min(K, 3);
    return float4(a[0] + b[1] + a[2], c[0].x + c[1].y);
}
