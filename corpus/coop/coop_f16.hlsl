// SPDX-License-Identifier: MIT OR Apache-2.0
// Provenance: a consumer's request for cooperative matrices, lightly adapted. 16x16x16 product of two float16_t tiles staged through groupshared memory.
// Requested HLSL surface: subgroup-scope cooperative matrices.
//
// One 16 x 16 x 16 product per subgroup, small enough to check the expected output by eye.
//
// Surface:
//   CooperativeMatrix<T, Rows, Cols, Use>      T = float16_t, bfloat16_t (see coop_bf16.hlsl), float (integer elements are not supported yet)
//                                              Use = MatrixUse::A, MatrixUse::B or MatrixUse::Accumulator
//   m.Load(buffer, elementIndex, stride, layout)   buffer is a (RW)StructuredBuffer or a groupshared array of T; stride in elements
//   m.Store(rwBuffer, elementIndex, stride, layout)
//   CooperativeMatrix<TC,M,N,Accumulator>::MulAdd(a, b, c)   a: M x K (Use A), b: K x N (Use B), c: M x N
//   m.Length(), m[i]                                 per-invocation share of the elements (element-wise ops, as in the SPIR-V extension)
// All members must be reached by every invocation of the subgroup (no divergent control flow around them).
//
// The shape is chosen by the program. The compiler does not need to know which shapes a device supports; the host queries that
// (VK_KHR_cooperative_matrix properties, simdgroup_matrix on Metal) and picks a kernel built for it.

StructuredBuffer<float16_t> A : register(t0);
StructuredBuffer<float16_t> B : register(t1);
RWStructuredBuffer<float> C : register(u2);

groupshared float16_t TileA[16 * 16];
groupshared float16_t TileB[16 * 16];

[shader("compute")]
[numthreads(32, 1, 1)]
void CooperativeProduct(uint tid : SV_GroupIndex)
{
    // Stage the operands (every invocation takes eight elements of each tile).
    for (uint i = tid; i < 256; i += 32)
    {
        TileA[i] = A[i];
        TileB[i] = B[i];
    }
    GroupMemoryBarrierWithGroupSync();

    CooperativeMatrix<float16_t, 16, 16, MatrixUse::A> a;
    CooperativeMatrix<float16_t, 16, 16, MatrixUse::B> b;
    CooperativeMatrix<float, 16, 16, MatrixUse::Accumulator> acc = CooperativeMatrix<float, 16, 16, MatrixUse::Accumulator>::Zero();

    a.Load(TileA, 0, 16, MatrixLayout::RowMajor);
    b.Load(TileB, 0, 16, MatrixLayout::RowMajor);
    acc = CooperativeMatrix<float, 16, 16, MatrixUse::Accumulator>::MulAdd(a, b, acc);
    acc.Store(C, 0, 16, MatrixLayout::RowMajor);
}
