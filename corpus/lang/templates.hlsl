// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Templates: deduced and explicit arguments, value parameters, defaults, struct templates with members.

template<typename T>
T Twice(T v)
{
    return v + v;
}

template<typename T>
T Clamp3(T v, T lo, T hi)
{
    return min(max(v, lo), hi);
}

template<typename T, int N>
T SumArray(T a[N])
{
    T s = (T)0;
    for (int i = 0; i < N; i++)
    {
        s += a[i];
    }
    return s;
}

template<int N, typename T = float>
vector<T, N> Fill(T v)
{
    return (vector<T, N>)v;
}

template<typename T>
struct Box
{
    T value;

    T Get() { return value; }
    void Set(T v) { value = v; }
};

template<typename T, uint N>
struct Ring
{
    T items[N];
    uint head;

    void Push(T v)
    {
        items[head % N] = v;
        head++;
    }

    T At(uint i) { return items[(head + i) % N]; }
};

[[vk::binding(0, 0)]] RWStructuredBuffer<uint4> Results : register(u0);

[shader("compute")]
[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    float arr[4] = { 1.5, 2.5, 3.0, 4.0 };
    int iarr[3] = { 3, 4, 5 };

    Box<float> bf = { 2.5 };
    bf.Set(Twice(bf.Get()));
    Box<uint> bu;
    bu.value = 7u;

    Ring<int, 4> ring = { { 0, 0, 0, 0 }, 0u };
    for (uint i = 0; i < 6; i++)
    {
        ring.Push(int(i) * 10);
    }

    float3 clamped = Clamp3(float3(-1.0, 0.5, 2.0), float3(0, 0, 0), float3(1, 1, 1));
    float3 filled = Fill<3>(1.5);

    Results[0] = uint4(asuint(Twice(1.25)), asuint(Twice(0.5f)), (uint)Twice(21), Twice(uint(8)));
    Results[1] = uint4(asuint(SumArray<float, 4>(arr)), (uint)SumArray(iarr), asuint(bf.Get()), bu.Get());
    Results[2] = uint4((uint)ring.At(0), (uint)ring.At(1), (uint)ring.At(2), (uint)ring.At(3));
    Results[3] = uint4(asuint(clamped.x), asuint(clamped.y), asuint(clamped.z), asuint(filled.y));
}
