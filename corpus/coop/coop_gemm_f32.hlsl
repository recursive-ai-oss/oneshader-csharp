// SPDX-License-Identifier: MIT OR Apache-2.0
// Provenance: written for this repository. A 64x64 output tile of a float GEMM (K = 128) computed by eight simdgroups of 32 invocations,
// with 64x64 float tiles of A and B staged in groupshared memory: exactly 32768 bytes declared, the whole threadgroup-memory budget of
// common GPUs. Each simdgroup owns two 16x16 blocks of the output and loads its operands straight from the groupshared tiles.

StructuredBuffer<float> A : register(t0);      // 64 x 128, row-major
StructuredBuffer<float> B : register(t1);      // 128 x 64, row-major
RWStructuredBuffer<float> C : register(u2);    // 64 x 64, row-major

groupshared float TileA[64 * 64];
groupshared float TileB[64 * 64];

typedef CooperativeMatrix<float, 16, 16, MatrixUse::A> MatA;
typedef CooperativeMatrix<float, 16, 16, MatrixUse::B> MatB;
typedef CooperativeMatrix<float, 16, 16, MatrixUse::Accumulator> MatC;

[shader("compute")]
[numthreads(256, 1, 1)]
void GemmTile(uint tid : SV_GroupIndex)
{
    uint sg = tid / 32;
    uint rowBlock = sg / 2;          // 0..3
    uint colBlock0 = (sg % 2) * 2;   // 0 or 2; the second block is the next column

    MatC acc0 = MatC::Zero();
    MatC acc1 = MatC::Zero();

    for (uint k0 = 0; k0 < 128; k0 += 64)
    {
        GroupMemoryBarrierWithGroupSync();
        for (uint i = tid; i < 64 * 64; i += 256)
        {
            uint r = i / 64;
            uint c = i % 64;
            TileA[i] = A[r * 128 + k0 + c];
            TileB[i] = B[(k0 + r) * 64 + c];
        }
        GroupMemoryBarrierWithGroupSync();

        for (uint k = 0; k < 64; k += 16)
        {
            MatA a;
            MatB b0;
            MatB b1;
            a.Load(TileA, rowBlock * 16 * 64 + k, 64, MatrixLayout::RowMajor);
            b0.Load(TileB, k * 64 + colBlock0 * 16, 64, MatrixLayout::RowMajor);
            b1.Load(TileB, k * 64 + (colBlock0 + 1) * 16, 64, MatrixLayout::RowMajor);
            acc0 = MatC::MulAdd(a, b0, acc0);
            acc1 = MatC::MulAdd(a, b1, acc1);
        }
    }

    acc0.Store(C, rowBlock * 16 * 64 + colBlock0 * 16, 64, MatrixLayout::RowMajor);
    acc1.Store(C, rowBlock * 16 * 64 + (colBlock0 + 1) * 16, 64, MatrixLayout::RowMajor);
}
