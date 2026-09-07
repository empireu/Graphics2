// cameraPos is needed beyond the viewProj matrix because:
// - The vertex shader builds a camera-facing billboard
// - The fragment shader reconstructs the view ray from the camera position
cbuffer Camera : register(b0) {
    row_major float4x4 viewProj;
    float3 cameraPos;
};

// Two triangles forming a quad. 
// Each line uses 6 vertices from SV_VertexID.
static const float2 corners[6] = {
    float2(0.0, 1.0),
    float2(1.0, 1.0),
    float2(0.0, 0.0),
    float2(0.0, 0.0),
    float2(1.0, 1.0),
    float2(1.0, 0.0)
};

// One line segment. start.w stores the radius.
// Colors are per-endpoint and interpolated along the axis at the hit point.
struct Line3D {
    float4 start;
    float4 end;
    float4 startColor;
    float4 endColor;
};

[[vk::binding(1, 0)]] StructuredBuffer<Line3D> lines;

struct LineVSOut {
    float4 position : SV_POSITION;
    float3 worldPos : TEXCOORD0;
    nointerpolation uint lineIndex : TEXCOORD1;
};

// The vertex shader builds a camera-facing billboard that covers the capsule's screen-space silhouette.
// The quad is centered at the capsule midpoint and oriented in the plane perpendicular to the view direction.
//
// axis1 is the projection of the line direction onto the screen plane.
// When the camera looks along the line, this projection is near zero and we fall back to an arbitrary perpendicular vector.
// axis2 is perpendicular to both the view direction and axis1.
//
// The sizes must account for perspective magnification.
// The near end of the capsule is closer to the camera and projects larger than the far end.
// The perspective correction factor D / nearDepth scales the quad so it covers the magnified silhouette, where nearDepth is the depth of the nearest capsule surface point.
[shader("vertex")]
LineVSOut vsLine3D(uint vertexId : SV_VertexID) {
    LineVSOut o;
    uint li = vertexId / 6;
    uint c = vertexId % 6;
    float2 corner = corners[c];
    Line3D l = lines[li];

    float3 start = l.start.xyz;
    float3 end = l.end.xyz;
    float radius = l.start.w;
    float3 ab = end - start;
    float abLen = max(length(ab), 1e-6);
    float3 dir = ab / abLen;
    float3 mid = (start + end) * 0.5;

    float3 viewDir = normalize(mid - cameraPos);

    // Project the line direction onto the screen plane (perpendicular to view):
    float3 dirProj = dir - viewDir * dot(dir, viewDir);
    float dirProjLen = length(dirProj);

    float3 axis1;
    if (dirProjLen < 1e-4) {
        // Camera looks along the line. Pick any perpendicular to viewDir:
        axis1 = cross(viewDir, float3(0.0, 1.0, 0.0));
        if (length(axis1) < 1e-4) {
            axis1 = cross(viewDir, float3(1.0, 0.0, 0.0));
        }
        
        axis1 = normalize(axis1);
    } else {
        axis1 = dirProj / dirProjLen;
    }
    float3 axis2 = normalize(cross(viewDir, axis1));

    // Perspective correction. 
    // The near end of the capsule is closer to the camera, so it projects larger on the billboard. 
    // nearDepth is the depth of the nearest capsule surface point: the near endpoint center minus the radius along the view direction. 
    // perspFactor scales the quad to cover that magnified silhouette.
    float halfLen = abLen * 0.5;
    float D = length(mid - cameraPos);
    float cosAngle = dot(dir, viewDir);
    float nearDepth = D - halfLen * abs(cosAngle) - radius;
    float perspFactor = D / max(nearDepth, 1e-4);

    // sizeX is the projected length of the cylinder body plus the end cap diameters.
    // sizeY is always the diameter. 
    // Both are scaled by perspFactor.
    float sizeX = (abLen * dirProjLen + radius * 2.0) * perspFactor;
    float sizeY = (radius * 2.0) * perspFactor;

    float2 centered = corner - 0.5;
    float3 worldPos = mid + axis1 * (centered.x * sizeX) + axis2 * (centered.y * sizeY);

    o.position = mul(float4(worldPos, 1.0), viewProj);
    o.worldPos = worldPos;
    o.lineIndex = li;
    return o;
}

struct LineFSOut {
    float4 color : SV_TARGET;
    float depth : SV_Depth;
};

// Ray-capsule intersection. 
// Returns the distance t along the ray to the nearest surface hit, or -1.0 if no hit. 
// h is the axial parameter of the hit point along the segment (0 at start, 1 at end), used for color.
// The capsule is an infinite cylinder clipped to the segment plus two end-cap spheres.
// Three intersections are tested and the nearest valid hit wins:
//  1. Infinite cylinder. ray and axis projected perpendicular to the axis, solved as a quadratic. Hit is valid if it falls within the segment (s in [0, 1]).
//  2. Start sphere. ray-sphere at the start point. Both roots are checked.
//  3. End sphere. ray-sphere at the end point. Both roots are checked.
// Sphere hits within the segment are redundant with the cylinder but will be farther, so nearest-wins handles them. 
// Sphere hits outside the segment are the rounded caps.
float intersectCapsule(float3 ro, float3 rd, float3 a, float3 b, float r, out float h) {
    float3 ab = b - a;
    float abLenSq = max(dot(ab, ab), 1e-6);
    float3 ao = ro - a;

    float bestT = 1e30;
    h = 0.0;

    // Cylinder body. Project ray and origin perpendicular to the axis:
    float3 n = rd - ab * (dot(rd, ab) / abLenSq);
    float3 m = ao - ab * (dot(ao, ab) / abLenSq);
    float aa = dot(n, n);
    if (aa > 1e-8) {
        float bb = 2.0 * dot(m, n);
        float cc = dot(m, m) - r * r;
        float disc = bb * bb - 4.0 * aa * cc;
        if (disc >= 0.0) {
            float sq = sqrt(disc);
            float t0 = (-bb - sq) / (2.0 * aa);
            float t1 = (-bb + sq) / (2.0 * aa);
            if (t0 > 0.001 && t0 < bestT) {
                float s = dot(ro + rd * t0 - a, ab) / abLenSq;
                if (s >= 0.0 && s <= 1.0) { bestT = t0; h = s; }
            }
            if (t1 > 0.001 && t1 < bestT) {
                float s = dot(ro + rd * t1 - a, ab) / abLenSq;
                if (s >= 0.0 && s <= 1.0) { bestT = t1; h = s; }
            }
        }
    }

    // Start sphere. Both roots checked so the far surface of the cap is not missed:
    float3 ocA = ro - a;
    float bA = dot(ocA, rd);
    float cA = dot(ocA, ocA) - r * r;
    float discA = bA * bA - cA;
    if (discA >= 0.0) {
        float sq = sqrt(discA);
        float t0 = -bA - sq;
        float t1 = -bA + sq;
        if (t0 > 0.001 && t0 < bestT) {
            bestT = t0;
            h = clamp(dot(ro + rd * t0 - a, ab) / abLenSq, 0.0, 1.0);
        }
        if (t1 > 0.001 && t1 < bestT) {
            bestT = t1;
            h = clamp(dot(ro + rd * t1 - a, ab) / abLenSq, 0.0, 1.0);
        }
    }

    // End sphere:
    float3 ocB = ro - b;
    float bB = dot(ocB, rd);
    float cB = dot(ocB, ocB) - r * r;
    float discB = bB * bB - cB;
    if (discB >= 0.0) {
        float sq = sqrt(discB);
        float t0 = -bB - sq;
        float t1 = -bB + sq;
        if (t0 > 0.001 && t0 < bestT) {
            bestT = t0;
            h = clamp(dot(ro + rd * t0 - a, ab) / abLenSq, 0.0, 1.0);
        }
        if (t1 > 0.001 && t1 < bestT) {
            bestT = t1;
            h = clamp(dot(ro + rd * t1 - a, ab) / abLenSq, 0.0, 1.0);
        }
    }

    return bestT < 1e29 ? bestT : -1.0;
}

// The fragment shader raytraces the capsule from the camera through each fragment.
// The view ray is reconstructed from cameraPos and the fragment's worldPos.
// If the ray misses the capsule, the fragment is discarded.
// On a hit, the depth is written from the actual surface intersection point (not the quad surface), so the line occludes and is occluded correctly by other 3D geometry.
// Color is interpolated along the axis by the hit parameter h.
[shader("fragment")]
LineFSOut fsLine3D(LineVSOut i) {
    Line3D l = lines[i.lineIndex];
    float3 start = l.start.xyz;
    float3 end = l.end.xyz;
    float radius = l.start.w;

    float3 ro = cameraPos;
    float3 rd = normalize(i.worldPos - cameraPos);

    float h;
    float t = intersectCapsule(ro, rd, start, end, radius, h);

    if (t < 0.0) {
        discard;
    }

    float3 surfacePoint = ro + rd * t;
    float4 clipPos = mul(float4(surfacePoint, 1.0), viewProj);

    LineFSOut o;
    o.depth = clipPos.z / clipPos.w;
    o.color = lerp(l.startColor, l.endColor, clamp(h, 0.0, 1.0));
    return o;
}
