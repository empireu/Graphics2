// Ground truth ambient occlusion (Jimenez et al. 2016), after Intel's XeGTAO, with its denoiser replaced by a depth-aware 4x4 average.
// View space here has x to the right, y up and z forward, so a pixel's view depth is its z. Screen space has y down.

// The depth prepass:
[[vk::binding(0, 0)]] Texture2D<float> sceneDepth;

// The occlusion the filter averages:
[[vk::binding(1, 0)]] Texture2D<float> rawOcclusion;

// AmbientOcclusion.PushConstants in C#:
struct PushConstants {
    // The tangents of half the horizontal and vertical field of view in xy, the near and far planes in metres in zw:
    float4 projection;

    // The radius in metres in x, and the frame's size in pixels in zw:
    float4 parameters;
};

[[vk::push_constant]] PushConstants pc;

static const float Pi = 3.14159265;
static const uint SliceCount = 2;
static const uint StepsPerSide = 4;

// The share of the radius over which a sample's weight falls off to nothing at its far end:
static const float FalloffShare = 0.6;

// XeGTAO's default power on the result, which deepens the occlusion where the integral alone reads too light:
static const float FinalPower = 2.2;

// The relative difference in view depth past which the filter leaves a neighbour out:
static const float FilterDepthTolerance = 0.05;

// A 4x4 Bayer matrix, so each 4x4 block of pixels holds each of the 16 values once and the filter's 4x4 window averages all of them:
static const uint Bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

// One triangle that covers the target:
[shader("vertex")]
float4 vsMain(uint vertexIndex : SV_VertexID) : SV_Position {
    float2 clip = float2((vertexIndex << 1) & 2, vertexIndex & 2) * 2.0 - 1.0;
    return float4(clip, 0.0, 1.0);
}

int2 ClampPixel(int2 pixel) {
    return clamp(pixel, int2(0, 0), int2(pc.parameters.zw) - 1);
}

// The view depth in metres of a depth buffer value from the camera's projection, whose depth runs from 0 at the near plane to 1 at the far plane:
float ViewDepth(float depth) {
    float near = pc.projection.z;
    float far = pc.projection.w;
    return far * near / (far - depth * (far - near));
}

float3 ViewPosition(int2 pixel, float depth) {
    float z = ViewDepth(depth);
    float2 ndc = (float2(pixel) + 0.5) / pc.parameters.zw * 2.0 - 1.0;
    return float3(ndc.x * pc.projection.x * z, -ndc.y * pc.projection.y * z, z);
}

float3 ViewPositionAt(int2 pixel) {
    pixel = ClampPixel(pixel);
    return ViewPosition(pixel, sceneDepth.Load(int3(pixel, 0)));
}

// The surface's view space normal from the depth around the pixel, taking on each axis the neighbour nearer in depth so an edge does not bend it, and turned to face the camera:
float3 ReconstructNormal(int2 pixel, float3 center) {
    float3 left = ViewPositionAt(pixel - int2(1, 0));
    float3 right = ViewPositionAt(pixel + int2(1, 0));
    float3 up = ViewPositionAt(pixel - int2(0, 1));
    float3 down = ViewPositionAt(pixel + int2(0, 1));

    float3 alongX = abs(center.z - left.z) < abs(right.z - center.z) ? center - left : right - center;
    float3 alongY = abs(center.z - up.z) < abs(down.z - center.z) ? center - up : down - center;
    float3 normal = normalize(cross(alongX, alongY));

    return dot(normal, -center) < 0.0 ? -normal : normal;
}

// The cosine of a sample's elevation above the view vector, faded towards the side's tangent plane as the sample nears the radius.
// A sample off the screen holds nothing to occlude with, so it gives the tangent plane, as clamping it to the edge would make the border pixels occlude:
float SampleHorizonCos(int2 samplePixel, float3 center, float3 viewVector, float lowCos, float falloffMultiplier, float falloffAdd) {
    if (any(samplePixel < 0) || any(samplePixel >= int2(pc.parameters.zw))) {
        return lowCos;
    }

    float3 delta = ViewPosition(samplePixel, sceneDepth.Load(int3(samplePixel, 0))) - center;
    float distance = length(delta);
    float weight = saturate(distance * falloffMultiplier + falloffAdd);

    return lerp(lowCos, dot(delta, viewVector) / max(distance, 1e-6), weight);
}

[shader("fragment")]
float fsOcclusion(float4 position : SV_Position) : SV_TARGET {
    int2 pixel = int2(position.xy);
    float depth = sceneDepth.Load(int3(pixel, 0));

    // The sky has nothing to occlude:
    if (depth >= 1.0) {
        return 1.0;
    }

    float3 center = ViewPosition(pixel, depth);
    float3 normal = ReconstructNormal(pixel, center);
    float3 viewVector = normalize(-center);

    float radius = pc.parameters.x;
    float metresPerPixel = 2.0 * pc.projection.x * center.z / pc.parameters.z;
    float radiusPixels = radius / metresPerPixel;

    // A radius under about a pixel finds nothing but the pixel's own surface:
    if (radiusPixels < 1.5) {
        return 1.0;
    }

    float falloffRange = FalloffShare * radius;
    float falloffMultiplier = -1.0 / falloffRange;
    float falloffAdd = (radius - falloffRange) / falloffRange + 1.0;

    float sliceNoise = (Bayer[(pixel.y & 3) * 4 + (pixel.x & 3)] + 0.5) / 16.0;
    float stepNoise = (Bayer[((pixel.y + 2) & 3) * 4 + ((pixel.x + 1) & 3)] + 0.5) / 16.0;

    float visibility = 0.0;

    for (uint slice = 0; slice < SliceCount; slice++) {
        float angle = (slice + sliceNoise) * Pi / SliceCount;
        float3 direction = float3(cos(angle), sin(angle), 0.0);
        float2 screenDirection = float2(direction.x, -direction.y);

        // The slice's plane holds the view vector and the direction. The normal is projected into it, and n is the projected normal's angle from the view vector:
        float3 orthogonalDirection = direction - dot(direction, viewVector) * viewVector;
        float3 axis = normalize(cross(orthogonalDirection, viewVector));
        float3 projectedNormal = normal - axis * dot(normal, axis);
        float projectedLength = length(projectedNormal);

        float signNormal = sign(dot(orthogonalDirection, projectedNormal));
        float cosNormal = saturate(dot(projectedNormal, viewVector) / max(projectedLength, 1e-6));
        float n = signNormal * acos(cosNormal);

        // Each side's horizon starts at the surface's own tangent plane:
        float lowCos0 = cos(n + 0.5 * Pi);
        float lowCos1 = cos(n - 0.5 * Pi);
        float horizonCos0 = lowCos0;
        float horizonCos1 = lowCos1;

        for (uint stepIndex = 0; stepIndex < StepsPerSide; stepIndex++) {
            // Squared, so the steps crowd near the pixel where occlusion matters most, and at least a pixel and a bit out, so a step never reads the pixel itself:
            float distanceShare = (stepIndex + stepNoise) / StepsPerSide;
            float2 offset = screenDirection * max(distanceShare * distanceShare * radiusPixels, 1.5 + stepIndex);

            horizonCos0 = max(horizonCos0, SampleHorizonCos(int2(floor(float2(pixel) + 0.5 + offset)), center, viewVector, lowCos0, falloffMultiplier, falloffAdd));
            horizonCos1 = max(horizonCos1, SampleHorizonCos(int2(floor(float2(pixel) + 0.5 - offset)), center, viewVector, lowCos1, falloffMultiplier, falloffAdd));
        }

        float h0 = -acos(clamp(horizonCos1, -1.0, 1.0));
        float h1 = acos(clamp(horizonCos0, -1.0, 1.0));
        h0 = n + clamp(h0 - n, -0.5 * Pi, 0.5 * Pi);
        h1 = n + clamp(h1 - n, -0.5 * Pi, 0.5 * Pi);

        // The cosine weighted visible arc on each side of the normal:
        float arc0 = (cosNormal + 2.0 * h0 * sin(n) - cos(2.0 * h0 - n)) / 4.0;
        float arc1 = (cosNormal + 2.0 * h1 * sin(n) - cos(2.0 * h1 - n)) / 4.0;
        visibility += projectedLength * (arc0 + arc1);
    }

    // A visible pixel is never wholly occluded:
    return clamp(pow(saturate(visibility / SliceCount), FinalPower), 0.03, 1.0);
}

// The occlusion averaged over the 4x4 block of pixels that holds every noise value once, leaving out neighbours at another depth, so an edge keeps its own side's occlusion:
[shader("fragment")]
float fsFilter(float4 position : SV_Position) : SV_TARGET {
    int2 pixel = int2(position.xy);
    float centerDepth = ViewDepth(sceneDepth.Load(int3(pixel, 0)));

    float sum = 0.0;
    float weightSum = 0.0;

    for (int y = -2; y < 2; y++) {
        for (int x = -2; x < 2; x++) {
            int2 neighbour = ClampPixel(pixel + int2(x, y));
            float neighbourDepth = ViewDepth(sceneDepth.Load(int3(neighbour, 0)));
            float weight = abs(neighbourDepth - centerDepth) <= FilterDepthTolerance * centerDepth ? 1.0 : 0.0;

            sum += weight * rawOcclusion.Load(int3(neighbour, 0));
            weightSum += weight;
        }
    }

    // The pixel's own sample always passes the test, so the weights never sum to 0:
    return sum / weightSum;
}
