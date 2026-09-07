cbuffer Camera : register(b0) {
    row_major float4x4 viewProj;
};

static const float2 corners[6] = {
    float2(0.0, 1.0),
    float2(1.0, 1.0),
    float2(0.0, 0.0),
    float2(0.0, 0.0),
    float2(1.0, 1.0),
    float2(1.0, 0.0)
};

struct SolidQuad3D {
    float4 position;
    float4 right;
    float4 up;
    float2 size;
    float2 _pad;
    float4 color;
};

[[vk::binding(1, 0)]] StructuredBuffer<SolidQuad3D> solidQuads;

struct Quad3DVSOut {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

[shader("vertex")]
Quad3DVSOut vsSolid3D(uint vertexId : SV_VertexID) {
    Quad3DVSOut o;
    uint qi = vertexId / 6;
    uint c = vertexId % 6;
    float2 corner = corners[c];
    SolidQuad3D q = solidQuads[qi];
    float2 centered = corner - 0.5;
    float3 offset = q.right.xyz * (centered.x * q.size.x) + q.up.xyz * (centered.y * q.size.y);
    float3 worldPos = q.position.xyz + offset;
    o.position = mul(float4(worldPos, 1.0), viewProj);
    o.uv = corner;
    o.color = q.color;
    return o;
}

[shader("fragment")]
float4 fsSolid3D(Quad3DVSOut i) : SV_TARGET {
    return i.color;
}

struct TexturedQuad3D {
    float4 position;
    float4 right;
    float4 up;
    float2 size;
    float2 uvMin;
    float2 uvMax;
    float2 _pad;
    float4 color;
};

[[vk::binding(2, 0)]] StructuredBuffer<TexturedQuad3D> texturedQuads;
[[vk::binding(3, 0)]] SamplerState quadSampler;
[[vk::binding(0, 1)]] Texture2D<float4> textures[1024];

struct TexturedQuad3DVSOut {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
    nointerpolation uint texIndex : TEXCOORD1;
};

[shader("vertex")]
TexturedQuad3DVSOut vsTextured3D(uint vertexId : SV_VertexID) {
    TexturedQuad3DVSOut o;
    uint qi = vertexId / 6;
    uint c = vertexId % 6;
    float2 corner = corners[c];
    TexturedQuad3D q = texturedQuads[qi];
    float2 centered = corner - 0.5;
    float3 offset = q.right.xyz * (centered.x * q.size.x) + q.up.xyz * (centered.y * q.size.y);
    float3 worldPos = q.position.xyz + offset;
    o.position = mul(float4(worldPos, 1.0), viewProj);
    o.uv = lerp(q.uvMin, q.uvMax, corner);
    o.color = q.color;
    o.texIndex = uint(q.position.w);
    return o;
}

[shader("fragment")]
float4 fsTextured3D(TexturedQuad3DVSOut i) : SV_TARGET {
    return textures[i.texIndex].Sample(quadSampler, i.uv) * i.color;
}
