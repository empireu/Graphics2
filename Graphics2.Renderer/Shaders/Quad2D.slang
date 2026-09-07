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

float2 rotate2d(float angle, float2 v) {
    float s = sin(angle), c = cos(angle);
    return float2(c * v.x - s * v.y, s * v.x + c * v.y);
}

struct QuadVSOut {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

struct SolidQuad2D {
    float4 position;
    float4 color;
    float2 size;
    float2 _pad;
};

[[vk::binding(1, 0)]] StructuredBuffer<SolidQuad2D> solidQuads;

[shader("vertex")]
QuadVSOut vsSolid2D(uint vertexId : SV_VertexID) {
    QuadVSOut o;
    uint qi = vertexId / 6;
    uint c = vertexId % 6;
    float2 corner = corners[c];
    SolidQuad2D q = solidQuads[qi];
    float2 center = q.position.xy + q.size * 0.5;
    float2 offset = rotate2d(q.position.z, corner * q.size - q.size * 0.5);
    float2 screenPos = center + offset;
    o.position = mul(float4(screenPos, 0.0, 1.0), viewProj);
    o.uv = corner;
    o.color = q.color;
    return o;
}

[shader("fragment")]
float4 fsSolid2D(QuadVSOut i) : SV_TARGET {
    return i.color;
}

struct TexturedQuad2D {
    float4 position;
    float4 color;
    float2 size;
    float2 uvMin;
    float2 uvMax;
    float2 _pad;
};

[[vk::binding(2, 0)]] StructuredBuffer<TexturedQuad2D> texturedQuads;
[[vk::binding(5, 0)]] SamplerState quadSampler;
[[vk::binding(0, 1)]] Texture2D<float4> textures[1024];

struct TexturedQuadVSOut {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
    nointerpolation uint texIndex : TEXCOORD1;
};

[shader("vertex")]
TexturedQuadVSOut vsTextured2D(uint vertexId : SV_VertexID) {
    TexturedQuadVSOut o;
    uint qi = vertexId / 6;
    uint c = vertexId % 6;
    float2 corner = corners[c];
    TexturedQuad2D q = texturedQuads[qi];
    float2 center = q.position.xy + q.size * 0.5;
    float2 offset = rotate2d(q.position.z, corner * q.size - q.size * 0.5);
    float2 screenPos = center + offset;
    o.position = mul(float4(screenPos, 0.0, 1.0), viewProj);
    o.uv = lerp(q.uvMin, q.uvMax, corner);
    o.color = q.color;
    o.texIndex = uint(q.position.w);
    return o;
}

[shader("fragment")]
float4 fsTextured2D(TexturedQuadVSOut i) : SV_TARGET {
    return textures[i.texIndex].Sample(quadSampler, i.uv) * i.color;
}

struct CircleQuad2D {
    float4 position;
    float4 color;
};

[[vk::binding(3, 0)]] StructuredBuffer<CircleQuad2D> circleQuads;

[shader("vertex")]
QuadVSOut vsCircle2D(uint vertexId : SV_VertexID) {
    QuadVSOut o;
    uint qi = vertexId / 6;
    uint c = vertexId % 6;
    float2 corner = corners[c];
    CircleQuad2D q = circleQuads[qi];
    float r = q.position.z;
    float2 size = float2(r * 2.0, r * 2.0);
    float2 screenPos = q.position.xy - r + corner * size;
    o.position = mul(float4(screenPos, 0.0, 1.0), viewProj);
    o.uv = corner;
    o.color = q.color;
    return o;
}

[shader("fragment")]
float4 fsCircle2D(QuadVSOut i) : SV_TARGET {
    float2 d = i.uv * 2.0 - 1.0;
    if (length(d) > 1.0) {
        discard;
    }
    return i.color;
}

struct GradientQuad2D {
    float4 position;
    float4 color0;
    float4 color1;
    float4 color2;
    float4 color3;
    float2 size;
    float2 _pad;
};

[[vk::binding(4, 0)]] StructuredBuffer<GradientQuad2D> gradientQuads;

struct GradientQuadVSOut {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    nointerpolation uint quadIndex : TEXCOORD1;
};

[shader("vertex")]
GradientQuadVSOut vsGradient2D(uint vertexId : SV_VertexID) {
    GradientQuadVSOut o;
    uint qi = vertexId / 6;
    uint c = vertexId % 6;
    float2 corner = corners[c];
    GradientQuad2D q = gradientQuads[qi];
    float2 center = q.position.xy + q.size * 0.5;
    float2 offset = rotate2d(q.position.z, corner * q.size - q.size * 0.5);
    float2 screenPos = center + offset;
    o.position = mul(float4(screenPos, 0.0, 1.0), viewProj);
    o.uv = corner;
    o.quadIndex = qi;
    return o;
}

[shader("fragment")]
float4 fsGradient2D(GradientQuadVSOut i) : SV_TARGET {
    GradientQuad2D q = gradientQuads[i.quadIndex];
    float4 top = lerp(q.color0, q.color1, i.uv.x);
    float4 bottom = lerp(q.color2, q.color3, i.uv.x);
    return lerp(top, bottom, i.uv.y);
}
