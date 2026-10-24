// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: instanced UI rectangles.
//
// Every instance is one quad, expanded in the vertex shader from SV_VertexID and read from a
// structured buffer by SV_InstanceID; there is no per-instance vertex buffer. The pixel shader
// shades from an analytic signed distance to a rounded rectangle with four independent corner
// radii, so edges, the border and the fill stay anti-aliased at any size:
//   - border: its own width and colour, drawn over the fill;
//   - fill: solid, linear gradient (two points) or radial gradient (centre and radius);
//   - output: premultiplied alpha, meant for blending with ONE, ONE_MINUS_SRC_ALPHA.
// Coordinates are pixels with the origin at the top-left and y pointing down.

#ifndef UI_AA_WIDTH
#define UI_AA_WIDTH 1.0 // width of the anti-aliasing ramp in pixels
#endif

static const uint FillSolid = 0;
static const uint FillLinear = 1;
static const uint FillRadial = 2;

struct UiInstance
{
    float4 Rect;        // x, y, width, height
    float4 Radii;       // top-left, top-right, bottom-right, bottom-left
    float4 Color0;      // solid colour, or gradient start colour (straight alpha)
    float4 Color1;      // gradient end colour (straight alpha)
    float4 BorderColor; // straight alpha
    float4 Gradient;    // linear: start.xy end.xy; radial: centre.xy radius .
                        // Points are in pixels relative to the rectangle's top-left.
    float BorderWidth;  // pixels; may be smaller than one
    uint FillMode;
    float2 Padding;
};

[[vk::binding(0, 0)]] cbuffer Frame
{
    float2 ViewportSize;
    float2 FramePad;
};

[[vk::binding(1, 0)]] StructuredBuffer<UiInstance> Instances;

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Local : TEXCOORD0; // pixels from the rectangle's centre
    nointerpolation uint Instance : TEXCOORD1;
};

[shader("vertex")]
VertexOutput VSMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    UiInstance instance = Instances[instanceId];

    // Two triangles from six vertex ids: corners 0 1 2, 2 1 3 packed as bit pairs.
    uint corner = (0xDA4u >> (vertexId * 2u)) & 3u;
    float2 unit = float2(corner & 1u, corner >> 1u);

    // Grow the quad so the anti-aliasing ramp outside the edge is not clipped.
    float margin = ceil(UI_AA_WIDTH) + 1.0;
    float2 size = instance.Rect.zw;
    float2 pixel = instance.Rect.xy - margin + unit * (size + 2.0 * margin);

    VertexOutput output;
    output.Position = float4(pixel / ViewportSize * 2.0 - 1.0, 0.0, 1.0);
    output.Local = pixel - (instance.Rect.xy + 0.5 * size);
    output.Instance = instanceId;
    return output;
}

// Signed distance in pixels to a rounded box centred on the origin; negative inside.
float RoundedBoxDistance(float2 p, float2 halfSize, float4 radii)
{
    // Pick the radius of the corner this point is nearest to, and keep it within the box.
    float radius = (p.x < 0.0) ? ((p.y < 0.0) ? radii.x : radii.w)
                               : ((p.y < 0.0) ? radii.y : radii.z);
    radius = min(radius, min(halfSize.x, halfSize.y));
    float2 q = abs(p) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float4 Premultiply(float4 c)
{
    return float4(c.rgb * c.a, c.a);
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    UiInstance instance = Instances[input.Instance];
    float2 halfSize = 0.5 * instance.Rect.zw;

    float distance = RoundedBoxDistance(input.Local, halfSize, instance.Radii);

    // One pixel of the distance field: its screen-space gradient length is close to 1 for a
    // true distance, and stays correct if the quad is ever scaled.
    float pixelSize = max(length(float2(ddx(distance), ddy(distance))), 1e-3);
    float aa = UI_AA_WIDTH * pixelSize;
    float outer = saturate(0.5 - distance / aa);
    float inner = saturate(0.5 - (distance + instance.BorderWidth) / aa);

    float2 fromTopLeft = input.Local + halfSize; // relative to the top-left
    float t = 0.0;
    if (instance.FillMode == FillLinear)
    {
        float2 start = instance.Gradient.xy;
        float2 axis = instance.Gradient.zw - start;
        t = saturate(dot(fromTopLeft - start, axis) / max(dot(axis, axis), 1e-6));
    }
    else if (instance.FillMode == FillRadial)
    {
        t = saturate(length(fromTopLeft - instance.Gradient.xy) / max(instance.Gradient.z, 1e-3));
    }
    float4 fill = Premultiply(lerp(instance.Color0, instance.Color1, t));
    float4 border = Premultiply(instance.BorderColor);

    // The fill extends under the border; the border ring is composited over it.
    float4 result = fill * outer;
    float ring = max(outer - inner, 0.0);
    result = border * ring + result * (1.0 - border.a * ring);
    return result;
}
