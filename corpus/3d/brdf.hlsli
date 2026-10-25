// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: shared microfacet BRDF helpers for the 3D corpus shaders.
// Formulas follow the published papers: Walter et al. 2007 (GGX), Heitz 2014 (height-correlated
// Smith visibility), Schlick 1994 (Fresnel), and the glTF 2.0 specification's metal/rough model.

#ifndef BRDF_HLSLI
#define BRDF_HLSLI

static const float PI = 3.14159265358979;

// Below this a perfectly smooth surface would produce a singular, aliased highlight.
static const float MIN_ROUGHNESS = 0.045;

float Pow5(float x)
{
    float x2 = x * x;
    return x2 * x2 * x;
}

// Trowbridge-Reitz normal distribution; alpha is roughness squared.
float DistributionGGX(float NoH, float alpha)
{
    float a2 = alpha * alpha;
    float d = NoH * NoH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

// Height-correlated Smith visibility term, already divided by 4 * NoL * NoV.
float VisibilitySmithGGX(float NoV, float NoL, float alpha)
{
    float a2 = alpha * alpha;
    float gv = NoL * sqrt(NoV * NoV * (1.0 - a2) + a2);
    float gl = NoV * sqrt(NoL * NoL * (1.0 - a2) + a2);
    return 0.5 / max(gv + gl, 1e-5);
}

float3 FresnelSchlick(float3 f0, float VoH)
{
    return f0 + (1.0 - f0) * Pow5(saturate(1.0 - VoH));
}

// Smooth window so a light's influence reaches exactly zero at its range, on top of inverse-square falloff.
float RangeAttenuation(float distance, float range)
{
    float d2 = distance * distance;
    float r2 = range * range;
    float window = saturate(1.0 - (d2 * d2) / (r2 * r2));
    return window * window / max(d2, 1e-4);
}

// Cook-Torrance specular plus Lambert diffuse for one light direction L carrying `radiance`.
// diffuseColor and f0 come from the metal/rough split of the base colour.
float3 EvaluateLight(float3 N, float3 V, float3 L, float3 radiance, float3 diffuseColor, float3 f0, float roughness)
{
    float NoL = saturate(dot(N, L));
    if (NoL <= 0.0)
    {
        return 0.0;
    }
    float3 H = normalize(V + L);
    float NoV = max(dot(N, V), 1e-4);
    float NoH = saturate(dot(N, H));
    float VoH = saturate(dot(V, H));
    float alpha = roughness * roughness;
    float3 F = FresnelSchlick(f0, VoH);
    float3 specular = F * (DistributionGGX(NoH, alpha) * VisibilitySmithGGX(NoV, NoL, alpha));
    float3 diffuse = (1.0 - F) * diffuseColor / PI;
    return (diffuse + specular) * radiance * NoL;
}

#endif
