struct DrawRecord {
    float3 origin;
    float voxelSize;
};

[[vk::binding(0, 0)]] StructuredBuffer<DrawRecord> draws;

// SunShadow.CascadeCount in C#:
static const uint ShadowCascadeCount = 3;

// ShadowCascade in C#, the cascade's texel size in metres in parameters.x:
struct ShadowCascade {
    row_major float4x4 viewProj;
    float4 parameters;
};

// FrameLighting in C#, whose members are all 16 bytes or a multiple of it, so this std140 layout matches it:
struct FrameLighting {
    row_major float4x4 viewProj;
    float4 lightDirection;
    float4 lightRadiance;
    float4 skyZenith;
    float4 skyHorizon;
    ShadowCascade shadowCascades[ShadowCascadeCount];
};

[[vk::binding(1, 0)]] ConstantBuffer<FrameLighting> lighting;
[[vk::binding(2, 0)]] Texture2D shadowMap;
[[vk::binding(3, 0)]] SamplerComparisonState shadowSampler;

static const uint MaterialCount = 8;

// Linear albedo, because the swapchain is an sRGB format and the write converts it:
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

// One comparison at a position in texels inside a cascade's tile, filtered over its four nearest texels by the sampler:
float ShadowTap(uint cascade, float2 tileTexel, float depth, float2 mapSize) {
    float2 uv = float2(cascade * mapSize.y + tileTexel.x, tileTexel.y) / mapSize;
    return shadowMap.SampleCmpLevelZero(shadowSampler, uv, depth);
}

// A cascade's shadow at a position in texels inside its tile, filtered over 5x5 texels.
// This is Castano's optimized PCF, as in MJP's shadow sample. Nine filtered comparisons stand in for the 25 texels, and their weights and offsets follow the lookup's position inside its texel, so the result moves smoothly instead of stepping a texel at a time.
float FilteredShadow(uint cascade, float2 tileTexel, float depth, float2 mapSize) {
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
    sum += uw0 * vw0 * ShadowTap(cascade, baseTexel + float2(u0, v0), depth, mapSize);
    sum += uw1 * vw0 * ShadowTap(cascade, baseTexel + float2(u1, v0), depth, mapSize);
    sum += uw2 * vw0 * ShadowTap(cascade, baseTexel + float2(u2, v0), depth, mapSize);

    sum += uw0 * vw1 * ShadowTap(cascade, baseTexel + float2(u0, v1), depth, mapSize);
    sum += uw1 * vw1 * ShadowTap(cascade, baseTexel + float2(u1, v1), depth, mapSize);
    sum += uw2 * vw1 * ShadowTap(cascade, baseTexel + float2(u2, v1), depth, mapSize);

    sum += uw0 * vw2 * ShadowTap(cascade, baseTexel + float2(u0, v2), depth, mapSize);
    sum += uw1 * vw2 * ShadowTap(cascade, baseTexel + float2(u1, v2), depth, mapSize);
    sum += uw2 * vw2 * ShadowTap(cascade, baseTexel + float2(u2, v2), depth, mapSize);

    // The weights on each axis sum to 12, so all nine sum to 144:
    return sum / 144.0;
}

// The fraction of the directional light that reaches a point, from the smallest cascade that holds it.
float SunShadow(float3 worldPosition, float3 normal, float normalDotLight) {
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
            return FilteredShadow(cascade, (clip.xy * 0.5 + 0.5) * cascadeSize, clip.z, float2(mapWidth, cascadeSize));
        }
    }

    return 1.0;
}

[shader("fragment")]
float4 fsMain(VertexOutput input) : SV_TARGET {
    float3 normal = normalize(input.normal);

    // The air id is out of the palette, and a cell with no solid corner falls back to rock:
    uint material = input.material < MaterialCount ? input.material : 0;
    float3 albedo = MaterialAlbedo[material];

    float normalDotLight = saturate(dot(normal, lighting.lightDirection.xyz));
    float shadow = normalDotLight > 0.0 ? SunShadow(input.worldPosition, normal, normalDotLight) : 0.0;

    float3 sky = lerp(lighting.skyHorizon.rgb, lighting.skyZenith.rgb, saturate(normal.y));
    float3 direct = lighting.lightRadiance.rgb * normalDotLight * shadow;

    return float4(albedo * (sky + direct), 1.0);
}
