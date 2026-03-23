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

void SobelEdgeSample_float(float4 UV, float2 TexelSize, float depthSensitivity,float normalSensitivity, out float Edge)
{
    float SobelX = 0;
    float SobelY = 0;
    
    
    float SobelNormalX = 0;
    float SobelNormalY = 0;
    
    float3 middleNormal = SHADERGRAPH_SAMPLE_SCENE_NORMAL(UV);
    
    [unroll]
    for (int i = 0; i < 9; i++)
    {
        //float2 sampleUV = clamp(UV.xy + sobelSamplePoints[i] * TexelSize, 0.0, 1.0);
        float depth = SHADERGRAPH_SAMPLE_SCENE_DEPTH(float4(UV.xy + sobelSamplePoints[i] * TexelSize.xy,UV.zw));
        //float ddepth = clamp(abs(middleDepth - depth) / (sobelSamplePoints[i].x * sobelSamplePoints[i].x + sobelSamplePoints[i].y + sobelSamplePoints[i].y),0,1);
        SobelX += depth * sobelXKernel[i] * depthSensitivity;
        SobelY += depth * sobelYKernel[i] * depthSensitivity;
        
        float3 normal = SHADERGRAPH_SAMPLE_SCENE_NORMAL(float4(UV.xy + sobelSamplePoints[i] * TexelSize.xy, UV.zw));
        float dotcalc = 1 - step(dot(normal, middleNormal), normalSensitivity);
        //float ddepth = clamp(abs(middleDepth - depth) / (sobelSamplePoints[i].x * sobelSamplePoints[i].x + sobelSamplePoints[i].y + sobelSamplePoints[i].y),0,1);
        SobelNormalX += dotcalc * sobelXKernel[i];
        SobelNormalY += dotcalc * sobelYKernel[i];
    }
    
    
    Edge = max(sqrt(SobelX * SobelX + SobelY * SobelY), sqrt(SobelNormalX * SobelNormalX + SobelNormalY * SobelNormalY));
}



#endif //STYLIZATIONINCLUDE