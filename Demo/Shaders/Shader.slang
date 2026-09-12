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
VertexOutput vsMain(VertexInput input) {
    VertexOutput output;
    output.position = mul(float4(input.position.xyz, 1.0), pc.viewProj);
    output.normal = input.normal.xyz;
    return output;
}

[shader("fragment")]
float4 fsMain(VertexOutput input) : SV_TARGET {
     float3 lightDir = normalize(float3(0.35, 0.9, -0.35));
     float lambert = saturate(dot(normalize(input.normal), lightDir));
     return float4(float3(0.15) + float3(0.85) * lambert, 1.0);
}