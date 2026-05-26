Shader "Custom/OpaqueLitCrosshatchShadowReceiver"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)

        _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0, 2)) = 1

        _Smoothness ("Smoothness", Range(0, 1)) = 0.35
        _SpecularStrength ("Specular Strength", Range(0, 2)) = 0.25
        _AmbientStrength ("Ambient Strength", Range(0, 2)) = 1

        _UseCrosshatch ("Use Crosshatch Shadows", Range(0, 1)) = 0
        _CrosshatchTexture ("Crosshatch Texture", 2D) = "white" {}
        _CrosshatchRepetition ("Crosshatch Repetition", Float) = 10
        _HatchStrength ("Hatch Strength", Range(0, 1)) = 1
        _ShadowColor ("Crosshatch Shadow Color", Color) = (0,0,0,1)
        _InvertHatchTexture ("Invert Hatch Texture", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual
            Blend One Zero

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            #pragma multi_compile_fog

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            TEXTURE2D(_CrosshatchTexture);
            SAMPLER(sampler_CrosshatchTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float4 _CrosshatchTexture_ST;

                float4 _BaseColor;

                float _BumpScale;

                float _Smoothness;
                float _SpecularStrength;
                float _AmbientStrength;

                float _UseCrosshatch;
                float _CrosshatchRepetition;
                float _HatchStrength;
                float4 _ShadowColor;
                float _InvertHatchTexture;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                float4 screenPos : TEXCOORD4;
                float2 normalizedScreenSpaceUV : TEXCOORD5;
                float fogCoord : TEXCOORD6;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.tangentWS = float4(normalInputs.tangentWS, input.tangentOS.w);
                output.uv = input.uv;
                output.screenPos = ComputeScreenPos(output.positionCS);
                output.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(output.positionCS);
                output.fogCoord = ComputeFogFactor(output.positionCS.z);

                return output;
            }

            float3 TangentNormalToWorld(float3 normalTS, float3 normalWS, float4 tangentWS)
            {
                float3 n = normalize(normalWS);
                float3 t = normalize(tangentWS.xyz);

                float tangentSign = tangentWS.w * GetOddNegativeScale();
                float3 b = cross(n, t) * tangentSign;

                float3x3 tangentToWorld = float3x3(t, b, n);
                return normalize(mul(normalTS, tangentToWorld));
            }

            float3 SampleSurfaceNormalWS(float2 uv, float3 normalWS, float4 tangentWS)
            {
                float2 normalUV = TRANSFORM_TEX(uv, _BumpMap);
                float4 packedNormal = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, normalUV);
                float3 normalTS = UnpackNormalScale(packedNormal, _BumpScale);
                return TangentNormalToWorld(normalTS, normalWS, tangentWS);
            }

            float GetScreenSpaceHatch(float2 screenUV)
            {
                float aspect = _ScreenParams.x / max(_ScreenParams.y, 0.0001);

                float2 hatchUV = screenUV;
                hatchUV.x *= aspect;
                hatchUV *= _CrosshatchRepetition;

                float hatch = SAMPLE_TEXTURE2D(
                    _CrosshatchTexture,
                    sampler_CrosshatchTexture,
                    hatchUV
                ).r;

                if (_InvertHatchTexture > 0.5)
                {
                    hatch = 1.0 - hatch;
                }

                return saturate(hatch);
            }

            float3 CalculateDirectLight(
                Light light,
                float3 albedo,
                float3 normalWS,
                float3 viewDirWS,
                float shadowMultiplier,
                float directAO
            )
            {
                float3 lightDirWS = normalize(light.direction);

                float ndotl = saturate(dot(normalWS, lightDirWS));
                float3 diffuse = albedo * light.color * ndotl;

                float3 halfDirWS = normalize(lightDirWS + viewDirWS);
                float ndoth = saturate(dot(normalWS, halfDirWS));

                float specPower = lerp(8.0, 128.0, _Smoothness);
                float specular = pow(ndoth, specPower) * _SpecularStrength;

                float attenuation = light.distanceAttenuation * shadowMultiplier;

                return (diffuse + specular * light.color) * attenuation * directAO;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 baseUV = TRANSFORM_TEX(input.uv, _BaseMap);
                float4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, baseUV);

                float3 albedo = baseSample.rgb * _BaseColor.rgb;

                float3 geometricNormalWS = normalize(input.normalWS);
                float3 normalWS = SampleSurfaceNormalWS(input.uv, geometricNormalWS, input.tangentWS);
                float3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(input.normalizedScreenSpaceUV);
                float directAO = aoFactor.directAmbientOcclusion;
                float indirectAO = aoFactor.indirectAmbientOcclusion;

                float3 color = 0;

                color += SampleSH(normalWS) * albedo * _AmbientStrength * indirectAO;

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                float normalShadow = mainLight.shadowAttenuation;
                float finalMainShadowMultiplier = normalShadow;
                float crosshatchShadowColorMask = 0.0;

                if (_UseCrosshatch > 0.5)
                {
                    float shadowAmount = 1.0 - normalShadow;

                    float2 screenUV = input.screenPos.xy / input.screenPos.w;
                    float hatch = GetScreenSpaceHatch(screenUV);

                    float hatchDarken = lerp(
                        1.0,
                        1.0 - _HatchStrength,
                        hatch
                    );

                    // Replace normal shadow attenuation with crosshatch darkening.
                    // The shadow map only decides where the hatching is allowed to appear.
                    finalMainShadowMultiplier = lerp(
                        1.0,
                        hatchDarken,
                        shadowAmount
                    );

                    // Separate color mask for the received cast-shadow hatch strokes.
                    // This affects the final shaded result, including ambient light,
                    // so shadows do not inherit a blue-ish ambient tint unless you choose that color.
                    crosshatchShadowColorMask = saturate(shadowAmount * hatch * _HatchStrength * _ShadowColor.a);
                }

                color += CalculateDirectLight(
                    mainLight,
                    albedo,
                    normalWS,
                    viewDirWS,
                    finalMainShadowMultiplier,
                    directAO
                );

                #ifdef _ADDITIONAL_LIGHTS
                uint additionalLightCount = GetAdditionalLightsCount();

                for (uint i = 0u; i < additionalLightCount; i++)
                {
                    Light light = GetAdditionalLight(i, input.positionWS);

                    color += CalculateDirectLight(
                        light,
                        albedo,
                        normalWS,
                        viewDirWS,
                        light.shadowAttenuation,
                        directAO
                    );
                }
                #endif

                if (_UseCrosshatch > 0.5)
                {
                    color = lerp(color, _ShadowColor.rgb, crosshatchShadowColorMask);
                }

                color = MixFog(color, input.fogCoord);

                return float4(color, 1.0);
            }

            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM

            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma target 3.0
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            float4 GetShadowPositionHClip(Attributes input)
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                float3 lightDirectionWS = _LightDirection;

                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    lightDirectionWS = normalize(_LightPosition - positionWS);
                #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS)
                );

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return positionCS;
            }

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;
                output.positionCS = GetShadowPositionHClip(input);
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_TARGET
            {
                return 0;
            }

            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM

            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_TARGET
            {
                return 0;
            }

            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM

            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 DepthNormalsFragment(Varyings input) : SV_TARGET
            {
                float3 normalWS = normalize(input.normalWS);

                // Match URP Lit-style DepthNormals output for modern URP:
                // write signed world-space normals, not remapped 0..1 normals.
                // Remapping to 0..1 makes SSAO interpret the surface normals incorrectly,
                // which can make the whole object look occluded.
                return half4(normalWS, 0.0);
            }

            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
