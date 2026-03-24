#ifndef STYLIZATIONINCLUDE
#define STYLIZATIONINCLUDE

// 3x3 sample points
static float2 sobelSamplePoints[9] =
{
    float2(-1, 1), float2(0, 1), float2(1, 1),
    float2(-1, 0), float2(0, 0), float2(1, 0),
    float2(-1, -1), float2(0, -1), float2(1, -1)
};
    
static float sobelXKernel[9] =
{
    1, 0, -1,
    2, 0, -2,
    1, 0, -1
};
    
static float sobelYKernel[9] =
{
    1, 2, 1,
    0, 0, 0,
    -1, -2, -1
};

void SobelEdgeSample_float(float4 UV, float2 TexelSize, float depthSensitivity, float normalSensitivity, float3 viewDir, out float Edge, out float angle)
{
    float SobelX = 0;
    float SobelY = 0;
    
    
    float SobelNormalX = 0;
    float SobelNormalY = 0;
    
    float3 middleNormal = SHADERGRAPH_SAMPLE_SCENE_NORMAL(UV);
    angle = dot(middleNormal, viewDir);
    [unroll]
    for (int i = 0; i < 9; i++)
    {
        float3 normal = SHADERGRAPH_SAMPLE_SCENE_NORMAL(float4(UV.xy + sobelSamplePoints[i] * TexelSize.xy, UV.zw));
       
        //float faceingcamera = dot(normal, viewDir);
        
        
        float depth = SHADERGRAPH_SAMPLE_SCENE_DEPTH(float4(UV.xy + sobelSamplePoints[i] * TexelSize.xy * smoothstep(0.95,0.99,angle), UV.zw));
        
        
        SobelX += depth * sobelXKernel[i] * depthSensitivity;
        SobelY += depth * sobelYKernel[i] * depthSensitivity;
        
        float dotcalc = 1 - step(dot(normal, middleNormal), normalSensitivity);
        
        SobelNormalX += dotcalc * sobelXKernel[i];
        SobelNormalY += dotcalc * sobelYKernel[i];
    }
    
    
    Edge = max(sqrt(SobelX * SobelX + SobelY * SobelY), sqrt(SobelNormalX * SobelNormalX + SobelNormalY * SobelNormalY));
}



#endif //STYLIZATIONINCLUDE