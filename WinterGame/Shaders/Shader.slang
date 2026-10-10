struct DrawRecord {
    float3 origin;
    float voxelSize;
};

[[vk::binding(0, 0)]] StructuredBuffer<DrawRecord> draws;

// SunShadow.CascadeCount in C#:
static const uint ShadowCascadeCount = 3;

// The steepest receiver plane the shadow filter follows, as depth per lateral distance in the light's view. 10 is a triangle about 6 degrees from the light's direction:
static const float MaxReceiverSlope = 10.0;

// ShadowCascade in C#, the cascade's texel size in metres in parameters.x:
struct ShadowCascade {
    row_major float4x4 viewProj;
    float4 parameters;
};

// FrameLighting in C#, whose members are all 16 bytes or a multiple of it, so this std140 layout matches it:
struct FrameLighting {
    row_major float4x4 viewProj;
    row_major float4x4 inverseViewProj;
    float4 cameraPosition;
    float4 lightDirection;
    float4 lightRadiance;
    float4 skyZenith;
    float4 skyHorizon;
    ShadowCascade shadowCascades[ShadowCascadeCount];
    float4 lightVolumePlacement;
    float4 debugOptions;
};

[[vk::binding(1, 0)]] ConstantBuffer<FrameLighting> lighting;
[[vk::binding(2, 0)]] Texture2D shadowMap;
[[vk::binding(3, 0)]] SamplerComparisonState shadowSampler;

// The sky visibility of each light cell, at the cell's brick coordinates modulo the texture's size:
[[vk::binding(4, 0)]] Texture3D<float> lightVolume;

// Nearest filtering with repeat addressing, so a read at a cell's centre returns that cell's texel wherever the cell lies:
[[vk::binding(5, 0)]] SamplerState lightSampler;

// LightEngine.MaxLevel in C#:
static const float MaxLightLevel = 8.0;

// How much brighter the sun's disc is than the light it casts on a surface facing it. The real ratio is about 15000, and any value that saturates through the tonemap looks the same:
static const float SunDiscBrightness = 50.0;

static const uint MaterialCount = 8;

// Linear albedo, because the world renders into a linear float target and the tonemap pass's sRGB write converts it:
static const float3 MaterialAlbedo[MaterialCount] = {
    float3(0.17, 0.11, 0.06),   // rock, the generic fallback
    float3(0.30, 0.38, 0.42),   // ice
    float3(0.78, 0.82, 0.88),   // snow
    float3(0.42, 0.31, 0.19),   // sandstone
    float3(0.16, 0.18, 0.21),   // shale
    float3(0.47, 0.46, 0.42),   // limestone
    float3(0.10, 0.10, 0.12),   // basalt
    float3(0.22, 0.20, 0.21)    // gneiss
};

// The debug view's colour for each shadow cascade, red, green and blue from the smallest:
static const float3 ShadowCascadeTints[ShadowCascadeCount] = {
    float3(1.0, 0.45, 0.45),
    float3(0.45, 1.0, 0.45),
    float3(0.45, 0.45, 1.0)
};

// The debug view's colour for how far a shadow texel stretches across a surface at its angle to the light, green, yellow, orange and red for under 2, 2 to 4, 4 to 11 and over 11 times:
static const float3 LightAngleTints[4] = {
    float3(0.45, 1.0, 0.45),
    float3(1.0, 1.0, 0.35),
    float3(1.0, 0.65, 0.3),
    float3(1.0, 0.3, 0.3)
};

struct VertexInput {
    float3 position;
    float3 normal;

    // The id of the vertex's own cell, one per cell, so a triangle reads it through its provoking vertex:
    uint material;
};

struct VertexOutput {
    float4 position : SV_Position;
    float3 worldPosition : WORLD_POSITION;
    float3 normal : NORMAL;

    nointerpolation uint material : MATERIAL;
};

[shader("vertex")]
VertexOutput vsMain(VertexInput input, uint recordIndex : SV_StartInstanceLocation) {
    VertexOutput output;

    // The indirect command's firstInstance, which is the record's own index. SV_InstanceID would be the instance within the draw, which is always 0 here because every command has one instance.
    DrawRecord draw = draws[recordIndex];

    // Positions are chunk-local voxel units, so the origin stays exact and the scale is applied once:
    float3 worldPosition = (draw.origin + input.position.xyz) * draw.voxelSize;

    output.position = mul(float4(worldPosition, 1.0), lighting.viewProj);
    output.worldPosition = worldPosition;
    output.normal = input.normal.xyz;
    output.material = input.material;
    return output;
}

// One comparison at a position in texels inside a cascade's tile, filtered over its four nearest texels by the sampler.
// The reference depth follows the receiver's plane from the lookup at tileTexel towards the light, so the receiver does not shadow itself.
// It never follows the plane away from the light. Past a fold the plane would run behind the neighbouring triangle and shadow the receiver in a band, which is wide on a triangle at a grazing angle to the light:
float ShadowTap(uint cascade, float2 tapTexel, float2 tileTexel, float depth, float2 depthPerTexel, float2 mapSize) {
    float2 uv = float2(cascade * mapSize.y + tapTexel.x, tapTexel.y) / mapSize;
    return shadowMap.SampleCmpLevelZero(shadowSampler, uv, depth + min(dot(tapTexel - tileTexel, depthPerTexel), 0.0));
}

// A cascade's shadow at a position in texels inside its tile, filtered over 5x5 texels.
// This is Castano's optimized PCF, as in MJP's shadow sample. Nine filtered comparisons stand in for the 25 texels, and their weights and offsets follow the lookup's position inside its texel, so the result moves smoothly instead of stepping a texel at a time.
float FilteredShadow(uint cascade, float2 tileTexel, float depth, float2 depthPerTexel, float2 mapSize) {
    float2 baseTexel = floor(tileTexel + 0.5);
    float s = tileTexel.x + 0.5 - baseTexel.x;
    float t = tileTexel.y + 0.5 - baseTexel.y;
    baseTexel -= 0.5;

    float uw0 = 4.0 - 3.0 * s;
    float uw1 = 7.0;
    float uw2 = 1.0 + 3.0 * s;
    float u0 = (3.0 - 2.0 * s) / uw0 - 2.0;
    float u1 = (3.0 + s) / uw1;
    float u2 = s / uw2 + 2.0;

    float vw0 = 4.0 - 3.0 * t;
    float vw1 = 7.0;
    float vw2 = 1.0 + 3.0 * t;
    float v0 = (3.0 - 2.0 * t) / vw0 - 2.0;
    float v1 = (3.0 + t) / vw1;
    float v2 = t / vw2 + 2.0;

    float sum = 0.0;
    sum += uw0 * vw0 * ShadowTap(cascade, baseTexel + float2(u0, v0), tileTexel, depth, depthPerTexel, mapSize);
    sum += uw1 * vw0 * ShadowTap(cascade, baseTexel + float2(u1, v0), tileTexel, depth, depthPerTexel, mapSize);
    sum += uw2 * vw0 * ShadowTap(cascade, baseTexel + float2(u2, v0), tileTexel, depth, depthPerTexel, mapSize);

    sum += uw0 * vw1 * ShadowTap(cascade, baseTexel + float2(u0, v1), tileTexel, depth, depthPerTexel, mapSize);
    sum += uw1 * vw1 * ShadowTap(cascade, baseTexel + float2(u1, v1), tileTexel, depth, depthPerTexel, mapSize);
    sum += uw2 * vw1 * ShadowTap(cascade, baseTexel + float2(u2, v1), tileTexel, depth, depthPerTexel, mapSize);

    sum += uw0 * vw2 * ShadowTap(cascade, baseTexel + float2(u0, v2), tileTexel, depth, depthPerTexel, mapSize);
    sum += uw1 * vw2 * ShadowTap(cascade, baseTexel + float2(u1, v2), tileTexel, depth, depthPerTexel, mapSize);
    sum += uw2 * vw2 * ShadowTap(cascade, baseTexel + float2(u2, v2), tileTexel, depth, depthPerTexel, mapSize);

    // The weights on each axis sum to 12, so all nine sum to 144:
    return sum / 144.0;
}

// The change in a cascade's depth per texel across the plane of a pixel's triangle, from the screen derivatives of the world position.
// Each filter tap compares against this plane, so a sloped triangle does not shadow itself however far the filter reaches.
// The slope is limited to MaxReceiverSlope where the triangle is nearly parallel to the light.
float2 ReceiverDepthSlope(ShadowCascade shadowCascade, float3 positionDx, float3 positionDy, float texelSize, float cascadeSize) {
    // Depth is measured in texels too, so the plane's slope is a tangent:
    float depthPerMetre = length(float3(shadowCascade.viewProj[0].z, shadowCascade.viewProj[1].z, shadowCascade.viewProj[2].z));
    float3 scale = float3(0.5 * cascadeSize, 0.5 * cascadeSize, 1.0 / (depthPerMetre * texelSize));

    float3 texelsDx = mul(float4(positionDx, 0.0), shadowCascade.viewProj).xyz * scale;
    float3 texelsDy = mul(float4(positionDy, 0.0), shadowCascade.viewProj).xyz * scale;
    float3 planeNormal = cross(texelsDx, texelsDy);

    float lateral = length(planeNormal.xy);
    float2 slope = -planeNormal.xy / max(abs(planeNormal.z), lateral / MaxReceiverSlope + 1e-20) * sign(planeNormal.z);

    return slope / scale.z;
}

// The fraction of the directional light that reaches a point, from the smallest cascade that holds it, which is ShadowCascadeCount when none does.
// positionDx and positionDy are the screen derivatives of the world position.
float SunShadow(float3 worldPosition, float3 positionDx, float3 positionDy, float3 normal, float normalDotLight, out uint cascadeUsed) {
    uint mapWidth;
    uint cascadeSize;
    shadowMap.GetDimensions(mapWidth, cascadeSize);

    // A lookup stays three texels inside its cascade's tile, so the filter never reads the tile next to it:
    float inside = 1.0 - 6.0 / cascadeSize;

    for (uint cascade = 0; cascade < ShadowCascadeCount; cascade++) {
        ShadowCascade shadowCascade = lighting.shadowCascades[cascade];
        float texelSize = shadowCascade.parameters.x;

        // Moving the lookup off the surface along its normal keeps a lit surface from shadowing itself, and a surface at a grazing angle to the light needs it most:
        float3 lookupPosition = worldPosition + normal * texelSize * (0.5 + 1.5 * (1.0 - normalDotLight));

        // An orthographic projection, so w is 1, and Vulkan's framebuffer y runs down like the texture's v:
        float4 clip = mul(float4(lookupPosition, 1.0), shadowCascade.viewProj);

        if (all(abs(clip.xy) < inside) && clip.z <= 1.0) {
            cascadeUsed = cascade;
            float2 depthPerTexel = ReceiverDepthSlope(shadowCascade, positionDx, positionDy, texelSize, cascadeSize);
            return FilteredShadow(cascade, (clip.xy * 0.5 + 0.5) * cascadeSize, clip.z, depthPerTexel, float2(mapWidth, cascadeSize));
        }
    }

    cascadeUsed = ShadowCascadeCount;
    return 1.0;
}

// The fraction of the sky a point sees, from the eight cells around a lookup half a cell out along the normal, weighted as linear filtering weights them.
// A cell's weight fades out as its centre goes behind the surface's tangent plane, so a dark cell under a thin wall does not darken the surface on top of it.
// A cell with no air has no weight, so the rock in front of a concave surface does not darken it.
// A texel of 0 is a cell with no air, and a cell with air runs from 1 for no sky to 255 for the whole sky, as LightEngine.TakeVolumeChanges writes them in C#.
// A point outside the volume sees the whole sky. The eight cells stay inside the volume, because its two opposite faces lie next to each other in the texture.
// Each cell is read through the sampler's repeat addressing. Load with an integer remainder wrap read wrong texels in the game, though the same arithmetic is right on the CPU.
float SkyVisibility(float3 worldPosition, float3 normal) {
    float3 surface = worldPosition / lighting.lightVolumePlacement.w;
    float3 cell = surface + normal * 0.5;

    uint3 size;
    lightVolume.GetDimensions(size.x, size.y, size.z);

    float3 low = lighting.lightVolumePlacement.xyz;
    float3 high = low + float3(size);

    if (any(cell < low) || any(cell >= high)) {
        return 1.0;
    }

    float3 lookup = clamp(cell, low + 0.5, high - 0.5);
    float3 first = min(floor(lookup - 0.5), high - 2.0);
    float3 fraction = lookup - 0.5 - first;

    float sum = 0.0;
    float total = 0.0;
    float linearSum = 0.0;

    [unroll]
    for (uint corner = 0; corner < 8; corner++) {
        float3 offset = float3(corner & 1, (corner >> 1) & 1, corner >> 2);
        float3 axisWeights = lerp(1.0 - fraction, fraction, offset);
        float linearWeight = axisWeights.x * axisWeights.y * axisWeights.z;

        float texel = lightVolume.SampleLevel(lightSampler, (first + offset + 0.5) / float3(size), 0.0);
        float visibility = saturate((texel * 255.0 - 1.0) / 254.0);

        // A cell is gone a quarter of a cell behind the surface and whole a quarter of a cell in front of it:
        float weight = texel > 0.0 ? linearWeight * saturate(dot(first + offset + 0.5 - surface, normal) * 2.0 + 0.5) : 0.0;

        sum += weight * visibility;
        total += weight;
        linearSum += linearWeight * visibility;
    }

    // When no cell with air is in front of the surface, or its weight is 0 on a cell boundary, the plain blend stands in:
    return total > 0.0 ? sum / total : linearSum;
}

// The sky's radiance towards a unit direction: the horizon colour blending into the zenith colour, and the horizon colour below the horizon, where only the space past the loaded terrain shows it:
float3 SkyRadiance(float3 direction) {
    return lerp(lighting.skyHorizon.rgb, lighting.skyZenith.rgb, sqrt(saturate(direction.y)));
}

// The sun's disc, 0.53 degrees wide, and the glow around it, towards a unit direction. Both are zero while the sun is down, because its radiance is.
float3 SunRadiance(float3 direction) {
    float cosAngle = dot(direction, lighting.lightDirection.xyz);

    // The disc's edge is 0.265 degrees from its centre, where the cosine is 1 - 1.07e-5:
    float disc = smoothstep(1.0 - 1.3e-5, 1.0 - 0.9e-5, cosAngle);
    float glow = 0.5 * pow(saturate(cosAngle), 2000.0) + 0.04 * pow(saturate(cosAngle), 16.0);

    return lighting.lightRadiance.rgb * (SunDiscBrightness * disc + glow);
}

[shader("fragment")]
float4 fsMain(VertexOutput input) : SV_TARGET {
    float3 normal = normalize(input.normal);

    // The air id is out of the palette, and a cell with no solid corner falls back to rock:
    uint material = input.material < MaterialCount ? input.material : 0;
    float3 albedo = MaterialAlbedo[material];

    // A derivative needs every pixel of the quad to reach it, so these are taken before the shadow's branch:
    float3 positionDx = ddx(input.worldPosition);
    float3 positionDy = ddy(input.worldPosition);

    float normalDotLight = saturate(dot(normal, lighting.lightDirection.xyz));
    uint shadowCascade = ShadowCascadeCount;
    float shadow = normalDotLight > 0.0 ? SunShadow(input.worldPosition, positionDx, positionDy, normal, normalDotLight, shadowCascade) : 0.0;

    float skyVisibility = SkyVisibility(input.worldPosition, normal);

    // A point no sky light reaches gets no sun either, which keeps the sun out of caves deeper than the shadow cascades reach:
    float sunReach = saturate(skyVisibility * MaxLightLevel);

    float3 sky = lerp(lighting.skyHorizon.rgb, lighting.skyZenith.rgb, saturate(normal.y)) * skyVisibility;
    float3 direct = lighting.lightRadiance.rgb * normalDotLight * shadow * sunReach;
    float3 color = albedo * (sky + direct);

    uint debugView = uint(lighting.debugOptions.x);

    if (debugView == 1 && shadowCascade < ShadowCascadeCount) {
        color *= ShadowCascadeTints[shadowCascade];
    }
    else if (debugView == 2 && normalDotLight > 0.0) {
        // A texel stretches by one over the cosine of the angle between the normal and the light, 2 at 0.5, 4 at 0.25 and 11 at 0.09:
        uint band = normalDotLight >= 0.5 ? 0 : normalDotLight >= 0.25 ? 1 : normalDotLight >= 0.09 ? 2 : 3;
        color *= LightAngleTints[band];
    }
    else if (debugView == 3) {
        color = float3(skyVisibility, skyVisibility, skyVisibility);
    }
    else if (debugView == 4) {
        color = float3(shadow, shadow, shadow);
    }
    else if (debugView == 5) {
        // The triangle's own normal, turned to the side of the smooth one:
        float3 faceNormal = normalize(cross(positionDx, positionDy));
        faceNormal = dot(faceNormal, normal) < 0.0 ? -faceNormal : faceNormal;
        float faceDotLight = dot(faceNormal, lighting.lightDirection.xyz);

        // The sun's shadow tinted by the triangle's angle to the light, in the bands of view 2, and purple where the triangle faces away from the sun:
        uint band = faceDotLight >= 0.5 ? 0 : faceDotLight >= 0.25 ? 1 : faceDotLight >= 0.09 ? 2 : 3;
        float3 tint = faceDotLight > 0.0 ? LightAngleTints[band] : float3(0.6, 0.35, 1.0);
        color = lerp(0.25, 1.0, shadow) * tint;
    }

    return float4(color, 1.0);
}

struct SkyOutput {
    float4 position : SV_Position;
    float2 clip : CLIP_POSITION;
};

// One triangle that covers the screen at the far plane, so a depth test of LessOrEqual draws the sky only where nothing else did:
[shader("vertex")]
SkyOutput vsSky(uint vertexIndex : SV_VertexID) {
    float2 clip = float2((vertexIndex << 1) & 2, vertexIndex & 2) * 2.0 - 1.0;

    SkyOutput output;
    output.position = float4(clip, 1.0, 1.0);
    output.clip = clip;
    return output;
}

[shader("fragment")]
float4 fsSky(SkyOutput input) : SV_TARGET {
    float4 farPoint = mul(float4(input.clip, 1.0, 1.0), lighting.inverseViewProj);
    float3 direction = normalize(farPoint.xyz / farPoint.w - lighting.cameraPosition.xyz);

    return float4(SkyRadiance(direction) + SunRadiance(direction), 1.0);
}
