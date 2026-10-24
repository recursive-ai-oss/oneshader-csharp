// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: per-particle integration over a structured buffer of structs.

#ifndef PARTICLE_GRAVITY
#define PARTICLE_GRAVITY 9.8
#endif

struct Particle
{
    float2 Position;
    float2 Velocity;
    float4 Color;
    float Age;
    float MaxAge;
    float2 Padding;
};

[[vk::binding(0, 0)]] cbuffer Simulation
{
    float DeltaTime;
    uint Count;
    float2 Bounds; // the simulated box is [0, Bounds]
};

[[vk::binding(1, 0)]] RWStructuredBuffer<Particle> Particles;

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Count)
    {
        return;
    }

    Particle p = Particles[id.x];
    float maxAge = max(p.MaxAge, 0.25);
    p.Age += DeltaTime;
    if (p.Age >= maxAge)
    {
        // Respawn at the top centre, falling from rest.
        p.Age = 0.0;
        p.Position = float2(0.5 * Bounds.x, 0.0);
        p.Velocity = float2(0.0, 0.0);
    }
    else
    {
        p.Velocity += float2(0.0, PARTICLE_GRAVITY) * DeltaTime;
        p.Position += p.Velocity * DeltaTime;
        if (p.Position.y > Bounds.y)
        {
            p.Position.y = 2.0 * Bounds.y - p.Position.y;
            p.Velocity.y = -0.5 * p.Velocity.y;
        }
        if (p.Position.x < 0.0 || p.Position.x > Bounds.x)
        {
            p.Position.x = clamp(p.Position.x, 0.0, Bounds.x);
            p.Velocity.x = -p.Velocity.x;
        }
    }
    p.Color.a = 1.0 - p.Age / maxAge;
    Particles[id.x] = p;
}
