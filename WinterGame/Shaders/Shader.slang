struct DrawRecord {
    float3 origin;
    float voxelSize;
};

[[vk::binding(0, 0)]] StructuredBuffer<DrawRecord> draws;

struct VertexInput {
    float3 position;
    float3 normal;
};

struct VertexOutput {
    float4 position : SV_Position;
    float3 normal : NORMAL;
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
    return output;
}

[shader("fragment")]
float4 fsMain(VertexOutput input) : SV_TARGET {
     float3 lightDir = normalize(float3(0.35, 0.9, -0.35));
     float lambert = saturate(dot(normalize(input.normal), lightDir));
     return float4(float3(0.15) + float3(0.85) * lambert, 1.0);
}
