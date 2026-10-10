// The world pass's linear colour, read one texel per pixel:
[[vk::binding(0, 0)]] Texture2D<float4> sceneColor;

// ToneMapper.PushConstants in C#, with the factor the scene's radiance is scaled by before the curve in parameters.x:
struct PushConstants {
    float4 parameters;
};

[[vk::push_constant]] PushConstants pc;

// One triangle that covers the screen:
[shader("vertex")]
float4 vsMain(uint vertexIndex : SV_VertexID) : SV_Position {
    float2 clip = float2((vertexIndex << 1) & 2, vertexIndex & 2) * 2.0 - 1.0;
    return float4(clip, 0.0, 1.0);
}

// Khronos PBR Neutral. A colour whose channels stay below about 0.76 passes through unchanged apart from a small offset near black, so the palette keeps its tuned values, and brighter colours roll off towards white with a little desaturation.
float3 PbrNeutral(float3 color) {
    const float startCompression = 0.8 - 0.04;
    const float desaturation = 0.15;

    float darkest = min(color.r, min(color.g, color.b));
    float offset = darkest < 0.08 ? darkest - 6.25 * darkest * darkest : 0.04;
    color -= offset;

    float peak = max(color.r, max(color.g, color.b));

    if (peak < startCompression) {
        return color;
    }

    const float d = 1.0 - startCompression;
    float newPeak = 1.0 - d * d / (peak + d - startCompression);
    color *= newPeak / peak;

    float whiteness = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
    return lerp(color, float3(newPeak, newPeak, newPeak), whiteness);
}

float3 LinearToSrgb(float3 color) {
    return select(color <= 0.0031308, color * 12.92, 1.055 * pow(color, 1.0 / 2.4) - 0.055);
}

float3 SrgbToLinear(float3 color) {
    return select(color <= 0.04045, color / 12.92, pow((color + 0.055) / 1.055, 2.4));
}

// Jimenez's interleaved gradient noise, uniform in [0, 1) and different at neighbouring pixels:
float GradientNoise(float2 pixel) {
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// The swapchain is an sRGB format, so the write encodes the linear result.
// Triangular noise of one 8-bit step is added in the encoded space first and taken back to linear, so a smooth gradient such as the sky dithers instead of banding:
[shader("fragment")]
float4 fsMain(float4 position : SV_Position) : SV_TARGET {
    float3 color = sceneColor.Load(int3(int2(position.xy), 0)).rgb * pc.parameters.x;
    float3 encoded = LinearToSrgb(saturate(PbrNeutral(max(color, 0.0))));

    float noise = GradientNoise(position.xy) + GradientNoise(position.xy + float2(47.0, 17.0)) - 1.0;
    encoded = saturate(encoded + noise / 255.0);

    return float4(SrgbToLinear(encoded), 1.0);
}
