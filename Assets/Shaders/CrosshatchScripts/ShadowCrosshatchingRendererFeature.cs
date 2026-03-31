using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
#if UNITY_EDITOR
using ShaderKeywordFilter = UnityEditor.ShaderKeywordFilter;
#endif

[SupportedOnRenderer(typeof(UniversalRendererData))]
[DisallowMultipleRendererFeature("CrossHatch Screen Space Ambient Occlusion")]
[Tooltip("The Ambient Occlusion effect darkens creases, holes, intersections and surfaces that are close to each other.")]
//[URPHelpURL("post-processing-ssao")]
public class ShadowCrosshatchingRendererFeature : ScriptableRendererFeature
{

    // Serialized Fields
    [SerializeField] private ShadowCrosshatchingRenderFeatureSettings m_Settings = new ShadowCrosshatchingRenderFeatureSettings();
    [SerializeField] private Shader m_SSAOShader;
    [SerializeField] private Material m_CrosshatchMaterial;
    [SerializeField] private Texture2D[] m_BlueNoiseTextures;

    // Private Fields
    private Material m_Material;
    private SSAOShadowCrosshatchingPass m_SSAOPass = null;

    // Internal / Constants
    internal ref ShadowCrosshatchingRenderFeatureSettings settings => ref m_Settings;
    internal const string k_AOInterleavedGradientKeyword = "_INTERLEAVED_GRADIENT";
    internal const string k_AOBlueNoiseKeyword = "_BLUE_NOISE";
    internal const string k_OrthographicCameraKeyword = "_ORTHOGRAPHIC";
    internal const string k_SourceDepthLowKeyword = "_SOURCE_DEPTH_LOW";
    internal const string k_SourceDepthMediumKeyword = "_SOURCE_DEPTH_MEDIUM";
    internal const string k_SourceDepthHighKeyword = "_SOURCE_DEPTH_HIGH";
    internal const string k_SourceDepthNormalsKeyword = "_SOURCE_DEPTH_NORMALS";
    internal const string k_SampleCountLowKeyword = "_SAMPLE_COUNT_LOW";
    internal const string k_SampleCountMediumKeyword = "_SAMPLE_COUNT_MEDIUM";
    internal const string k_SampleCountHighKeyword = "_SAMPLE_COUNT_HIGH";

    /// <inheritdoc/>
    public override void Create()
    {
        // Create the pass...
        if (m_SSAOPass == null)
            m_SSAOPass = new SSAOShadowCrosshatchingPass();

        if (m_Material == null && m_SSAOShader != null)
            m_Material = CoreUtils.CreateEngineMaterial(m_SSAOShader);

        // Check for previous version of SSAO
        if (m_Settings.SampleCount > 0)
        {
            m_Settings.AOMethod = ShadowCrosshatchingRenderFeatureSettings.AOMethodOptions.InterleavedGradient;

            if (m_Settings.SampleCount > 11)
                m_Settings.Samples = ShadowCrosshatchingRenderFeatureSettings.AOSampleOption.High;
            else if (m_Settings.SampleCount > 8)
                m_Settings.Samples = ShadowCrosshatchingRenderFeatureSettings.AOSampleOption.Medium;
            else
                m_Settings.Samples = ShadowCrosshatchingRenderFeatureSettings.AOSampleOption.Low;

            m_Settings.SampleCount = -1;
        }
    }

    /// <inheritdoc/>
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (UniversalRenderer.IsOffscreenDepthTexture(ref renderingData.cameraData))
            return;

        if (m_SSAOShader == null)
        {
            Debug.LogError("Missing SSAO shader.");
            return;
        }

        if (m_BlueNoiseTextures == null || m_BlueNoiseTextures.Length == 0)
        {
            Debug.LogError("Missing blue noise textures.");
            return;
        }

        bool shouldAdd = m_SSAOPass.Setup(
            m_Settings,
            renderer,
            m_Material,
            m_CrosshatchMaterial,
            m_BlueNoiseTextures
        );

        if (shouldAdd)
            renderer.EnqueuePass(m_SSAOPass);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        m_SSAOPass?.Dispose();
        m_SSAOPass = null;
        CoreUtils.Destroy(m_Material);
    }


    // Use this class to pass around settings from the feature to the pass
    [Serializable]
    public class ShadowCrosshatchingRenderFeatureSettings
    {
        // Parameters
        [SerializeField] internal AOMethodOptions AOMethod = AOMethodOptions.BlueNoise;
        [SerializeField] internal bool Downsample = false;
        [SerializeField] internal bool AfterOpaque = false;
        [SerializeField] internal DepthSource Source = DepthSource.DepthNormals;
        [SerializeField] internal NormalQuality NormalSamples = NormalQuality.Medium;
        [SerializeField] internal float Intensity = 3.0f;
        [SerializeField] internal float DirectLightingStrength = 0.25f;
        [SerializeField] internal float Radius = 0.035f;
        [SerializeField] internal AOSampleOption Samples = AOSampleOption.Medium;
        [SerializeField] internal BlurQualityOptions BlurQuality = BlurQualityOptions.High;
        [SerializeField] internal float Falloff = 100f;

        // Legacy. Kept to migrate users over to use Samples instead.
        [SerializeField] internal int SampleCount = -1;

        // Enums
        internal enum DepthSource
        {
            Depth = 0,
            DepthNormals = 1
        }

        internal enum NormalQuality
        {
            Low,
            Medium,
            High
        }

        internal enum AOSampleOption
        {
            High,   // 12 Samples
            Medium, // 8 Samples
            Low,    // 4 Samples
        }

        internal enum AOMethodOptions
        {
            BlueNoise,
            InterleavedGradient,
        }

        internal enum BlurQualityOptions
        {
            High,   // Bilateral
            Medium, // Gaussian
            Low,    // Kawase
        }
    }



}