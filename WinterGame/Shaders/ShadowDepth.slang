struct DrawRecord {
    float3 origin;
    float voxelSize;
};

[[vk::binding(0, 0)]] StructuredBuffer<DrawRecord> draws;

struct PushConstants {
    row_major float4x4 viewProj;
};

[[vk::push_constant]] PushConstants pc;

struct VertexInput {
    float3 position;
};

// Depth only, so there is no fragment stage. The record index is the indirect command's firstInstance, as in Shader.slang:
[shader("vertex")]
float4 vsMain(VertexInput input, uint recordIndex : SV_StartInstanceLocation) : SV_Position {
    DrawRecord draw = draws[recordIndex];
    float3 worldPosition = (draw.origin + input.position) * draw.voxelSize;

    return mul(float4(worldPosition, 1.0), pc.viewProj);
}
