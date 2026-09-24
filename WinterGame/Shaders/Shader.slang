struct DrawRecord {
    float3 origin;
    float voxelSize;
};

[[vk::binding(0, 0)]] StructuredBuffer<DrawRecord> draws;

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
    float3 normal : NORMAL;

    nointerpolation uint material : MATERIAL;
};

struct PushConstants {
    row_major float4x4 viewProj;
};

[[vk::push_constant]] PushConstants pc;

[shader("vertex")]
VertexOutput vsMain(VertexInput input, uint recordIndex : SV_StartInstanceLocation) {
    VertexOutput output;

    // The indirect command's firstInstance, which is the record's own index. SV_InstanceID would be the instance within the draw, which is always 0 here because every command has one instance.
    DrawRecord draw = draws[recordIndex];

    // Positions are chunk-local voxel units, so the origin stays exact and the scale is applied once:
    float3 worldPosition = (draw.origin + input.position.xyz) * draw.voxelSize;

    output.position = mul(float4(worldPosition, 1.0), pc.viewProj);
    output.normal = input.normal.xyz;
    output.material = input.material;
    return output;
}

[shader("fragment")]
float4 fsMain(VertexOutput input) : SV_TARGET {
    float3 normal = normalize(input.normal);

    // The air id is out of the palette, and a cell with no solid corner falls back to rock:
    uint material = input.material < MaterialCount ? input.material : 0;
    float3 albedo = MaterialAlbedo[material];

    static const float3 sunDirection = normalize(float3(0.35, 0.9, -0.35));
    static const float3 sunColor = float3(1.0, 0.96, 0.90);
    static const float3 zenithColor = float3(0.22, 0.30, 0.46);
    static const float3 horizonColor = float3(0.38, 0.44, 0.54);

    float3 sky = lerp(horizonColor, zenithColor, saturate(normal.y));
    float sunlight = saturate(dot(normal, sunDirection)) * 0.7;

    return float4(albedo * (sky + sunColor * sunlight), 1.0);
}
