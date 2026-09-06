struct TextQuad {
    float3 position;
    float weight;
    float3 right;
    float smoothing;
    float3 up;
    float _pad0;
    float2 size;
    float2 uvMin;
    float2 uvMax;
    float4 color;
};

cbuffer Camera : register(b0) {
    row_major float4x4 viewProj;
};

[[vk::binding(1, 0)]] StructuredBuffer<TextQuad> quads;

struct VertexOutputNormal {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

struct VertexOutputSdf {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
    float weight : WEIGHT;
    float smoothing : SMOOTHING;
};

[shader("vertex")]
VertexOutputNormal vsNormal(uint vertexId : SV_VertexID) {
    VertexOutputNormal output;

    uint qi = vertexId / 6;
    uint c = vertexId % 6;

    float2 corners[6] = {
        float2(0.0, 1.0),
        float2(1.0, 1.0),
        float2(0.0, 0.0),
        float2(0.0, 0.0),
        float2(1.0, 1.0),
        float2(1.0, 0.0)
    };

    float2 corner = corners[c];
    TextQuad q = quads[qi];

    float3 worldPos = q.position
        + q.right * corner.x * q.size.x
        + q.up * corner.y * q.size.y;

    output.position = mul(float4(worldPos, 1.0), viewProj);
    output.uv = lerp(q.uvMin, q.uvMax, corner);
    output.color = q.color;

    return output;
}

[shader("vertex")]
VertexOutputSdf vsSdf(uint vertexId : SV_VertexID) {
    VertexOutputSdf output;

    uint qi = vertexId / 6;
    uint c = vertexId % 6;

    float2 corners[6] = {
        float2(0.0, 1.0),
        float2(1.0, 1.0),
        float2(0.0, 0.0),
        float2(0.0, 0.0),
        float2(1.0, 1.0),
        float2(1.0, 0.0)
    };

    float2 corner = corners[c];
    TextQuad q = quads[qi];

    float3 worldPos = q.position
        + q.right * corner.x * q.size.x
        + q.up * corner.y * q.size.y;

    output.position = mul(float4(worldPos, 1.0), viewProj);
    output.uv = lerp(q.uvMin, q.uvMax, corner);
    output.color = q.color;
    output.weight = q.weight;
    output.smoothing = q.smoothing;

    return output;
}

[[vk::binding(2, 0)]] SamplerState atlasSampler;

[[vk::binding(3, 0)]] Texture2D<float> atlas;

[shader("fragment")]
float4 fsNormal(VertexOutputNormal input) : SV_TARGET {
    float alpha = atlas.Sample(atlasSampler, input.uv).r;
    return float4(input.color.rgb, input.color.a * alpha);
}

[shader("fragment")]
float4 fsSdf(VertexOutputSdf input) : SV_TARGET {
    float dist = atlas.Sample(atlasSampler, input.uv).r;
    float alpha = smoothstep(input.weight - input.smoothing, input.weight + input.smoothing, dist);
    
    if (alpha < 0.0001) {
        discard;
    }
    
    return float4(input.color.rgb, input.color.a * alpha);
}