// SPDX-License-Identifier: MIT OR Apache-2.0
// Provenance: written for this repository. A 16x32 by 32x16 product built from two 16x16x16 cooperative-matrix steps, exercising every
// load and store path: A is read column-major from a structured buffer at an element offset with a runtime stride, B is staged in
// groupshared memory (row stride 20, not a multiple of the tile) and read row-major, the result is stored column-major with stride 19,
// and a second copy goes through Length() and m[i] (doubled) to a row-major buffer.

cbuffer Params : register(b3)
{
    uint aOffset;   // element offset of A inside its buffer
    uint aStride;   // distance between two columns of A, in elements
    uint pad0;
    uint pad1;
};

StructuredBuffer<float16_t> A : register(t0);
StructuredBuffer<float16_t> B : register(t1);
RWStructuredBuffer<float> C : register(u2);
RWStructuredBuffer<float> D : register(u4);

groupshared float16_t TileB[32 * 20];

typedef CooperativeMatrix<float16_t, 16, 16, MatrixUse::A> MatA;
typedef CooperativeMatrix<float16_t, 16, 16, MatrixUse::B> MatB;
typedef CooperativeMatrix<float, 16, 16, MatrixUse::Accumulator> MatC;

[shader("compute")]
[numthreads(32, 1, 1)]
void LayoutProduct(uint tid : SV_GroupIndex)
{
    for (uint i = tid; i < 32 * 16; i += 32)
    {
        TileB[(i / 16) * 20 + (i % 16)] = B[i];
    }
    GroupMemoryBarrierWithGroupSync();

    MatC acc = MatC::Zero();
    for (uint step = 0; step < 2; step++)
    {
        MatA a;
        MatB b;
        a.Load(A, aOffset + step * 16 * aStride, aStride, MatrixLayout::ColumnMajor);
        b.Load(TileB, step * 16 * 20, 20, MatrixLayout::RowMajor);
        acc = MatC::MulAdd(a, b, acc);
    }
    acc.Store(C, 0, 19, MatrixLayout::ColumnMajor);

    MatC twice = acc;
    for (uint e = 0; e < twice.Length(); e++)
    {
        twice[e] = acc[e] * 2.0f;
    }
    twice.Store(D, 0, 16, MatrixLayout::RowMajor);
}
