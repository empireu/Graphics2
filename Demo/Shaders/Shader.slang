struct VertexInput {
    float2 position : POSITION;
    uint vertexId : SV_VertexID;
};

struct VertexOutput {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

cbuffer Camera : register(b0) {
    row_major float4x4 viewProj;
};

struct PushConstants {
    float rotation;
    float time;
    uint textureIndex2;
    uint textureIndex3;
};

[[vk::push_constant]] PushConstants pc;

StructuredBuffer<float2> uvs;
Texture2D<float4> tex;
SamplerState samp;

[[vk::binding(0, 1)]] Texture2D<float4> textures[1024];

[shader("vertex")]
VertexOutput vsMain(VertexInput input) {
    VertexOutput output;
    float c = cos(pc.rotation);
    float s = sin(pc.rotation);
    float2 rotated = float2(
        input.position.x * c - input.position.y * s,
        input.position.x * s + input.position.y * c
    );
    output.position = mul(float4(rotated, 0.0, 1.0), viewProj);
    output.uv = uvs[input.vertexId];
    return output;
}

[shader("fragment")]
float4 fsMain(VertexOutput input) : SV_TARGET {
    float4 c1 = tex.Sample(samp, input.uv);
    float4 c2 = textures[pc.textureIndex2].Sample(samp, input.uv);
    float4 c3 = textures[pc.textureIndex3].Sample(samp, input.uv);
    float4 blended = (c1 + c2 + c3) / 3.0;
    float pulse = 0.5 + 0.5 * sin(pc.time * 3.0);
    return float4(blended.rgb * pulse, 1.0);
}