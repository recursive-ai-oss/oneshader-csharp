// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository, reduced from a user report: a `continue` guarded by a condition, inside nested [unroll] loops.
// Elements whose row is at or past Count.x, or whose column is at or past Count.y, must not be written.

cbuffer P : register(b0) { uint2 Count; };
RWStructuredBuffer<uint> Out : register(u1);

[shader("compute")]
[numthreads(1, 1, 1)]
void Main()
{
    [unroll] for (uint row = 0; row < 4; row++)
    {
        if (row >= Count.x) { continue; }
        [unroll] for (uint col = 0; col < 4; col++)
        {
            if (col >= Count.y) { continue; }
            Out[row * 4 + col] = 1;
        }
    }
}
