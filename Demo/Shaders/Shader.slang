struct VertexInput {
    float4 position;
    float4 normal;
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
VertexOutput vsMain(VertexInput input) {
    VertexOutput output;
    output.position = mul(float4(input.position.xyz, 1.0), pc.viewProj);
    output.normal = input.normal.xyz;
    return output;
}

[shader("fragment")]
float4 fsMain(VertexOutput input) : SV_TARGET {
    return float4(input.normal, 1.0);
}