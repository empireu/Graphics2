// The world pass's linear colour:
[[vk::binding(0, 0)]] Texture2D<float4> sceneColor;

// Linear filtering with clamped addressing, so each tap averages the four texels around it:
[[vk::binding(1, 0)]] SamplerState sceneSampler;

// ExposureMeter.Width and ExposureMeter.Height in C#:
static const float2 MeterSize = float2(64.0, 32.0);

static const uint TapsPerAxis = 4;

// One triangle that covers the meter:
[shader("vertex")]
float4 vsMain(uint vertexIndex : SV_VertexID) : SV_Position {
    float2 clip = float2((vertexIndex << 1) & 2, vertexIndex & 2) * 2.0 - 1.0;
    return float4(clip, 0.0, 1.0);
}

// The mean base 2 logarithm of the luminance over the meter texel's block of the screen, from a grid of taps across the block:
[shader("fragment")]
float4 fsMain(float4 position : SV_Position) : SV_TARGET {
    float2 block = floor(position.xy);
    float sum = 0.0;

    for (uint y = 0; y < TapsPerAxis; y++) {
        for (uint x = 0; x < TapsPerAxis; x++) {
            float2 uv = (block + (float2(x, y) + 0.5) / TapsPerAxis) / MeterSize;
            float3 color = sceneColor.SampleLevel(sceneSampler, uv, 0.0).rgb;
            float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
            sum += log2(max(luminance, 1e-6));
        }
    }

    return float4(sum / (TapsPerAxis * TapsPerAxis), 0.0, 0.0, 1.0);
}
