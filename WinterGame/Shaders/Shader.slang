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
    float4 fogParameters;
    float4 debugOptions;
};

[[vk::binding(1, 0)]] ConstantBuffer<FrameLighting> lighting;
[[vk::binding(2, 0)]] Texture2D shadowMap;
[[vk::binding(3, 0)]] SamplerComparisonState shadowSampler;

// The sky visibility of each light cell, at the cell's brick coordinates modulo the texture's size:
[[vk::binding(4, 0)]] Texture3D<float> lightVolume;

// Nearest filtering with repeat addressing, so a read at a cell's centre returns that cell's texel wherever the cell lies:
[[vk::binding(5, 0)]] SamplerState lightSampler;

// The fraction of the sky each pixel's surface sees past the geometry within AmbientOcclusion.Radius, read at the pixel:
[[vk::binding(6, 0)]] Texture2D<float> ambientOcclusion;

// The terrain's albedo in sRGB with its height in alpha, one layer per SurfaceMaterial id, as TerrainMaterials uploads it:
[[vk::binding(7, 0)]] Texture2DArray materialAlbedo;

// The terrain's normal maps in the layers of materialAlbedo, with x along the texture's u, y along its v and z out of the surface, each component c stored as the byte 128 + 127 c, and in alpha the fraction of the sky the relief leaves a point:
[[vk::binding(8, 0)]] Texture2DArray materialNormal;

// Trilinear and anisotropic with repeat addressing:
[[vk::binding(9, 0)]] SamplerState materialSampler;

// LightEngine.MaxLevel in C#:
static const float MaxLightLevel = 8.0;

// How much brighter the sun's disc is than the light it casts on a surface facing it. The real ratio is about 15000, and any value that saturates through the tonemap looks the same:
static const float SunDiscBrightness = 50.0;

// SurfaceMaterial.Count in C#, the layers of the material arrays:
static const uint MaterialCount = 8;

// MaterialMaps.TileMetres in C#, the extent one tile of the material maps covers:
static const float TileMetres = 4.0;

// How far the projection axes' weights outweigh the layer's height in their blend. A height runs from 0 to 1, so an axis shows only where its weight is within (1 + HeightBlendRange) / HeightBlendScale of the largest:
static const float HeightBlendScale = 3.0;

// How far below the highest axis in the blend another axis still shows, which softens the edges where one projection gives way to another:
static const float HeightBlendRange = 0.2;

// Each projection's maps are shifted by their own offset in tiles, so the features of two projections that share an axis do not line up where they blend:
static const float2 ProjectionOffsetX = float2(0.0, 0.0);
static const float2 ProjectionOffsetY = float2(0.5, 0.25);
static const float2 ProjectionOffsetZ = float2(0.25, 0.5);

// A surface facing the negative side of an axis shifts that axis's projection further, so the opposite walls of a narrow cut do not show the same features mirrored. The shift changes only where the axis has no weight:
static const float2 OppositeSideShift = float2(0.5, 0.375);

// The material maps have this many variants, each the tile shifted by its own amount, and a slow noise over the world picks one per point, so the tile does not repeat every TileMetres:
static const uint VariantCount = 8;

// The sizes in metres of the two octaves of that noise. A variant holds for a few metres:
static const float VariantNoiseMetres = 20.0;
static const float VariantDetailMetres = 7.0;

// Where two variants meet, the one whose maps are higher shows, as between the projection axes. A variant can outgrow the other only within (1 + VariantBlendRange) / (2 * VariantBlendScale) of a band's edge, measured in bands:
static const float VariantBlendScale = 10.0;
static const float VariantBlendRange = 0.2;

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

    // The position in tiles of the material maps from a tile corner near the section, as TilePosition gives it:
    float3 tilePosition : TILE_POSITION;

    nointerpolation uint material : MATERIAL;
};

// A vertex's world position from its record, the indirect command's firstInstance, which is the record's own index. SV_InstanceID would be the instance within the draw, which is always 0 here because every command has one instance.
// Positions are chunk-local voxel units, so the origin stays exact and the scale is applied once.
// The depth prepass and the lit pass both go through this and ClipPosition, and precise keeps the compiler from fusing the arithmetic differently in each, so the lit pass meets exactly the depth the prepass wrote:
float3 WorldPosition(float3 position, uint recordIndex) {
    DrawRecord draw = draws[recordIndex];
    precise float3 worldPosition = (draw.origin + position) * draw.voxelSize;
    return worldPosition;
}

float4 ClipPosition(float3 worldPosition) {
    precise float4 clip = mul(float4(worldPosition, 1.0), lighting.viewProj);
    return clip;
}

// A vertex's position in tiles of the material maps, counted from the tile corner at or below the record's origin, so it stays small and exact anywhere in the world.
// The origin is a whole number of voxels and a tile a power of two of them, so its remainder by a tile is exact. Two sections' positions for one point differ by whole tiles, which the sampler's repeat hides:
float3 TilePosition(float3 position, uint recordIndex) {
    DrawRecord draw = draws[recordIndex];
    float tileVoxels = TileMetres / draw.voxelSize;
    float3 tileOrigin = draw.origin - tileVoxels * floor(draw.origin / tileVoxels);

    return (tileOrigin + position) / tileVoxels;
}

[shader("vertex")]
VertexOutput vsMain(VertexInput input, uint recordIndex : SV_StartInstanceLocation) {
    VertexOutput output;

    float3 worldPosition = WorldPosition(input.position, recordIndex);

    output.position = ClipPosition(worldPosition);
    output.worldPosition = worldPosition;
    output.normal = input.normal.xyz;
    output.tilePosition = TilePosition(input.position, recordIndex);
    output.material = input.material;
    return output;
}

struct DepthInput {
    float3 position;
    float3 normal;
};

struct DepthOutput {
    float4 position : SV_Position;
    float3 normal : NORMAL;
};

// The depth prepass, which the ambient occlusion reads before the lit pass draws:
[shader("vertex")]
DepthOutput vsDepth(DepthInput input, uint recordIndex : SV_StartInstanceLocation) {
    DepthOutput output;

    output.position = ClipPosition(WorldPosition(input.position, recordIndex));
    output.normal = input.normal;
    return output;
}

// The interpolated world normal, as WorldLayer.NormalFormat stores it, so the ambient occlusion sees the shading's smooth surface rather than the triangles of the depth:
[shader("fragment")]
float4 fsDepth(DepthOutput input) : SV_TARGET {
    return float4(normalize(input.normal) * 0.5 + 0.5, 0.0);
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

// The glow the air scatters around the sun, towards a unit direction. It is zero while the sun is down, because its radiance is.
float3 SunGlow(float3 direction) {
    float cosAngle = saturate(dot(direction, lighting.lightDirection.xyz));
    return lighting.lightRadiance.rgb * (0.5 * pow(cosAngle, 2000.0) + 0.04 * pow(cosAngle, 16.0));
}

// The sun's disc, 0.53 degrees wide, and its glow, towards a unit direction:
float3 SunRadiance(float3 direction) {
    // The disc's edge is 0.265 degrees from its centre, where the cosine is 1 - 1.07e-5:
    float disc = smoothstep(1.0 - 1.3e-5, 1.0 - 0.9e-5, dot(direction, lighting.lightDirection.xyz));
    return lighting.lightRadiance.rgb * SunDiscBrightness * disc + SunGlow(direction);
}

// The fraction of a surface's colour the air replaces: a thin haze over the whole distance, and a fade over the last stretch of the meshed terrain that reaches the whole sky where the terrain ends, so the world does not stop in a cut.
// The fade uses the horizontal distance, because the loaded terrain is a cylinder around the player.
float FogAmount(float distance, float horizontalDistance) {
    float haze = 1.0 - exp(-distance * lighting.fogParameters.x);
    float edge = smoothstep(lighting.fogParameters.y, lighting.fogParameters.z, horizontalDistance);
    return max(haze, edge);
}

// The fog takes the colour of the sky behind it, with the sun's glow, so far terrain melts into the sky:
float3 ApplyFog(float3 color, float3 worldPosition) {
    float3 toPoint = worldPosition - lighting.cameraPosition.xyz;
    float distance = length(toPoint);
    float3 direction = toPoint / max(distance, 1e-4);

    return lerp(color, SkyRadiance(direction) + SunGlow(direction), FogAmount(distance, length(toPoint.xz)));
}

// A random value in [0, 1) for a lattice cell, from MurmurHash3's finalizer over the combined coordinates:
float CellValue(int3 cell) {
    uint hash = uint(cell.x) * 0x85EBCA77u ^ uint(cell.y) * 0xC2B2AE3Du ^ uint(cell.z) * 0x27D4EB2Fu;
    hash ^= hash >> 16;
    hash *= 0x7FEB352Du;
    hash ^= hash >> 15;
    hash *= 0x846CA68Bu;
    hash ^= hash >> 16;
    return float(hash >> 8) * (1.0 / 16777216.0);
}

// Value noise in [0, 1) over a lattice of unit cells, smoothed by the quintic fade:
float ValueNoise(float3 position) {
    float3 floored = floor(position);
    float3 fraction = position - floored;
    float3 fade = fraction * fraction * fraction * (fraction * (fraction * 6.0 - 15.0) + 10.0);
    int3 cell = int3(floored);

    float4 low = float4(CellValue(cell), CellValue(cell + int3(1, 0, 0)), CellValue(cell + int3(0, 1, 0)), CellValue(cell + int3(1, 1, 0)));
    float4 high = float4(CellValue(cell + int3(0, 0, 1)), CellValue(cell + int3(1, 0, 1)), CellValue(cell + int3(0, 1, 1)), CellValue(cell + int3(1, 1, 1)));
    float4 corners = lerp(low, high, fade.z);
    float2 rows = lerp(corners.xz, corners.yw, fade.x);
    return lerp(rows.x, rows.y, fade.y);
}

// Which variant of the material maps a world position reads, as a position in [0, VariantCount) along the variants, so a variant holds between two whole numbers:
float VariantPosition(float3 worldPosition) {
    float noise = 0.7 * ValueNoise(worldPosition / VariantNoiseMetres) + 0.3 * ValueNoise(worldPosition / VariantDetailMetres + 17.0);
    return noise * VariantCount;
}

struct SurfaceDetail {
    float3 albedo;
    float3 normal;

    // The fraction of the sky the texture's relief leaves the point:
    float occlusion;

    // The maps' height at the point, in [0, 1]:
    float height;
};

// One projection's normal map read at uv, its x and y along the projection's u and v, and in w the fraction of the sky the relief leaves:
float4 ProjectedNormal(float layer, float2 uv, float2 uvDx, float2 uvDy) {
    float4 texel = materialNormal.SampleGrad(materialSampler, float3(uv, layer), uvDx, uvDy);
    return float4(texel.xyz * (255.0 / 127.0) - 128.0 / 127.0, texel.w);
}

// A material layer's albedo and detailed normal at a point, from three projections along the world axes, blended by the unit geometric normal and the layer's height.
// The projection along x maps z and y to the texture's u and v, the one along y maps x and z, and the one along z maps x and y. Golus's whiteout blend turns each projection's normal into world space.
// A variant shifts the projections from above in both directions and the others only along u, so a structure along the rows stays level on a wall.
// An axis that cannot reach the height blend's top anywhere is not read. Every read takes its gradients from the caller, because an implicit derivative in divergent control flow is undefined:
SurfaceDetail MaterialDetail(uint material, uint variant, float3 tilePosition, float3 positionDx, float3 positionDy, float3 normal) {
    float3 squared = normal * normal;
    float3 axisWeights = squared * squared;
    axisWeights /= axisWeights.x + axisWeights.y + axisWeights.z;

    float layer = material;

    // The fractions of the golden ratio spread the variants' shifts evenly over the tile:
    float2 variantShift = frac(float(variant) * float2(0.618034, 0.381966));

    float2 uvX = tilePosition.zy + ProjectionOffsetX + (normal.x < 0.0 ? OppositeSideShift : 0.0) + float2(variantShift.x, 0.0);
    float2 uvY = tilePosition.xz + ProjectionOffsetY + (normal.y < 0.0 ? OppositeSideShift : 0.0) + variantShift;
    float2 uvZ = tilePosition.xy + ProjectionOffsetZ + (normal.z < 0.0 ? OppositeSideShift : 0.0) + float2(variantShift.y, 0.0);

    // An axis at its highest point has 1 + its weighted term, and the strongest axis has at least its own weighted term:
    float largest = max(axisWeights.x, max(axisWeights.y, axisWeights.z));
    bool3 reaches = axisWeights * HeightBlendScale + 1.0 > largest * HeightBlendScale - HeightBlendRange;

    float4 albedoX = reaches.x ? materialAlbedo.SampleGrad(materialSampler, float3(uvX, layer), positionDx.zy, positionDy.zy) : 0.0;
    float4 albedoY = reaches.y ? materialAlbedo.SampleGrad(materialSampler, float3(uvY, layer), positionDx.xz, positionDy.xz) : 0.0;
    float4 albedoZ = reaches.z ? materialAlbedo.SampleGrad(materialSampler, float3(uvZ, layer), positionDx.xy, positionDy.xy) : 0.0;

    // Each axis's height lifted by its weight, and the axes within HeightBlendRange of the highest share the point:
    float3 heights = float3(albedoX.a, albedoY.a, albedoZ.a) + axisWeights * HeightBlendScale;
    float3 lift = max(heights - (max(heights.x, max(heights.y, heights.z)) - HeightBlendRange), 0.0);
    float3 weights = lift / (lift.x + lift.y + lift.z);

    SurfaceDetail detail;
    detail.albedo = albedoX.rgb * weights.x + albedoY.rgb * weights.y + albedoZ.rgb * weights.z;
    detail.height = albedoX.a * weights.x + albedoY.a * weights.y + albedoZ.a * weights.z;

    float3 blended = 0.0;
    detail.occlusion = 0.0;

    if (weights.x > 0.0) {
        float4 projected = ProjectedNormal(layer, uvX, positionDx.zy, positionDy.zy);
        blended += weights.x * float3(abs(projected.z) * normal.x, projected.y + normal.y, projected.x + normal.z);
        detail.occlusion += weights.x * projected.w;
    }

    if (weights.y > 0.0) {
        float4 projected = ProjectedNormal(layer, uvY, positionDx.xz, positionDy.xz);
        blended += weights.y * float3(projected.x + normal.x, abs(projected.z) * normal.y, projected.y + normal.z);
        detail.occlusion += weights.y * projected.w;
    }

    if (weights.z > 0.0) {
        float4 projected = ProjectedNormal(layer, uvZ, positionDx.xy, positionDy.xy);
        blended += weights.z * float3(projected.x + normal.x, projected.y + normal.y, abs(projected.z) * normal.z);
        detail.occlusion += weights.z * projected.w;
    }

    detail.normal = normalize(blended);
    return detail;
}

// The material's detail where the world's noise picks a variant, two variants blended by their heights near the edge between them.
// The variant the point lies in is favoured by its distance from the edge, so the other is read only where it can still show:
SurfaceDetail VariedMaterialDetail(uint material, float3 worldPosition, float3 tilePosition, float3 positionDx, float3 positionDy, float3 normal) {
    float variantPosition = VariantPosition(worldPosition);
    float edge = round(variantPosition);
    float fromEdge = variantPosition - edge;

    // Either side of an edge at a whole number e lie the variants e - 1 and e:
    uint below = uint(edge + VariantCount - 1.0) % VariantCount;
    uint above = uint(edge) % VariantCount;

    SurfaceDetail detail = MaterialDetail(material, fromEdge < 0.0 ? below : above, tilePosition, positionDx, positionDy, normal);

    float bias = abs(fromEdge) * VariantBlendScale;

    if (2.0 * bias < 1.0 + VariantBlendRange) {
        SurfaceDetail other = MaterialDetail(material, fromEdge < 0.0 ? above : below, tilePosition, positionDx, positionDy, normal);

        float2 heights = float2(detail.height + bias, other.height - bias);
        float2 lift = max(heights - (max(heights.x, heights.y) - VariantBlendRange), 0.0);
        float otherWeight = lift.y / (lift.x + lift.y);

        detail.albedo = lerp(detail.albedo, other.albedo, otherWeight);
        detail.normal = normalize(lerp(detail.normal, other.normal, otherWeight));
        detail.occlusion = lerp(detail.occlusion, other.occlusion, otherWeight);
        detail.height = lerp(detail.height, other.height, otherWeight);
    }

    return detail;
}

[shader("fragment")]
float4 fsMain(VertexOutput input) : SV_TARGET {
    float3 normal = normalize(input.normal);

    // The air id is out of the palette, and a cell with no solid corner falls back to rock:
    uint material = input.material < MaterialCount ? input.material : 0;

    float3 tileDx = ddx(input.tilePosition);
    float3 tileDy = ddy(input.tilePosition);

    // The sun and the sky shade the detailed normal, while the shadow and the sky visibility keep the geometric one, which their filters are built around.
    // The World panel can turn off the variants, so every point reads the first:
    SurfaceDetail detail;

    if (lighting.debugOptions.w != 0.0) {
        detail = VariedMaterialDetail(material, input.worldPosition, input.tilePosition, tileDx, tileDy, normal);
    }
    else {
        detail = MaterialDetail(material, 0, input.tilePosition, tileDx, tileDy, normal);
    }

    float3 albedo = detail.albedo;

    // The World panel can turn off the normal map's normals and its occlusion:
    if (lighting.debugOptions.y == 0.0) {
        detail.normal = normal;
    }

    if (lighting.debugOptions.z == 0.0) {
        detail.occlusion = 1.0;
    }

    // A derivative needs every pixel of the quad to reach it, so these are taken before the shadow's branch:
    float3 positionDx = ddx(input.worldPosition);
    float3 positionDy = ddy(input.worldPosition);

    float normalDotLight = saturate(dot(normal, lighting.lightDirection.xyz));
    uint shadowCascade = ShadowCascadeCount;
    float shadow = normalDotLight > 0.0 ? SunShadow(input.worldPosition, positionDx, positionDy, normal, normalDotLight, shadowCascade) : 0.0;

    float skyVisibility = SkyVisibility(input.worldPosition, normal);

    // A point no sky light reaches gets no sun either, which keeps the sun out of caves deeper than the shadow cascades reach:
    float sunReach = saturate(skyVisibility * MaxLightLevel);

    // Light bounces between bright surfaces, so a crease in snow darkens far less than the occlusion alone says. This is Jimenez's fit of that for a surface's albedo, from the GTAO paper:
    // The ambient occlusion sees the mesh and the texture's own occlusion its relief, so both take their share of the sky:
    float occlusion = ambientOcclusion.Load(int3(int2(input.position.xy), 0)) * detail.occlusion;
    float3 bounceA = 2.0404 * albedo - 0.3324;
    float3 bounceB = -4.7951 * albedo + 0.6417;
    float3 bounceC = 2.7552 * albedo + 0.6903;
    float3 skyOcclusion = max(occlusion, ((occlusion * bounceA + bounceB) * occlusion + bounceC) * occlusion);

    float3 sky = lerp(lighting.skyHorizon.rgb, lighting.skyZenith.rgb, saturate(detail.normal.y)) * skyVisibility * skyOcclusion;
    float3 direct = lighting.lightRadiance.rgb * saturate(dot(detail.normal, lighting.lightDirection.xyz)) * shadow * sunReach;
    float3 color = albedo * (sky + direct);

    uint debugView = uint(lighting.debugOptions.x);

    // A debug view shows its values unfogged at every distance:
    if (debugView == 0) {
        color = ApplyFog(color, input.worldPosition);
    }
    else if (debugView == 1 && shadowCascade < ShadowCascadeCount) {
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
    else if (debugView == 6) {
        color = float3(occlusion, occlusion, occlusion);
    }
    else if (debugView == 7) {
        color = albedo;
    }
    else if (debugView == 8) {
        color = detail.normal * 0.5 + 0.5;
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
