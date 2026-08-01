#include <metal_stdlib>
#include <metal_math>
#include <metal_texture>
using namespace metal;

            raytracing::intersection_params _slang_ray_flags_to_intersection_params(uint flags){
                raytracing::intersection_params params;
                if (flags & 0x01) /* RAY_FLAG_FORCE_OPAQUE */
                    params.force_opacity(raytracing::forced_opacity::opaque);
                if (flags & 0x02) /* RAY_FLAG_FORCE_NON_OPAQUE */
                    params.force_opacity(raytracing::forced_opacity::non_opaque);
                if (flags & 0x10) /* RAY_FLAG_CULL_BACK_FACING_TRIANGLES */
                    params.set_triangle_cull_mode(raytracing::triangle_cull_mode::back);
                if (flags & 0x20) /* RAY_FLAG_CULL_FRONT_FACING_TRIANGLES */
                    params.set_triangle_cull_mode(raytracing::triangle_cull_mode::front);
                if (flags & 0x40) /* RAY_FLAG_CULL_OPAQUE */
                    params.set_opacity_cull_mode(raytracing::opacity_cull_mode::opaque);
                if (flags & 0x80) /* RAY_FLAG_CULL_NON_OPAQUE */
                    params.set_opacity_cull_mode(raytracing::opacity_cull_mode::non_opaque);
                if (flags & 0x100) /* RAY_FLAG_SKIP_TRIANGLES */
                    params.set_geometry_cull_mode(raytracing::geometry_cull_mode::triangle);
                if (flags & 0x200) /* RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES */
                    params.set_geometry_cull_mode(raytracing::geometry_cull_mode::bounding_box);
                return params;
            }
        

                uint _slang_committed_intersection_type_to_uint(raytracing::intersection_type type){
                    switch(type){
                        case raytracing::intersection_type::none:
                            return 0; /* COMMITTED_NOTHING */
                        case raytracing::intersection_type::triangle:
                            return 1; /* COMMITTED_TRIANGLE_HIT */
                        case raytracing::intersection_type::bounding_box:
                            return 2; /* COMMITTED_PROCEDURAL_PRIMITIVE_HIT */
                        default:
                            return 0xFFFFFFFF; // Invalid value to indicate error case, should never be returned by the API
                    }
                }
            

#line 12 "Content/shaders/nishita_sky.slang"
bool IntersectSphere_0(float3 origin_0, float3 dir_0, float radius_0, float thread* t0_0, float thread* t1_0)
{

#line 13
    float b_0 = dot(origin_0, dir_0);

    float d_0 = b_0 * b_0 - (dot(origin_0, origin_0) - radius_0 * radius_0);
    if(d_0 < 0.0)
    {

#line 16
        return false;
    }

#line 17
    float sqrtD_0 = sqrt(d_0);
    float _S1 = - b_0;

#line 18
    *t0_0 = _S1 - sqrtD_0;
    *t1_0 = _S1 + sqrtD_0;
    return true;
}


#line 58 "Content/shaders/scene_data.slang"
struct SkyParams_0
{
    float3 sunDirection_0;
    float sunAngularRadius_0;
    float sunIntensity_0;
    float turbidity_0;
    float groundAlbedo_0;
    float3 pad_sky_0;
};


#line 23 "Content/shaders/nishita_sky.slang"
float3 GetSkyRadiance_0(float3 viewDir_0, float3 sunDirection_1, const SkyParams_0 constant* sky_0)
{

#line 24
    float3 origin_1 = float3(0.0, 6.360001e+06, 0.0);
    thread float t0_1 = 0.0;

#line 25
    thread float t1_1 = 0.0;
    bool _S2 = IntersectSphere_0(origin_1, viewDir_0, 6.42e+06, &t0_1, &t1_1);

#line 26
    if(!_S2)
    {

#line 27
        return float3(0.0) ;
    }
    float _S3 = t1_1;
    thread float tEarth0_0 = 0.0;

#line 30
    thread float tEarth1_0 = 0.0;
    bool _S4 = IntersectSphere_0(origin_1, viewDir_0, 6.36e+06, &tEarth0_0, &tEarth1_0);

#line 31
    bool _S5;

#line 31
    if(_S4)
    {

#line 31
        _S5 = tEarth0_0 > 0.0;

#line 31
    }
    else
    {

#line 31
        _S5 = false;

#line 31
    }

#line 31
    float tMax_0;

#line 31
    if(_S5)
    {

#line 31
        tMax_0 = tEarth0_0;

#line 31
    }
    else
    {

#line 31
        tMax_0 = _S3;

#line 31
    }

#line 37
    float _S6 = tMax_0 / 16.0;

#line 42
    float3 _S7 = float3(0.0) ;


    float mu_0 = dot(viewDir_0, sunDirection_1);
    float _S8 = 1.0 + mu_0 * mu_0;

#line 46
    float phaseR_0 = 0.05968315154314041 * _S8;

    float phaseM_0 = 0.11936630308628082 * (0.42543596029281616 * _S8) / (2.57456398010253906 * pow(1.57456398010253906 - 1.51600003242492676 * mu_0, 1.5));

#line 48
    int i_0 = int(0);

#line 48
    float tCurrent_0 = 0.0;

#line 48
    float opticalDepthR_0 = 0.0;

#line 48
    float opticalDepthM_0 = 0.0;

#line 48
    float3 sumR_0 = _S7;

#line 48
    float3 sumM_0 = _S7;


    for(;;)
    {

#line 51
        if(i_0 < int(16))
        {
        }
        else
        {

#line 51
            break;
        }

#line 52
        float3 samplePos_0 = origin_1 + viewDir_0 * float3((tCurrent_0 + _S6 * 0.5)) ;
        float height_0 = length(samplePos_0) - 6.36e+06;
        if(height_0 < 0.0)
        {

#line 54
            break;
        }
        float _S9 = - height_0;

#line 56
        float hr_0 = exp(_S9 / 8000.0) * _S6;
        float hm_0 = exp(_S9 / 1200.0) * _S6;

        float opticalDepthR_1 = opticalDepthR_0 + hr_0;
        float opticalDepthM_1 = opticalDepthM_0 + hm_0;

        thread float t0Light_0 = 0.0;

#line 62
        thread float t1Light_0 = 0.0;
        bool _S10 = IntersectSphere_0(samplePos_0, sunDirection_1, 6.42e+06, &t0Light_0, &t1Light_0);
        float _S11 = t1Light_0 / 8.0;

#line 70
        bool _S12 = IntersectSphere_0(samplePos_0, sunDirection_1, 6.36e+06, &tEarth0_0, &tEarth1_0);

#line 70
        if(_S12)
        {

#line 70
            _S5 = tEarth0_0 > 0.0;

#line 70
        }
        else
        {

#line 70
            _S5 = false;

#line 70
        }



        if(!_S5)
        {

#line 74
            int j_0 = int(0);

#line 74
            float tCurrentLight_0 = 0.0;

#line 74
            float opticalDepthLightR_0 = 0.0;

#line 74
            float opticalDepthLightM_0 = 0.0;
            for(;;)
            {

#line 75
                if(j_0 < int(8))
                {
                }
                else
                {

#line 75
                    break;
                }
                float heightLight_0 = length(samplePos_0 + sunDirection_1 * float3((tCurrentLight_0 + _S11 * 0.5)) ) - 6.36e+06;
                if(heightLight_0 < 0.0)
                {

#line 78
                    break;
                }
                float _S13 = - heightLight_0;

#line 80
                float opticalDepthLightR_1 = opticalDepthLightR_0 + exp(_S13 / 8000.0) * _S11;
                float opticalDepthLightM_1 = opticalDepthLightM_0 + exp(_S13 / 1200.0) * _S11;
                float tCurrentLight_1 = tCurrentLight_0 + _S11;

#line 75
                j_0 = j_0 + int(1);

#line 75
                tCurrentLight_0 = tCurrentLight_1;

#line 75
                opticalDepthLightR_0 = opticalDepthLightR_1;

#line 75
                opticalDepthLightM_0 = opticalDepthLightM_1;

#line 75
            }

#line 87
            float3 attenuation_0 = exp(- (float3(5.80000005356851034e-06, 0.00001350000002276, 0.00003310000101919) * float3((opticalDepthR_1 + opticalDepthLightR_0))  + float3((0.00002200000017183 * (opticalDepthM_1 + opticalDepthLightM_0))) ));


            float3 sumM_1 = sumM_0 + attenuation_0 * float3(hm_0) ;

#line 90
            sumR_0 = sumR_0 + attenuation_0 * float3(hr_0) ;

#line 90
            sumM_0 = sumM_1;

#line 74
        }

#line 92
        float tCurrent_1 = tCurrent_0 + _S6;

#line 51
        i_0 = i_0 + int(1);

#line 51
        tCurrent_0 = tCurrent_1;

#line 51
        opticalDepthR_0 = opticalDepthR_1;

#line 51
        opticalDepthM_0 = opticalDepthM_1;

#line 51
    }

#line 51
    float3 _S14 = float3(sky_0->sunIntensity_0) ;

#line 95
    float3 color_0 = (sumR_0 * float3(5.80000005356851034e-06, 0.00001350000002276, 0.00003310000101919) * float3(phaseR_0)  + sumM_0 * float3(0.00001999999949476)  * float3(phaseM_0) ) * _S14 * float3(20.0) ;

#line 100
    if(mu_0 > (cos(max(sky_0->sunAngularRadius_0, 0.00100000004749745))))
    {

#line 100
        _S5 = tMax_0 == t1_1;

#line 100
    }
    else
    {

#line 100
        _S5 = false;

#line 100
    }

#line 100
    float3 color_1;

#line 100
    if(_S5)
    {

#line 100
        color_1 = color_0 + _S14 * exp(- (float3(5.80000005356851034e-06, 0.00001350000002276, 0.00003310000101919) * float3(opticalDepthR_0)  + float3((0.00002200000017183 * opticalDepthM_0)) )) * float3(1000.0) ;

#line 100
    }
    else
    {

#line 100
        color_1 = color_0;

#line 100
    }

#line 107
    return max(color_1, _S7);
}


#line 21 "Content/shaders/path_tracer.slang"
uint pcg_hash_0(uint input_0)
{

#line 22
    uint state_0 = input_0 * 747796405U + 2891336453U;
    uint word_0 = ((state_0 >> ((state_0 >> 28U) + 4U)) ^ state_0) * 277803737U;
    return (word_0 >> 22U) ^ word_0;
}

float rand_0(uint thread* seed_0)
{

#line 28
    uint _S15 = pcg_hash_0(*seed_0);

#line 28
    *seed_0 = _S15;
    return float(_S15) / 4.294967296e+09;
}


#line 73 "Content/shaders/disney_bsdf.slang"
struct DisneyMaterial_0
{
    float3 baseColor_0;
    float metallic_0;
    float subsurface_0;
    float specular_0;
    float roughness_0;
    float specularTint_0;
    float anisotropic_0;
    float sheen_0;
    float sheenTint_0;
    float clearcoat_0;
    float clearcoatGloss_0;
};


#line 73
struct Vertex_natural_0
{
    packed_float3 position_0;
    packed_float3 normal_0;
    packed_float2 uv_0;
    packed_float4 tangent_0;
};


#line 17 "Content/shaders/scene_data.slang"
struct PartData_natural_0
{
    packed_float4 aabbMin_0;
    packed_float4 aabbMax_0;
    Vertex_natural_0 device* vertices_0;
    uint device* indices_0;
    uint indexCount_0;
    uint materialIdx_0;
    uint instanceIdx_0;
    uint pad0_0;
};


#line 17
struct _MatrixStorage_float4x4_ColMajornatural_0
{
    array<packed_float4, int(4)> data_0;
};


#line 17
struct InstanceData_natural_0
{
    _MatrixStorage_float4x4_ColMajornatural_0 modelMatrix_0;
    packed_float4 aabbMin_1;
    packed_float4 aabbMax_1;
    uint partCount_0;
    uint firstPartIndex_0;
    uint pad1_0;
    uint pad2_0;
};


#line 17
struct MaterialData_natural_0
{
    packed_float4 baseColor_1;
    packed_float4 emissiveColor_0;
    float metallic_1;
    float roughness_1;
    uint albedoTexIndex_0;
    uint normalTexIndex_0;
    uint rmaTexIndex_0;
    uint emissiveTexIndex_0;
    uint _pad0_0;
    uint _pad1_0;
};


#line 17
struct CameraData_natural_0
{
    _MatrixStorage_float4x4_ColMajornatural_0 viewProj_0;
    _MatrixStorage_float4x4_ColMajornatural_0 invViewProj_0;
    packed_float4 cameraPosition_0;
};


#line 17
struct LightData_natural_0
{
    packed_float4 position_1;
    packed_float4 direction_0;
    packed_float4 color_2;
    packed_float4 spotParams_0;
};


#line 67
struct ScenePushData_0
{
    PartData_natural_0 device* parts_0;
    InstanceData_natural_0 device* instances_0;
    MaterialData_natural_0 device* materials_0;
    CameraData_natural_0 device* camera_0;
    LightData_natural_0 device* lights_0;
    uint lightCount_0;
    uint frameCount_0;
    float2 resolution_0;
    uint debugFlags_0;
    uint hasGeometry_0;
    SkyParams_0 sky_1;
};


#line 5420 "core.meta.slang"
struct _Array_default_Texture2D4096_0
{
    array<texture2d<float, access::sample>, int(4096)> data_1;
};


#line 5420
struct BindlessHeap_default_0
{
    _Array_default_Texture2D4096_0 textures_0;
};


#line 5420
struct KernelContext_0
{
    ScenePushData_0 constant* push_0;
    metal::raytracing::acceleration_structure<metal::raytracing::instancing> tlas_0;
    texture2d<float, access::read_write> outputBuffer_0;
    BindlessHeap_default_0 constant* bindless_0;
    sampler tex_sampler_0;
    texture2d<float, access::read_write> accumulationBuffer_0;
};


#line 41 "Content/shaders/path_tracer.slang"
DisneyMaterial_0 GetMaterial_0(uint instanceId_0, uint primitiveId_0, float2 barycentrics_0, float3 thread* hitNormal_0, float3 thread* hitTangent_0, float3 thread* hitBitangent_0, float3 thread* albedo_0, KernelContext_0 thread* kernelContext_0)
{

#line 41
    InstanceData_natural_0 device* _S16 = kernelContext_0->push_0->instances_0 + instanceId_0;

#line 41
    PartData_natural_0 device* _S17 = kernelContext_0->push_0->parts_0 + (*_S16).firstPartIndex_0;

#line 47
    PartData_natural_0 part_0 = *_S17;

    uint _S18 = primitiveId_0 * 3U;

#line 49
    Vertex_natural_0 device* _S19 = (*_S17).vertices_0 + *((*_S17).indices_0 + _S18);

#line 49
    Vertex_natural_0 device* _S20 = (*_S17).vertices_0 + *((*_S17).indices_0 + (_S18 + 1U));

#line 49
    Vertex_natural_0 device* _S21 = (*_S17).vertices_0 + *((*_S17).indices_0 + (_S18 + 2U));

#line 57
    float _S22 = barycentrics_0.x;

#line 57
    float _S23 = barycentrics_0.y;

#line 57
    float w_0 = 1.0 - _S22 - _S23;

#line 57
    float3 _S24 = float3(w_0) ;

#line 57
    float3 _S25 = float3(_S22) ;

#line 57
    float3 _S26 = float3(_S23) ;

#line 57
    float4 _S27 = float4((*_S19).tangent_0) ;


    float3 localTangent_0 = normalize(_S27.xyz * _S24 + (float4((*_S20).tangent_0) ).xyz * _S25 + (float4((*_S21).tangent_0) ).xyz * _S26);
    float tangentW_0 = _S27.w;
    float2 uv_1 = float2((*_S19).uv_0)  * float2(w_0)  + float2((*_S20).uv_0)  * float2(_S22)  + float2((*_S21).uv_0)  * float2(_S23) ;

#line 62
    matrix<float,int(4),int(4)>  _S28 = matrix<float,int(4),int(4)> ((*_S16).modelMatrix_0.data_0[int(0)][int(0)], (*_S16).modelMatrix_0.data_0[int(1)][int(0)], (*_S16).modelMatrix_0.data_0[int(2)][int(0)], (*_S16).modelMatrix_0.data_0[int(3)][int(0)], (*_S16).modelMatrix_0.data_0[int(0)][int(1)], (*_S16).modelMatrix_0.data_0[int(1)][int(1)], (*_S16).modelMatrix_0.data_0[int(2)][int(1)], (*_S16).modelMatrix_0.data_0[int(3)][int(1)], (*_S16).modelMatrix_0.data_0[int(0)][int(2)], (*_S16).modelMatrix_0.data_0[int(1)][int(2)], (*_S16).modelMatrix_0.data_0[int(2)][int(2)], (*_S16).modelMatrix_0.data_0[int(3)][int(2)], (*_S16).modelMatrix_0.data_0[int(0)][int(3)], (*_S16).modelMatrix_0.data_0[int(1)][int(3)], (*_S16).modelMatrix_0.data_0[int(2)][int(3)], (*_S16).modelMatrix_0.data_0[int(3)][int(3)]);

    matrix<float,int(3),int(3)>  _S29 = matrix<float,int(3),int(3)> (_S28[int(0)].xyz, _S28[int(1)].xyz, _S28[int(2)].xyz);

#line 64
    *hitNormal_0 = normalize((((normalize(float3((*_S19).normal_0)  * _S24 + float3((*_S20).normal_0)  * _S25 + float3((*_S21).normal_0)  * _S26)) * (_S29))));
    float3 _S30 = normalize((((localTangent_0) * (_S29))));

#line 65
    *hitTangent_0 = _S30;

#line 65
    float3 baseColor_2;

    if(!((dot(_S30, _S30)) > 0.00100000004749745))
    {

#line 68
        if((abs((*hitNormal_0).y)) < 0.99900001287460327)
        {

#line 68
            baseColor_2 = float3(0.0, 1.0, 0.0);

#line 68
        }
        else
        {

#line 68
            baseColor_2 = float3(1.0, 0.0, 0.0);

#line 68
        }
        *hitTangent_0 = normalize(cross(baseColor_2, *hitNormal_0));

#line 67
    }

#line 67
    float3 _S31 = float3(tangentW_0) ;



    float3 _S32 = cross(*hitNormal_0, *hitTangent_0) * _S31;

#line 71
    *hitBitangent_0 = _S32;
    if(!((dot(_S32, _S32)) > 0.00100000004749745))
    {

#line 73
        *hitBitangent_0 = cross(*hitNormal_0, *hitTangent_0);

#line 72
    }


    float3 _S33 = normalize(*hitBitangent_0);

#line 75
    *hitBitangent_0 = _S33;
    *hitTangent_0 = normalize(cross(_S33, *hitNormal_0));

#line 76
    MaterialData_natural_0 device* _S34 = kernelContext_0->push_0->materials_0 + part_0.materialIdx_0;

    MaterialData_natural_0 mat_0 = *_S34;

    float3 baseColor_3 = (float4((*_S34).baseColor_1) ).xyz;
    if(((*_S34).albedoTexIndex_0) != 4294967295U)
    {

#line 81
        baseColor_2 = baseColor_3 * (((&kernelContext_0->bindless_0->textures_0)->data_1[mat_0.albedoTexIndex_0]).sample((kernelContext_0->tex_sampler_0), (uv_1), level((0.0)))).xyz;

#line 81
    }
    else
    {

#line 81
        baseColor_2 = baseColor_3;

#line 81
    }


    *albedo_0 = baseColor_2;

#line 84
    float metallic_2;

#line 84
    float roughness_2;



    if((mat_0.rmaTexIndex_0) != 4294967295U)
    {

#line 89
        float3 rma_0 = (((&kernelContext_0->bindless_0->textures_0)->data_1[mat_0.rmaTexIndex_0]).sample((kernelContext_0->tex_sampler_0), (uv_1), level((0.0)))).xyz;
        float roughness_3 = mat_0.roughness_1 * rma_0.y;

#line 90
        metallic_2 = mat_0.metallic_1 * rma_0.z;

#line 90
        roughness_2 = roughness_3;

#line 88
    }
    else
    {

#line 88
        metallic_2 = mat_0.metallic_1;

#line 88
        roughness_2 = mat_0.roughness_1;

#line 88
    }

#line 94
    if((mat_0.normalTexIndex_0) != 4294967295U)
    {


        float3 _S35 = normalize((((matrix<float,int(3),int(3)> (*hitTangent_0, *hitBitangent_0, *hitNormal_0)) * ((((&kernelContext_0->bindless_0->textures_0)->data_1[mat_0.normalTexIndex_0]).sample((kernelContext_0->tex_sampler_0), (uv_1), level((0.0)))).xyz * float3(2.0)  - float3(1.0) ))));

#line 98
        *hitNormal_0 = _S35;

        float3 _S36 = cross(_S35, *hitTangent_0) * _S31;

#line 100
        *hitBitangent_0 = _S36;
        if(!((dot(_S36, _S36)) > 0.00100000004749745))
        {

#line 101
            *hitBitangent_0 = cross(*hitNormal_0, *hitTangent_0);

#line 101
        }
        float3 _S37 = normalize(*hitBitangent_0);

#line 102
        *hitBitangent_0 = _S37;
        *hitTangent_0 = normalize(cross(_S37, *hitNormal_0));

#line 94
    }

#line 106
    thread DisneyMaterial_0 dmat_0;
    (&dmat_0)->baseColor_0 = baseColor_2;
    (&dmat_0)->metallic_0 = metallic_2;
    (&dmat_0)->subsurface_0 = 0.0;
    (&dmat_0)->specular_0 = 0.5;
    (&dmat_0)->roughness_0 = max(roughness_2, 0.00999999977648258);
    (&dmat_0)->specularTint_0 = 0.0;
    (&dmat_0)->anisotropic_0 = 0.0;
    (&dmat_0)->sheen_0 = 0.0;
    (&dmat_0)->sheenTint_0 = 0.5;
    (&dmat_0)->clearcoat_0 = 0.0;
    (&dmat_0)->clearcoatGloss_0 = 1.0;

    return dmat_0;
}


#line 46 "Content/shaders/disney_bsdf.slang"
float3 DisneyDiffuse_0(float3 baseColor_4, float roughness_4, float dotNV_0, float dotNL_0, float dotLH_0, float subsurface_1)
{

#line 47
    float FL_0 = pow(1.0 - dotNL_0, 5.0);
    float FV_0 = pow(1.0 - dotNV_0, 5.0);


    float Fd90_0 = 0.5 + 2.0 * dotLH_0 * dotLH_0 * roughness_4;



    float Fss90_0 = dotLH_0 * dotLH_0 * roughness_4;



    return baseColor_4 / float3(3.14159274101257324)  * float3(mix(mix(1.0, Fd90_0, FL_0) * mix(1.0, Fd90_0, FV_0), 1.25 * (mix(1.0, Fss90_0, FL_0) * mix(1.0, Fss90_0, FV_0) * (1.0 / (dotNL_0 + dotNV_0 + 0.00000999999974738) - 0.5) + 0.5), subsurface_1))  * float3((1.0 - subsurface_1)) ;
}


#line 7
float sqr_0(float x_0)
{

#line 7
    return x_0 * x_0;
}


#line 15
float D_GGX_Aniso_0(float dotNH_0, float dotNX_0, float dotNY_0, float ax_0, float ay_0)
{

    return 1.0 / (3.14159274101257324 * (ax_0 * ay_0) * sqr_0(sqr_0(dotNX_0 / ax_0) + sqr_0(dotNY_0 / ay_0) + sqr_0(dotNH_0)) + 0.00000999999974738);
}


float G_SmithGGX_0(float dotNV_1, float dotNL_1, float alpha_0)
{

#line 23
    float a2_0 = alpha_0 * alpha_0;
    float _S38 = 1.0 - a2_0;

    return 0.5 / (dotNL_1 * sqrt(a2_0 + _S38 * dotNV_1 * dotNV_1) + dotNV_1 * sqrt(a2_0 + _S38 * dotNL_1 * dotNL_1) + 0.00000999999974738);
}


#line 10
float3 F_Schlick_0(float cosTheta_0, float3 F0_0)
{

#line 11
    return F0_0 + (float3(1.0)  - F0_0) * float3(pow(clamp(1.0 - cosTheta_0, 0.0, 1.0), 5.0)) ;
}


#line 63
float3 MultiScatterApprox_0(float3 F0_1, float roughness_5, float dotNV_2, float dotNL_2)
{

#line 70
    return F0_1 * float3(((1.0 - mix(1.0, 0.0, roughness_5)) * roughness_5 * roughness_5))  / float3(3.14159274101257324) ;
}


#line 30
float D_GTR1_0(float dotNH_1, float alpha_1)
{

#line 31
    if(alpha_1 >= 1.0)
    {

#line 31
        return 0.31830987334251404;
    }

#line 32
    float a2_1 = alpha_1 * alpha_1;
    float _S39 = a2_1 - 1.0;
    return _S39 / (3.14159274101257324 * log(a2_1) * (1.0 + _S39 * dotNH_1 * dotNH_1) + 0.00000999999974738);
}


float G_SmithGGX_Clearcoat_0(float dotNV_3, float dotNL_3)
{


    return 0.5 / (dotNL_3 * sqrt(0.0625 + 0.9375 * dotNV_3 * dotNV_3) + dotNV_3 * sqrt(0.0625 + 0.9375 * dotNL_3 * dotNL_3) + 0.00000999999974738);
}


#line 88
float3 EvaluateDisneyBSDF_0(const DisneyMaterial_0 thread* mat_1, float3 N_0, float3 V_0, float3 L_0, float3 X_0, float3 Y_0)
{

#line 89
    float3 H_0 = normalize(L_0 + V_0);
    float dotNL_4 = clamp(dot(N_0, L_0), 0.00100000004749745, 1.0);
    float dotNV_4 = clamp(dot(N_0, V_0), 0.00100000004749745, 1.0);
    float dotNH_2 = clamp(dot(N_0, H_0), 0.00100000004749745, 1.0);
    float dotLH_1 = clamp(dot(L_0, H_0), 0.00100000004749745, 1.0);

#line 93
    float3 _S40 = mat_1->baseColor_0;

    float luminance_0 = dot(mat_1->baseColor_0, float3(0.30000001192092896, 0.60000002384185791, 0.10000000149011612));

#line 95
    float3 Ctint_0;
    if(luminance_0 > 0.0)
    {

#line 96
        Ctint_0 = _S40 / float3(luminance_0) ;

#line 96
    }
    else
    {

#line 96
        Ctint_0 = float3(1.0) ;

#line 96
    }
    float3 _S41 = float3(1.0) ;

#line 97
    float3 _S42 = float3(mat_1->metallic_0) ;

#line 97
    float3 Cspec0_0 = mix(float3((mat_1->specular_0 * 0.07999999821186066))  * mix(_S41, Ctint_0, float3(mat_1->specularTint_0) ), _S40, _S42);

#line 97
    float3 _S43 = float3((1.0 - mat_1->metallic_0)) ;

#line 105
    float FH_0 = pow(clamp(1.0 - dotLH_1, 0.0, 1.0), 5.0);



    float aspect_0 = sqrt(1.0 - mat_1->anisotropic_0 * 0.89999997615814209);
    float _S44 = sqr_0(mat_1->roughness_0);

#line 127
    return DisneyDiffuse_0(_S40, mat_1->roughness_0, dotNV_4, dotNL_4, dotLH_1, mat_1->subsurface_0) * _S43 + float3((FH_0 * mat_1->sheen_0))  * mix(_S41, Ctint_0, float3(mat_1->sheenTint_0) ) * _S43 + (float3((D_GGX_Aniso_0(dotNH_2, dot(X_0, H_0), dot(Y_0, H_0), max(0.00100000004749745, _S44 / aspect_0), max(0.00100000004749745, _S44 * aspect_0)) * G_SmithGGX_0(dotNV_4, dotNL_4, mat_1->roughness_0)))  * F_Schlick_0(dotLH_1, Cspec0_0) + MultiScatterApprox_0(Cspec0_0, mat_1->roughness_0, dotNV_4, dotNL_4) * _S42) + float3((mat_1->clearcoat_0 * D_GTR1_0(dotNH_2, mix(0.10000000149011612, 0.00100000004749745, mat_1->clearcoatGloss_0)) * G_SmithGGX_Clearcoat_0(dotNV_4, dotNL_4) * mix(0.03999999910593033, 1.0, FH_0))) ;
}


#line 143
float3 SampleGGX_0(float u1_0, float u2_0, float roughness_6, float3 N_1, float3 T_0, float3 B_0)
{

#line 144
    float a_0 = roughness_6 * roughness_6;
    float phi_0 = 6.28318548202514648 * u1_0;
    float cosTheta_1 = sqrt((1.0 - u2_0) / (1.0 + (a_0 * a_0 - 1.0) * u2_0));
    float sinTheta_0 = sqrt(max(0.0, 1.0 - cosTheta_1 * cosTheta_1));

    return normalize(float3((sinTheta_0 * cos(phi_0)))  * T_0 + float3((sinTheta_0 * sin(phi_0)))  * B_0 + float3(cosTheta_1)  * N_1);
}


#line 158
float PdfGGX_0(float dotNH_3, float dotNH_V_0, float roughness_7)
{

#line 159
    float a_1 = roughness_7 * roughness_7;
    float _S45 = a_1 * a_1;

#line 160
    float d_1 = dotNH_3 * dotNH_3 * (_S45 - 1.0) + 1.0;

    return _S45 / (3.14159274101257324 * d_1 * d_1) * dotNH_3 / (4.0 * dotNH_V_0 + 0.00000999999974738);
}


#line 153
float PdfCosineHemisphere_0(float dotNL_5)
{

#line 154
    return dotNL_5 / 3.14159274101257324;
}


#line 135
float3 SampleCosineHemisphere_0(float u1_1, float u2_1, float3 N_2, float3 T_1, float3 B_1)
{

#line 136
    float r_0 = sqrt(u1_1);
    float phi_1 = 6.28318548202514648 * u2_1;

    return normalize(float3((r_0 * cos(phi_1)))  * T_1 + float3((r_0 * sin(phi_1)))  * B_1 + float3(sqrt(max(0.0, 1.0 - u1_1)))  * N_2);
}


#line 167
float3 SampleDisneyBSDF_0(const DisneyMaterial_0 thread* mat_2, float3 N_3, float3 V_1, float3 T_2, float3 B_2, float2 rnd_0, float thread* pdf_0)
{

#line 167
    thread float2 _S46 = rnd_0;
    float specularWeight_0 = mix(0.5, 1.0, mat_2->metallic_0);

    if((rnd_0.x) < specularWeight_0)
    {
        _S46.x = _S46.x / specularWeight_0;
        float3 H_1 = SampleGGX_0(_S46.x, _S46.y, mat_2->roughness_0, N_3, T_2, B_2);
        float3 L_1 = reflect(- V_1, H_1);

#line 183
        *pdf_0 = mix(PdfCosineHemisphere_0(clamp(dot(N_3, L_1), 0.00100000004749745, 1.0)), PdfGGX_0(clamp(dot(N_3, H_1), 0.00100000004749745, 1.0), clamp(dot(V_1, H_1), 0.00100000004749745, 1.0), mat_2->roughness_0), specularWeight_0);
        return L_1;
    }
    else
    {

#line 187
        _S46.x = (_S46.x - specularWeight_0) / (1.0 - specularWeight_0);
        float3 L_2 = SampleCosineHemisphere_0(_S46.x, _S46.y, N_3, T_2, B_2);

        float3 H_2 = normalize(L_2 + V_1);

#line 198
        *pdf_0 = mix(PdfCosineHemisphere_0(clamp(dot(N_3, L_2), 0.00100000004749745, 1.0)), PdfGGX_0(clamp(dot(N_3, H_2), 0.00100000004749745, 1.0), clamp(dot(V_1, H_2), 0.00100000004749745, 1.0), mat_2->roughness_0), specularWeight_0);
        return L_2;
    }

#line 199
}


#line 19476 "hlsl.meta.slang"
struct RayDesc_0
{
    float3 Origin_0;
    float TMin_0;
    float3 Direction_0;
    float TMax_0;
};


#line 124 "Content/shaders/path_tracer.slang"
[[kernel]] void computeMain(uint3 dispatchThreadID_0 [[thread_position_in_grid]], ScenePushData_0 constant* push_1 [[buffer(0)]], metal::raytracing::acceleration_structure<metal::raytracing::instancing> tlas_1 [[buffer(2)]], texture2d<float, access::read_write> outputBuffer_1 [[texture(1)]], BindlessHeap_default_0 constant* bindless_1 [[buffer(1)]], sampler tex_sampler_1 [[sampler(0)]], texture2d<float, access::read_write> accumulationBuffer_1 [[texture(0)]])
{

#line 124
    float3 oldColor_0;

#line 124
    bool _S47;

#line 124
    float3 L_3;

#line 124
    thread KernelContext_0 kernelContext_1;

#line 124
    (&kernelContext_1)->push_0 = push_1;

#line 124
    (&kernelContext_1)->tlas_0 = tlas_1;

#line 124
    (&kernelContext_1)->outputBuffer_0 = outputBuffer_1;

#line 124
    (&kernelContext_1)->bindless_0 = bindless_1;

#line 124
    (&kernelContext_1)->tex_sampler_0 = tex_sampler_1;

#line 124
    (&kernelContext_1)->accumulationBuffer_0 = accumulationBuffer_1;
    uint _S48 = dispatchThreadID_0.x;

#line 125
    bool _S49;

#line 125
    if(_S48 >= uint(push_1->resolution_0.x))
    {

#line 125
        _S49 = true;

#line 125
    }
    else
    {

#line 125
        _S49 = (dispatchThreadID_0.y) >= uint(push_1->resolution_0.y);

#line 125
    }

#line 125
    if(_S49)
    {

#line 125
        return;
    }



    if((((&kernelContext_1)->push_0->debugFlags_0) & 1U) != 0U)
    {

#line 131
        uint2 _S50 = dispatchThreadID_0.xy;
        float2 _S51 = (float2(_S50) + float2(0.5) ) / push_1->resolution_0 * float2(2.0)  - float2(1.0) ;

#line 132
        thread float2 ndc_0 = _S51;
        ndc_0.y = - _S51.y;

        float3 rayOrigin_0 = (float4((&kernelContext_1)->push_0->camera_0->cameraPosition_0) ).xyz;
        float4 target_0 = (((float4(ndc_0.x, ndc_0.y, 1.0, 1.0)) * (matrix<float,int(4),int(4)> ((&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(0)][int(0)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(1)][int(0)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(2)][int(0)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(3)][int(0)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(0)][int(1)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(1)][int(1)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(2)][int(1)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(3)][int(1)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(0)][int(2)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(1)][int(2)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(2)][int(2)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(3)][int(2)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(0)][int(3)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(1)][int(3)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(2)][int(3)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(3)][int(3)]))));

        float3 rayDir_0 = normalize(target_0.xyz / float3(target_0.w)  - rayOrigin_0);
        thread RayDesc_0 dbgRay_0;
        (&dbgRay_0)->Origin_0 = rayOrigin_0;
        (&dbgRay_0)->TMin_0 = 0.00100000004749745;
        (&dbgRay_0)->Direction_0 = rayDir_0;
        (&dbgRay_0)->TMax_0 = 10000.0;

        thread raytracing::intersection_query<raytracing::triangle_data, raytracing::instancing> dbgQ_0;
        if(((&kernelContext_1)->push_0->hasGeometry_0) != 0U)
        {

#line 146
            (&dbgQ_0)->reset({ (dbgRay_0.Origin_0), (dbgRay_0.Direction_0), (dbgRay_0.TMin_0), (dbgRay_0.TMax_0) }, ((&kernelContext_1)->tlas_0), (255U), _slang_ray_flags_to_intersection_params((4U)));
            bool _S52 = ((&dbgQ_0)->next());

#line 146
        }


        thread float3 dbgColor_0;
        if(((&kernelContext_1)->push_0->hasGeometry_0) != 0U)
        {

#line 150
            uint _S53 = (_slang_committed_intersection_type_to_uint((&dbgQ_0)->get_committed_intersection_type()));

#line 150
            _S49 = _S53 == 1U;

#line 150
        }
        else
        {

#line 150
            _S49 = false;

#line 150
        }

#line 150
        if(_S49)
        {

#line 151
            float t_0 = ((&dbgQ_0)->get_committed_distance());

            float _S54 = 1.0 - saturate(t_0 / 20.0);

#line 153
            dbgColor_0 = float3(_S54, _S54, _S54);


            uint instId_0 = ((&dbgQ_0)->get_committed_user_instance_id());
            if((instId_0 & 1U) != 0U)
            {

#line 157
                dbgColor_0.xz = dbgColor_0.xz * float2(0.80000001192092896) ;

#line 157
            }
            if((instId_0 & 2U) != 0U)
            {

#line 158
                dbgColor_0.yz = dbgColor_0.yz * float2(0.80000001192092896) ;

#line 158
            }

#line 150
        }
        else
        {

#line 150
            float3 _S55 = GetSkyRadiance_0(rayDir_0, (&(&kernelContext_1)->push_0->sky_1)->sunDirection_0, &(&kernelContext_1)->push_0->sky_1);

#line 160
            dbgColor_0 = _S55;

#line 150
        }

#line 162
        (&kernelContext_1)->outputBuffer_0.write(float4(dbgColor_0, 1.0),_S50);
        return;
    }


    thread uint seed_1 = dispatchThreadID_0.y * uint(push_1->resolution_0.x) + _S48 + (&kernelContext_1)->push_0->frameCount_0 * 719393U;

    uint2 _S56 = dispatchThreadID_0.xy;

#line 169
    float2 _S57 = float2(_S56);

#line 169
    float _S58 = rand_0(&seed_1);

#line 169
    float _S59 = rand_0(&seed_1);

    float2 _S60 = (_S57 + float2(_S58, _S59)) / push_1->resolution_0 * float2(2.0)  - float2(1.0) ;

#line 171
    thread float2 ndc_1 = _S60;
    ndc_1.y = - _S60.y;

    float3 rayOrigin_1 = (float4((&kernelContext_1)->push_0->camera_0->cameraPosition_0) ).xyz;
    float4 target_1 = (((float4(ndc_1.x, ndc_1.y, 1.0, 1.0)) * (matrix<float,int(4),int(4)> ((&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(0)][int(0)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(1)][int(0)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(2)][int(0)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(3)][int(0)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(0)][int(1)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(1)][int(1)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(2)][int(1)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(3)][int(1)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(0)][int(2)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(1)][int(2)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(2)][int(2)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(3)][int(2)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(0)][int(3)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(1)][int(3)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(2)][int(3)], (&kernelContext_1)->push_0->camera_0->invViewProj_0.data_0[int(3)][int(3)]))));

    float3 rayDir_1 = normalize(target_1.xyz / float3(target_1.w)  - rayOrigin_1);

    float3 _S61 = float3(1.0) ;
    float3 _S62 = float3(0.0) ;

    thread RayDesc_0 ray_0;
    (&ray_0)->Origin_0 = rayOrigin_1;
    (&ray_0)->TMin_0 = 0.00100000004749745;
    (&ray_0)->Direction_0 = rayDir_1;
    (&ray_0)->TMax_0 = 10000.0;

#line 186
    int bounce_0 = int(0);

#line 186
    float3 throughput_0 = _S61;

#line 186
    float3 radiance_0 = _S62;



    for(;;)
    {

#line 190
        if(bounce_0 < int(4))
        {
        }
        else
        {

#line 190
            break;
        }

#line 191
        thread raytracing::intersection_query<raytracing::triangle_data, raytracing::instancing> q_0;
        if(((&kernelContext_1)->push_0->hasGeometry_0) != 0U)
        {

#line 192
            (&q_0)->reset({ (ray_0.Origin_0), (ray_0.Direction_0), (ray_0.TMin_0), (ray_0.TMax_0) }, ((&kernelContext_1)->tlas_0), (255U), _slang_ray_flags_to_intersection_params((0U)));
            bool _S63 = ((&q_0)->next());

#line 192
        }


        if(((&kernelContext_1)->push_0->hasGeometry_0) != 0U)
        {

#line 195
            uint _S64 = (_slang_committed_intersection_type_to_uint((&q_0)->get_committed_intersection_type()));

#line 195
            _S49 = _S64 == 1U;

#line 195
        }
        else
        {

#line 195
            _S49 = false;

#line 195
        }

#line 195
        float3 radiance_1;

#line 195
        if(_S49)
        {

#line 196
            uint instId_1 = ((&q_0)->get_committed_user_instance_id());
            uint primId_0 = ((&q_0)->get_committed_primitive_id());
            float2 bary_0 = ((&q_0)->get_committed_triangle_barycentric_coord());
            float t_1 = ((&q_0)->get_committed_distance());

            float3 hitPos_0 = (&ray_0)->Origin_0 + (&ray_0)->Direction_0 * float3(t_1) ;
            thread float3 N_4;

#line 202
            thread float3 T_3;

#line 202
            thread float3 B_3;

#line 202
            thread float3 albedo_1;

#line 202
            DisneyMaterial_0 _S65 = GetMaterial_0(instId_1, primId_0, bary_0, &N_4, &T_3, &B_3, &albedo_1, &kernelContext_1);

#line 202
            uint i_1 = 0U;

#line 202
            oldColor_0 = _S62;

#line 208
            for(;;)
            {

#line 208
                if(i_1 < ((&kernelContext_1)->push_0->lightCount_0))
                {
                }
                else
                {

#line 208
                    break;
                }

#line 208
                LightData_natural_0 device* _S66 = (&kernelContext_1)->push_0->lights_0 + i_1;
                LightData_natural_0 light_0 = *_S66;

#line 209
                float4 _S67 = float4((*_S66).direction_0) ;

#line 214
                bool _S68 = (_S67.w) == 0.0;

#line 214
                float attenuation_1;

#line 214
                if(_S68)
                {

#line 214
                    L_3 = normalize(_S67.xyz);

#line 214
                    attenuation_1 = 1.0;

#line 214
                }
                else
                {

#line 214
                    float4 _S69 = float4(light_0.position_1) ;


                    float3 delta_0 = _S69.xyz - hitPos_0;
                    float dist_0 = length(delta_0);


                    float falloff_0 = clamp(1.0 - pow(dist_0 / _S69.w, 4.0), 0.0, 1.0);
                    float _S70 = falloff_0 * falloff_0 / (dist_0 * dist_0 + 1.0);

#line 222
                    L_3 = delta_0 / float3(dist_0) ;

#line 222
                    attenuation_1 = _S70;

#line 214
                }

#line 226
                thread RayDesc_0 shadowRay_0;
                (&shadowRay_0)->Origin_0 = hitPos_0 + N_4 * float3(0.00100000004749745) ;
                (&shadowRay_0)->Direction_0 = L_3;
                (&shadowRay_0)->TMin_0 = 0.00100000004749745;

#line 229
                float _S71;
                if(_S68)
                {

#line 230
                    _S71 = 10000.0;

#line 230
                }
                else
                {

#line 230
                    _S71 = length((float4(light_0.position_1) ).xyz - hitPos_0) - 0.00100000004749745;

#line 230
                }

#line 230
                (&shadowRay_0)->TMax_0 = _S71;

                thread raytracing::intersection_query<raytracing::triangle_data, raytracing::instancing> sq_0;
                if(((&kernelContext_1)->push_0->hasGeometry_0) != 0U)
                {

#line 233
                    (&sq_0)->reset({ (shadowRay_0.Origin_0), (shadowRay_0.Direction_0), (shadowRay_0.TMin_0), (shadowRay_0.TMax_0) }, ((&kernelContext_1)->tlas_0), (255U), _slang_ray_flags_to_intersection_params((4U)));
                    bool _S72 = ((&sq_0)->next());

#line 233
                }


                if(((&kernelContext_1)->push_0->hasGeometry_0) == 0U)
                {

#line 236
                    _S47 = true;

#line 236
                }
                else
                {

#line 236
                    uint _S73 = (_slang_committed_intersection_type_to_uint((&sq_0)->get_committed_intersection_type()));

#line 236
                    _S47 = _S73 != 1U;

#line 236
                }

#line 236
                if(_S47)
                {

#line 237
                    float3 _S74 = - (&ray_0)->Direction_0;

#line 237
                    thread DisneyMaterial_0 _S75 = _S65;

#line 237
                    float3 _S76 = EvaluateDisneyBSDF_0(&_S75, N_4, _S74, L_3, T_3, B_3);

#line 237
                    float4 _S77 = float4(light_0.color_2) ;

#line 237
                    oldColor_0 = oldColor_0 + _S76 * _S77.xyz * float3(_S77.w)  * float3(attenuation_1)  * float3(clamp(dot(N_4, L_3), 0.00100000004749745, 1.0)) ;

#line 236
                }

#line 208
                i_1 = i_1 + 1U;

#line 208
            }

#line 243
            float3 radiance_2 = radiance_0 + throughput_0 * oldColor_0;

#line 243
            MaterialData_natural_0 device* _S78 = (&kernelContext_1)->push_0->materials_0 + ((&kernelContext_1)->push_0->parts_0 + ((&kernelContext_1)->push_0->instances_0 + instId_1)->firstPartIndex_0)->materialIdx_0;


            MaterialData_natural_0 mdata_0 = *_S78;
            float3 emissive_0 = (float4((*_S78).emissiveColor_0) ).xyz;
            if(((*_S78).emissiveTexIndex_0) != 4294967295U)
            {
                float _S79 = bary_0.x;

#line 250
                float _S80 = bary_0.y;
                uint _S81 = primId_0 * 3U;

#line 251
                L_3 = emissive_0 * (((&(&kernelContext_1)->bindless_0->textures_0)->data_1[mdata_0.emissiveTexIndex_0]).sample(((&kernelContext_1)->tex_sampler_0), (float2((((&kernelContext_1)->push_0->parts_0 + ((&kernelContext_1)->push_0->instances_0 + instId_1)->firstPartIndex_0)->vertices_0 + *(((&kernelContext_1)->push_0->parts_0 + ((&kernelContext_1)->push_0->instances_0 + instId_1)->firstPartIndex_0)->indices_0 + _S81))->uv_0)  * float2((1.0 - _S79 - _S80))  + float2((((&kernelContext_1)->push_0->parts_0 + ((&kernelContext_1)->push_0->instances_0 + instId_1)->firstPartIndex_0)->vertices_0 + *(((&kernelContext_1)->push_0->parts_0 + ((&kernelContext_1)->push_0->instances_0 + instId_1)->firstPartIndex_0)->indices_0 + (_S81 + 1U)))->uv_0)  * float2(_S79)  + float2((((&kernelContext_1)->push_0->parts_0 + ((&kernelContext_1)->push_0->instances_0 + instId_1)->firstPartIndex_0)->vertices_0 + *(((&kernelContext_1)->push_0->parts_0 + ((&kernelContext_1)->push_0->instances_0 + instId_1)->firstPartIndex_0)->indices_0 + (_S81 + 2U)))->uv_0)  * float2(_S80) ), level((0.0)))).xyz;

#line 248
            }
            else
            {

#line 248
                L_3 = emissive_0;

#line 248
            }

#line 256
            if(bounce_0 == int(0))
            {

#line 256
                radiance_1 = radiance_2 + throughput_0 * L_3;

#line 256
            }
            else
            {

#line 256
                radiance_1 = radiance_2;

#line 256
            }

#line 262
            float3 _S82 = - (&ray_0)->Direction_0;

#line 262
            float _S83 = rand_0(&seed_1);

#line 262
            float _S84 = rand_0(&seed_1);

#line 262
            float2 _S85 = float2(_S83, _S84);

#line 262
            thread DisneyMaterial_0 _S86 = _S65;

#line 261
            thread float pdf_1;

#line 261
            float3 _S87 = SampleDisneyBSDF_0(&_S86, N_4, _S82, T_3, B_3, _S85, &pdf_1);


            if(pdf_1 <= 0.0)
            {

#line 264
                radiance_0 = radiance_1;

#line 264
                break;
            }
            float3 _S88 = - (&ray_0)->Direction_0;

#line 266
            thread DisneyMaterial_0 _S89 = _S65;

#line 266
            float3 _S90 = EvaluateDisneyBSDF_0(&_S89, N_4, _S88, _S87, T_3, B_3);


            float3 throughput_1 = throughput_0 * (_S90 * float3(clamp(dot(N_4, _S87), 0.0, 1.0))  / float3(pdf_1) );


            float _S91 = max(throughput_1.x, max(throughput_1.y, throughput_1.z));
            if(!(_S91 > 0.0))
            {

#line 273
                radiance_0 = radiance_1;

#line 273
                break;
            }

            if(bounce_0 > int(2))
            {

#line 277
                float _S92 = min(_S91, 0.94999998807907104);
                if(_S92 < 0.00100000004749745)
                {

#line 278
                    _S47 = true;

#line 278
                }
                else
                {

#line 278
                    float _S93 = rand_0(&seed_1);

#line 278
                    _S47 = _S93 > _S92;

#line 278
                }

#line 278
                if(_S47)
                {

#line 278
                    radiance_0 = radiance_1;

#line 278
                    break;
                }

#line 278
                throughput_0 = throughput_1 / float3(_S92) ;

#line 276
            }
            else
            {

#line 276
                throughput_0 = throughput_1;

#line 276
            }

#line 282
            (&ray_0)->Origin_0 = hitPos_0 + N_4 * float3(0.00100000004749745) ;
            (&ray_0)->Direction_0 = _S87;
            (&ray_0)->TMax_0 = 10000.0;

#line 195
        }
        else
        {

#line 195
            float3 _S94 = GetSkyRadiance_0(rayDir_1, (&(&kernelContext_1)->push_0->sky_1)->sunDirection_0, &(&kernelContext_1)->push_0->sky_1);

#line 195
            radiance_0 = radiance_0 + throughput_0 * _S94;

#line 288
            break;
        }

#line 190
        bounce_0 = bounce_0 + int(1);

#line 190
        radiance_0 = radiance_1;

#line 190
    }

#line 293
    if(((&kernelContext_1)->push_0->frameCount_0) == 0U)
    {

#line 293
        oldColor_0 = float3(0.0, 0.0, 0.0);

#line 293
    }
    else
    {

#line 293
        float4 _S95 = (((&kernelContext_1)->accumulationBuffer_0).read(vec<uint,2>(((int2(_S56))).xy)));

#line 293
        oldColor_0 = _S95.xyz;

#line 293
    }
    float3 newColor_0 = mix(oldColor_0, radiance_0, float3((1.0 / float((&kernelContext_1)->push_0->frameCount_0 + 1U))) );
    (&kernelContext_1)->accumulationBuffer_0.write(float4(newColor_0, 1.0),_S56);



    float3 exposed_0 = newColor_0 * float3((float4((&kernelContext_1)->push_0->camera_0->cameraPosition_0) ).w) ;

#line 309
    (&kernelContext_1)->outputBuffer_0.write(float4(pow(max(clamp(exposed_0 * (float3(2.50999999046325684)  * exposed_0 + float3(0.02999999932944775) ) / (exposed_0 * (float3(2.43000006675720215)  * exposed_0 + float3(0.5899999737739563) ) + float3(0.14000000059604645) ), _S62, _S61), _S62), float3(0.45454543828964233, 0.45454543828964233, 0.45454543828964233)), 1.0),_S56);
    return;
}

