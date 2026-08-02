#include "scene_data.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct Plane {
    float3 n;
    float d;
};

float3 WorldFromNdc(float2 ndc, float depth)
{
    float4 p = mul(push.camera->invViewProj, float4(ndc, depth, 1.0));
    return p.xyz / p.w;
}

Plane MakePlane(float3 a, float3 b, float3 c, float3 insidePoint)
{
    Plane p;
    p.n = normalize(cross(b - a, c - a));
    p.d = -dot(p.n, a);
    if (dot(p.n, insidePoint) + p.d < 0.0) {
        p.n = -p.n;
        p.d = -p.d;
    }
    return p;
}

bool PointInsideCluster(float3 p, Plane planes[6])
{
    for (int i = 0; i < 6; ++i) {
        if (dot(planes[i].n, p) + planes[i].d < 0.0) return false;
    }
    return true;
}

bool SphereIntersectsCluster(float3 center, float radius, Plane planes[6])
{
    for (int i = 0; i < 6; ++i) {
        if (dot(planes[i].n, center) + planes[i].d < -radius) return false;
    }
    return true;
}

bool PunctualLightIntersectsCluster(
    LightData light,
    Plane planes[6])
{
    float lightType = light.direction.w;
    if (lightType == 1.0)
        return SphereIntersectsCluster(
            light.position.xyz,
            light.position.w,
            planes);
    if (lightType != 2.0)
        return false;
    float3 axis = normalize(light.direction.xyz);
    float outerCos = clamp(light.shapeParams.y, 0.001, 0.999);
    float tanTheta =
        sqrt(max(1.0 - outerCos * outerCos, 0.0)) / outerCos;
    float halfRange = light.position.w * 0.5;
    float capRadius = light.position.w * tanTheta;
    float boundsRadius = sqrt(
        halfRange * halfRange + capRadius * capRadius);
    return SphereIntersectsCluster(
        light.position.xyz + axis * halfRange,
        boundsRadius,
        planes);
}

bool PointInsideFiniteCone(float3 p, float3 apex, float3 axis, float height, float tanTheta)
{
    float3 v = p - apex;
    float axial = dot(v, axis);
    if (axial < 0.0 || axial > height) return false;
    float radial2 = max(dot(v, v) - axial * axial, 0.0);
    float radius = axial * tanTheta;
    return radial2 <= radius * radius;
}

bool SegmentIntersectsCluster(float3 a, float3 b, Plane planes[6])
{
    float enter = 0.0;
    float exit = 1.0;
    float3 d = b - a;

    for (int i = 0; i < 6; ++i) {
        float da = dot(planes[i].n, a) + planes[i].d;
        float db = dot(planes[i].n, b) + planes[i].d;
        float denom = db - da;

        if (abs(denom) < 1e-6) {
            if (da < 0.0) return false;
        } else {
            float t = -da / denom;
            if (denom > 0.0) enter = max(enter, t);
            else exit = min(exit, t);
            if (enter > exit) return false;
        }
    }

    return true;
}

bool SegmentIntersectsConeCap(float3 a, float3 b, float3 apex, float3 axis, float height, float capRadius)
{
    float3 d = b - a;
    float denom = dot(d, axis);
    if (abs(denom) < 1e-6) return false;

    float t = dot(apex + axis * height - a, axis) / denom;
    if (t < 0.0 || t > 1.0) return false;

    float3 p = a + d * t;
    float3 capCenter = apex + axis * height;
    float3 r = p - capCenter;
    float radial2 = max(dot(r, r) - dot(r, axis) * dot(r, axis), 0.0);
    return radial2 <= capRadius * capRadius;
}

bool SegmentIntersectsFiniteCone(float3 a, float3 b, float3 apex, float3 axis, float height, float tanTheta)
{
    if (PointInsideFiniteCone(a, apex, axis, height, tanTheta)) return true;
    if (PointInsideFiniteCone(b, apex, axis, height, tanTheta)) return true;

    float3 d = b - a;
    float3 q = a - apex;
    float k = tanTheta * tanTheta;
    float dv = dot(d, axis);
    float qv = dot(q, axis);
    float aCoef = dot(d, d) - (1.0 + k) * dv * dv;
    float bCoef = 2.0 * (dot(q, d) - (1.0 + k) * qv * dv);
    float cCoef = dot(q, q) - (1.0 + k) * qv * qv;

    if (abs(aCoef) > 1e-6) {
        float disc = bCoef * bCoef - 4.0 * aCoef * cCoef;
        if (disc >= 0.0) {
            float root = sqrt(disc);
            float invDenom = 0.5 / aCoef;
            float t0 = (-bCoef - root) * invDenom;
            float t1 = (-bCoef + root) * invDenom;
            float axial0 = qv + t0 * dv;
            float axial1 = qv + t1 * dv;
            if (t0 >= 0.0 && t0 <= 1.0 && axial0 >= 0.0 && axial0 <= height) return true;
            if (t1 >= 0.0 && t1 <= 1.0 && axial1 >= 0.0 && axial1 <= height) return true;
        }
    } else if (abs(bCoef) > 1e-6) {
        float t = -cCoef / bCoef;
        float axial = qv + t * dv;
        if (t >= 0.0 && t <= 1.0 && axial >= 0.0 && axial <= height) return true;
    }

    return SegmentIntersectsConeCap(a, b, apex, axis, height, height * tanTheta);
}

bool FiniteConeIntersectsCluster(float3 apex, float3 axis, float height, float tanTheta, float3 corners[8], Plane planes[6])
{
    if (PointInsideCluster(apex, planes)) return true;
    if (PointInsideCluster(apex + axis * height, planes)) return true;
    if (SegmentIntersectsCluster(apex, apex + axis * height, planes)) return true;

    for (int i = 0; i < 8; ++i) {
        if (PointInsideFiniteCone(corners[i], apex, axis, height, tanTheta)) return true;
    }

    uint edgeA[12] = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3 };
    uint edgeB[12] = { 1, 2, 3, 0, 5, 6, 7, 4, 4, 5, 6, 7 };
    for (int e = 0; e < 12; ++e) {
        if (SegmentIntersectsFiniteCone(corners[edgeA[e]], corners[edgeB[e]], apex, axis, height, tanTheta)) return true;
    }

    return false;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 threadId : SV_DispatchThreadID)
{
    uint clusterIndex = threadId.x;
    uint clusterCount = (uint)push.clusterGrid.w;
    if (clusterIndex >= clusterCount) return;

    uint gridX = (uint)push.clusterGrid.x;
    uint gridY = (uint)push.clusterGrid.y;
    uint gridZ = (uint)push.clusterGrid.z;
    uint maxLights = (uint)push.clusterParams.y;
    uint clustersPerSlice = gridX * gridY;
    uint zSlice = clusterIndex / clustersPerSlice;
    uint xy = clusterIndex % clustersPerSlice;
    uint tileX = xy % gridX;
    uint tileY = xy / gridX;

    float2 resolution = push.resolution.xy;
    float tileSize = push.clusterParams.x;
    float2 minPixel = float2(tileX, tileY) * tileSize;
    float2 maxPixel = min(minPixel + tileSize, resolution);
    float2 minNdc = float2((minPixel.x / resolution.x) * 2.0 - 1.0, 1.0 - (maxPixel.y / resolution.y) * 2.0);
    float2 maxNdc = float2((maxPixel.x / resolution.x) * 2.0 - 1.0, 1.0 - (minPixel.y / resolution.y) * 2.0);
    float z0 = (float)zSlice / (float)gridZ;
    float z1 = (float)(zSlice + 1) / (float)gridZ;

    float3 corners[8] = {
        WorldFromNdc(float2(minNdc.x, minNdc.y), z0),
        WorldFromNdc(float2(maxNdc.x, minNdc.y), z0),
        WorldFromNdc(float2(maxNdc.x, maxNdc.y), z0),
        WorldFromNdc(float2(minNdc.x, maxNdc.y), z0),
        WorldFromNdc(float2(minNdc.x, minNdc.y), z1),
        WorldFromNdc(float2(maxNdc.x, minNdc.y), z1),
        WorldFromNdc(float2(maxNdc.x, maxNdc.y), z1),
        WorldFromNdc(float2(minNdc.x, maxNdc.y), z1)
    };

    float3 center = float3(0.0);
    for (int i = 0; i < 8; ++i) center += corners[i];
    center *= 0.125;

    Plane planes[6] = {
        MakePlane(corners[0], corners[1], corners[2], center),
        MakePlane(corners[6], corners[5], corners[4], center),
        MakePlane(corners[3], corners[0], corners[4], center),
        MakePlane(corners[1], corners[2], corners[6], center),
        MakePlane(corners[0], corners[1], corners[5], center),
        MakePlane(corners[2], corners[3], corners[7], center)
    };

    uint outOffset = clusterIndex * maxLights;
    uint count = 0;

    for (uint lightIndex = 0; lightIndex < push.lightCount && count < maxLights; ++lightIndex) {
        LightData light = push.lights[lightIndex];
        float lightType = light.direction.w;
        bool intersects = lightType == 0.0;

        if (lightType == 1.0 || lightType == 2.0) {
            intersects = PunctualLightIntersectsCluster(
                light,
                planes);
            uint faceIndex = lightIndex * 6;
            if (faceIndex < push.punctualShadowFaceCount) {
                PunctualShadowFaceData face =
                    push.punctualShadowFaces[faceIndex];
                if (face.textureIndicesAndFlags.z >= 0.5 ||
                    face.textureIndicesAndFlags.w >= 0.5) {
                    LightData committedLight = light;
                    committedLight.position =
                        face.committedLightPosition;
                    committedLight.direction =
                        face.committedLightDirection;
                    committedLight.shapeParams =
                        face.committedLightShapeParams;
                    intersects = intersects ||
                        PunctualLightIntersectsCluster(
                            committedLight,
                            planes);
                }
            }
        }

        if (intersects) {
            push.clusterLightIndices[outOffset + count] = lightIndex;
            count++;
        }
    }

    ClusterRecord record;
    record.offset = outOffset;
    record.count = count;
    push.clusterRecords[clusterIndex] = record;
}
