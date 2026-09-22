struct DrawRecord {
    float3 origin;
    float voxelSize;
    uint materialVolume;
    uint materialSizeX;
    uint materialSizeY;
    uint materialSizeZ;
};

[[vk::binding(0, 0)]] StructuredBuffer<DrawRecord> draws;

// One texel per voxel sample of the chunk that owns it, so a pixel reads it with the mesh's own local coordinates and needs no material attribute in the vertex:
[[vk::binding(0, 1)]] Texture3D<uint> materialVolumes[1024];

static const uint MaterialRock = 0;
static const uint MaterialIce = 1;
static const uint MaterialSnow = 2;
static const uint MaterialSandstone = 3;
static const uint MaterialShale = 4;
static const uint MaterialLimestone = 5;
static const uint MaterialBasalt = 6;
static const uint MaterialGneiss = 7;
static const uint MaterialCount = 8;

// Air carries no material, and a cell's reconstruction skips those corners:
static const uint MaterialNull = 255;

// Linear albedo, because the swapchain is an sRGB format and the write converts it:
static const float3 MaterialAlbedo[MaterialCount] = {
    float3(0.17, 0.11, 0.06),   // rock, the generic fallback
    float3(0.30, 0.38, 0.42),   // ice
    float3(0.78, 0.82, 0.88),   // snow
    float3(0.42, 0.31, 0.19),   // sandstone
    float3(0.16, 0.18, 0.21),   // shale
    float3(0.47, 0.46, 0.42),   // limestone
    float3(0.10, 0.10, 0.12),   // basalt
    float3(0.22, 0.20, 0.21)    // gneiss
};

struct VertexInput {
    float3 position;
    float3 normal;
};

struct VertexOutput {
    float4 position : SV_Position;
    float3 normal : NORMAL;

    // Chunk-local voxel units, which is exactly the material volume's texel space:
    float3 voxelPosition : TEXCOORD0;

    nointerpolation uint materialVolume : TEXCOORD1;
    nointerpolation uint3 materialSize : TEXCOORD2;
};

struct PushConstants {
    row_major float4x4 viewProj;
};

[[vk::push_constant]] PushConstants pc;

[shader("vertex")]
VertexOutput vsMain(VertexInput input, uint recordIndex : SV_StartInstanceLocation) {
    VertexOutput output;

    // The indirect command's firstInstance, which is the record's own index. SV_InstanceID would be the instance within the draw, which is always 0 here because every command has one instance.
    DrawRecord draw = draws[recordIndex];

    // Positions are chunk-local voxel units, so the origin stays exact and the scale is applied once:
    float3 worldPosition = (draw.origin + input.position.xyz) * draw.voxelSize;

    output.position = mul(float4(worldPosition, 1.0), pc.viewProj);
    output.normal = input.normal.xyz;
    output.voxelPosition = input.position.xyz;
    output.materialVolume = draw.materialVolume;
    output.materialSize = uint3(draw.materialSizeX, draw.materialSizeY, draw.materialSizeZ);
    return output;
}

// Trilinear weight of one cell corner, whose index packs x in bit 0, y in bit 1 and z in bit 2:
float CornerWeight(uint corner, float3 fraction) {
    float x = ((corner & 1) != 0) ? fraction.x : 1.0 - fraction.x;
    float y = ((corner & 2) != 0) ? fraction.y : 1.0 - fraction.y;
    float z = ((corner & 4) != 0) ? fraction.z : 1.0 - fraction.z;
    return x * y * z;
}

uint LoadCorner(uint volume, int3 cell, uint corner) {
    int3 offset = int3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
    return materialVolumes[volume].Load(int4(cell + offset, 0));
}

// Reconstructs which material the surface belongs to at this pixel.
// Each material's weight is the trilinear interpolation of its own indicator over the cell's eight corner ids, so the boundary between two materials lands where their weights cross rather than on a triangle edge, and the weights are normalized over the solid corners because air carries no material.
float3 ReconstructAlbedo(uint volume, int3 size, float3 voxelPosition) {
    int3 maxCell = int3(size) - 1;
    int3 cell = clamp(int3(floor(voxelPosition)), int3(0, 0, 0), maxCell - 1);
    float3 fraction = voxelPosition - float3(cell);

    float weights[MaterialCount];
    float solidWeight = 0.0;

    for (uint material = 0; material < MaterialCount; material++) {
        weights[material] = 0.0;
    }

    for (uint corner = 0; corner < 8; corner++) {
        uint id = LoadCorner(volume, cell, corner);

        if (id == MaterialNull) {
            continue;
        }

        float weight = CornerWeight(corner, fraction);
        solidWeight += weight;

        if (id < MaterialCount) {
            weights[id] += weight;
        }
    }

    float3 albedo = MaterialAlbedo[MaterialRock];
    float best = -1.0;

    if (solidWeight > 0.0) {
        for (uint material = 0; material < MaterialCount; material++) {
            float normalised = weights[material] / solidWeight;

            if (normalised > best) {
                best = normalised;
                albedo = MaterialAlbedo[material];
            }
        }
    }

    return albedo;
}

[shader("fragment")]
float4 fsMain(VertexOutput input) : SV_TARGET {
    float3 normal = normalize(input.normal);
    float3 albedo = ReconstructAlbedo(input.materialVolume, input.materialSize, input.voxelPosition);

    static const float3 sunDirection = normalize(float3(0.35, 0.9, -0.35));
    static const float3 sunColor = float3(1.0, 0.96, 0.90);
    static const float3 zenithColor = float3(0.22, 0.30, 0.46);
    static const float3 horizonColor = float3(0.38, 0.44, 0.54);

    float3 sky = lerp(horizonColor, zenithColor, saturate(normal.y));
    float sunlight = saturate(dot(normal, sunDirection)) * 0.7;

    return float4(albedo * (sky + sunColor * sunlight), 1.0);
}
