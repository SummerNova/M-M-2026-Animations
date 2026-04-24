using UnityEngine;
using static ShadowCrosshatchingRendererFeature;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering;
using System;
using UnityEngine.Experimental.Rendering;

class SSAOShadowCrosshatchingPass : ScriptableRenderPass
{
    // Private Variables
    private readonly bool m_SupportsR8RenderTextureFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8);
    private int m_BlueNoiseTextureIndex = 0;
    private Material m_SSAOMaterial;
    private Material m_CrosshatchMaterial;
    private Texture2D[] m_BlueNoiseTextures;
    private Vector4[] m_CameraTopLeftCorner = new Vector4[2];
    private Vector4[] m_CameraXExtent = new Vector4[2];
    private Vector4[] m_CameraYExtent = new Vector4[2];
    private Vector4[] m_CameraZExtent = new Vector4[2];
    private BlurTypes m_BlurType = BlurTypes.Bilateral;
    private Matrix4x4[] m_CameraViewProjections = new Matrix4x4[2];
    private ProfilingSampler m_ProfilingSampler = new ProfilingSampler("SSAO Shadow Crosshatching");
    private RenderTextureDescriptor m_AOPassDescriptor;
    private ShadowCrosshatchingRenderFeatureSettings m_CurrentSettings;

    // Constants
    private const string k_SSAOTextureName = "_ScreenSpaceOcclusionTexture";
    private const string k_AmbientOcclusionParamName = "_AmbientOcclusionParam";

    // Statics
    internal static readonly int s_AmbientOcclusionParamID = Shader.PropertyToID(k_AmbientOcclusionParamName);
    private static readonly int s_SSAOParamsID = Shader.PropertyToID("_SSAOParams");
    private static readonly int s_SSAOBlueNoiseParamsID = Shader.PropertyToID("_SSAOBlueNoiseParams");
    private static readonly int s_BlueNoiseTextureID = Shader.PropertyToID("_BlueNoiseTexture");
    private static readonly int s_SSAOFinalTextureID = Shader.PropertyToID(k_SSAOTextureName);
    private static readonly int s_CameraViewXExtentID = Shader.PropertyToID("_CameraViewXExtent");
    private static readonly int s_CameraViewYExtentID = Shader.PropertyToID("_CameraViewYExtent");
    private static readonly int s_CameraViewZExtentID = Shader.PropertyToID("_CameraViewZExtent");
    private static readonly int s_ProjectionParams2ID = Shader.PropertyToID("_ProjectionParams2");
    private static readonly int s_CameraViewProjectionsID = Shader.PropertyToID("_CameraViewProjections");
    private static readonly int s_CameraViewTopLeftCornerID = Shader.PropertyToID("_CameraViewTopLeftCorner");
    private static readonly int s_CameraNormalsTextureID = Shader.PropertyToID("_CameraNormalsTexture");
    private static readonly int s_CustomAOTextureID = Shader.PropertyToID("_CrosshatchAOTexture");
    private static readonly int s_CameraColorTextureID = Shader.PropertyToID("_CameraColorTexture");

    // Enums
    private enum BlurTypes
    {
        Bilateral,
        Gaussian,
        Kawase,
    }

    private enum ShaderPasses
    {
        AmbientOcclusion = 0,

        BilateralBlurHorizontal = 1,
        BilateralBlurVertical = 2,
        BilateralBlurFinal = 3,
        BilateralAfterOpaque = 4,

        GaussianBlurHorizontal = 5,
        GaussianBlurVertical = 6,
        GaussianAfterOpaque = 7,

        KawaseBlur = 8,
        KawaseAfterOpaque = 9,
    }

    // Structs
    private readonly struct SSAOMaterialParams
    {
        internal readonly bool orthographicCamera;
        internal readonly bool aoBlueNoise;
        internal readonly bool aoInterleavedGradient;
        internal readonly bool sampleCountHigh;
        internal readonly bool sampleCountMedium;
        internal readonly bool sampleCountLow;
        internal readonly bool sourceDepthNormals;
        internal readonly bool sourceDepthHigh;
        internal readonly bool sourceDepthMedium;
        internal readonly bool sourceDepthLow;
        internal readonly Vector4 ssaoParams;

        internal SSAOMaterialParams(ShadowCrosshatchingRenderFeatureSettings settings, bool isOrthographic)
        {
            bool isUsingDepthNormals = settings.Source == ShadowCrosshatchingRenderFeatureSettings.DepthSource.DepthNormals;
            float radiusMultiplier = settings.AOMethod == ShadowCrosshatchingRenderFeatureSettings.AOMethodOptions.BlueNoise ? 1.5f : 1;
            orthographicCamera = isOrthographic;
            aoBlueNoise = settings.AOMethod == ShadowCrosshatchingRenderFeatureSettings.AOMethodOptions.BlueNoise;
            aoInterleavedGradient = settings.AOMethod == ShadowCrosshatchingRenderFeatureSettings.AOMethodOptions.InterleavedGradient;
            sampleCountHigh = settings.Samples == ShadowCrosshatchingRenderFeatureSettings.AOSampleOption.High;
            sampleCountMedium = settings.Samples == ShadowCrosshatchingRenderFeatureSettings.AOSampleOption.Medium;
            sampleCountLow = settings.Samples == ShadowCrosshatchingRenderFeatureSettings.AOSampleOption.Low;
            sourceDepthNormals = settings.Source == ShadowCrosshatchingRenderFeatureSettings.DepthSource.DepthNormals;
            sourceDepthHigh = !isUsingDepthNormals && settings.NormalSamples == ShadowCrosshatchingRenderFeatureSettings.NormalQuality.High;
            sourceDepthMedium = !isUsingDepthNormals && settings.NormalSamples == ShadowCrosshatchingRenderFeatureSettings.NormalQuality.Medium;
            sourceDepthLow = !isUsingDepthNormals && settings.NormalSamples == ShadowCrosshatchingRenderFeatureSettings.NormalQuality.Low;
            ssaoParams = new Vector4(
                settings.Intensity, // Intensity
                settings.Radius * radiusMultiplier, // Radius
                1.0f / (settings.Downsample ? 2 : 1), // Downsampling
                settings.Falloff // Falloff
            );
        }

        internal bool Equals(in SSAOMaterialParams other)
        {
            return orthographicCamera == other.orthographicCamera
                   && aoBlueNoise == other.aoBlueNoise
                   && aoInterleavedGradient == other.aoInterleavedGradient
                   && sampleCountHigh == other.sampleCountHigh
                   && sampleCountMedium == other.sampleCountMedium
                   && sampleCountLow == other.sampleCountLow
                   && sourceDepthNormals == other.sourceDepthNormals
                   && sourceDepthHigh == other.sourceDepthHigh
                   && sourceDepthMedium == other.sourceDepthMedium
                   && sourceDepthLow == other.sourceDepthLow
                   && ssaoParams == other.ssaoParams
                   ;
        }
    }
    private SSAOMaterialParams m_SSAOParamsPrev = new SSAOMaterialParams();

    internal SSAOShadowCrosshatchingPass()
    {
        m_CurrentSettings = new ShadowCrosshatchingRenderFeatureSettings();
    }

    internal bool Setup(ShadowCrosshatchingRenderFeatureSettings featureSettings, ScriptableRenderer renderer, Material material, Material crosshatchMaterial, Texture2D[] blueNoiseTextures)
    {
        m_BlueNoiseTextures = blueNoiseTextures;
        m_SSAOMaterial = material;
        m_CrosshatchMaterial = crosshatchMaterial;
        m_CurrentSettings = featureSettings;

        // RenderPass Event + Source Settings (Depth / Depth&Normals
        renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

        m_CurrentSettings.Source = ShadowCrosshatchingRenderFeatureSettings.DepthSource.DepthNormals;

        // Ask for a Depth or Depth + Normals textures
        switch (m_CurrentSettings.Source)
        {
            case ShadowCrosshatchingRenderFeatureSettings.DepthSource.Depth:
                ConfigureInput(ScriptableRenderPassInput.Depth);
                break;
            case ShadowCrosshatchingRenderFeatureSettings.DepthSource.DepthNormals:
                ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal); // need depthNormal prepass for forward-only geometry
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        // Blur settings
        switch (m_CurrentSettings.BlurQuality)
        {
            case ShadowCrosshatchingRenderFeatureSettings.BlurQualityOptions.High:
                m_BlurType = BlurTypes.Bilateral;
                break;
            case ShadowCrosshatchingRenderFeatureSettings.BlurQualityOptions.Medium:
                m_BlurType = BlurTypes.Gaussian;
                break;
            case ShadowCrosshatchingRenderFeatureSettings.BlurQualityOptions.Low:
                m_BlurType = BlurTypes.Kawase;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return m_SSAOMaterial != null
               && m_CrosshatchMaterial != null
               && m_CurrentSettings.Intensity > 0.0f
               && m_CurrentSettings.Radius > 0.0f
               && m_CurrentSettings.Falloff > 0.0f;
    }

    private void SetupKeywordsAndParameters(ref ShadowCrosshatchingRenderFeatureSettings settings, ref UniversalCameraData cameraData)
    {
#if ENABLE_VR && ENABLE_XR_MODULE
                int eyeCount = cameraData.xr.enabled && cameraData.xr.singlePassEnabled ? 2 : 1;
#else
        int eyeCount = 1;
#endif

        for (int eyeIndex = 0; eyeIndex < eyeCount; eyeIndex++)
        {
            Matrix4x4 view = cameraData.GetViewMatrix(eyeIndex);
            Matrix4x4 proj = cameraData.GetProjectionMatrix(eyeIndex);
            m_CameraViewProjections[eyeIndex] = proj * view;

            // camera view space without translation, used by SSAO.hlsl ReconstructViewPos() to calculate view vector.
            Matrix4x4 cview = view;
            cview.SetColumn(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            Matrix4x4 cviewProj = proj * cview;
            Matrix4x4 cviewProjInv = cviewProj.inverse;

            Vector4 topLeftCorner = cviewProjInv.MultiplyPoint(new Vector4(-1, 1, -1, 1));
            Vector4 topRightCorner = cviewProjInv.MultiplyPoint(new Vector4(1, 1, -1, 1));
            Vector4 bottomLeftCorner = cviewProjInv.MultiplyPoint(new Vector4(-1, -1, -1, 1));
            Vector4 farCentre = cviewProjInv.MultiplyPoint(new Vector4(0, 0, 1, 1));
            m_CameraTopLeftCorner[eyeIndex] = topLeftCorner;
            m_CameraXExtent[eyeIndex] = topRightCorner - topLeftCorner;
            m_CameraYExtent[eyeIndex] = bottomLeftCorner - topLeftCorner;
            m_CameraZExtent[eyeIndex] = farCentre;
        }

        m_SSAOMaterial.SetVector(s_ProjectionParams2ID, new Vector4(1.0f / cameraData.camera.nearClipPlane, 0.0f, 0.0f, 0.0f));
        m_SSAOMaterial.SetMatrixArray(s_CameraViewProjectionsID, m_CameraViewProjections);
        m_SSAOMaterial.SetVectorArray(s_CameraViewTopLeftCornerID, m_CameraTopLeftCorner);
        m_SSAOMaterial.SetVectorArray(s_CameraViewXExtentID, m_CameraXExtent);
        m_SSAOMaterial.SetVectorArray(s_CameraViewYExtentID, m_CameraYExtent);
        m_SSAOMaterial.SetVectorArray(s_CameraViewZExtentID, m_CameraZExtent);

        if (settings.AOMethod == ShadowCrosshatchingRenderFeatureSettings.AOMethodOptions.BlueNoise)
        {
            m_BlueNoiseTextureIndex = (m_BlueNoiseTextureIndex + 1) % m_BlueNoiseTextures.Length;
            Texture2D noiseTexture = m_BlueNoiseTextures[m_BlueNoiseTextureIndex];
            Vector4 blueNoiseParams = new Vector4(
                cameraData.cameraTargetDescriptor.width / (float)noiseTexture.width,
                cameraData.cameraTargetDescriptor.height / (float)noiseTexture.height,
                UnityEngine.Random.value,
                UnityEngine.Random.value
            );

            // For testing we use a single blue noise texture and a single set of blue noise params.
#if UNITY_INCLUDE_TESTS
            noiseTexture = m_BlueNoiseTextures[0];
            blueNoiseParams.z = 1;
            blueNoiseParams.w = 1;
#endif

            m_SSAOMaterial.SetTexture(s_BlueNoiseTextureID, noiseTexture);
            m_SSAOMaterial.SetVector(s_SSAOBlueNoiseParamsID, blueNoiseParams);
        }

        // Setting keywords can be somewhat expensive on low-end platforms.
        // Previous params are cached to avoid setting the same keywords every frame.
        SSAOMaterialParams matParams = new SSAOMaterialParams(settings, cameraData.camera.orthographic);
        bool ssaoParamsDirty = !m_SSAOParamsPrev.Equals(in matParams); // Checks if the parameters have changed.
        bool isParamsPropertySet = m_SSAOMaterial.HasProperty(s_SSAOParamsID); // Checks if the parameters have been set on the material.
        if (!ssaoParamsDirty && isParamsPropertySet)
            return;

        m_SSAOParamsPrev = matParams;
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_OrthographicCameraKeyword, matParams.orthographicCamera);
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_AOBlueNoiseKeyword, matParams.aoBlueNoise);
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_AOInterleavedGradientKeyword, matParams.aoInterleavedGradient);
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_SampleCountHighKeyword, matParams.sampleCountHigh);
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_SampleCountMediumKeyword, matParams.sampleCountMedium);
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_SampleCountLowKeyword, matParams.sampleCountLow);
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_SourceDepthNormalsKeyword, matParams.sourceDepthNormals);
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_SourceDepthHighKeyword, matParams.sourceDepthHigh);
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_SourceDepthMediumKeyword, matParams.sourceDepthMedium);
        CoreUtils.SetKeyword(m_SSAOMaterial, ShadowCrosshatchingRendererFeature.k_SourceDepthLowKeyword, matParams.sourceDepthLow);
        m_SSAOMaterial.SetVector(s_SSAOParamsID, matParams.ssaoParams);
    }

    /*----------------------------------------------------------------------------------------------------------------------------------------
     ------------------------------------------------------------- RENDER-GRAPH --------------------------------------------------------------
     ----------------------------------------------------------------------------------------------------------------------------------------*/

    private class SSAOPassData
    {
        internal bool afterOpaque;
        internal ShadowCrosshatchingRenderFeatureSettings.BlurQualityOptions BlurQuality;
        internal MaterialPropertyBlock materialPropertyBlock;
        internal Material material;
        internal float directLightingStrength;
        internal TextureHandle cameraColor;
        internal TextureHandle AOTexture;
        internal TextureHandle finalTexture;
        internal TextureHandle blurTexture;
        internal TextureHandle cameraNormalsTexture;
        internal UniversalCameraData cameraData;
    }

    private class SSAOBlurPassData
    {
        internal TextureHandle srcTexture;
        internal TextureHandle dstTexture;
        internal MaterialPropertyBlock materialPropertyBlock;
        internal Material material;
        internal UniversalCameraData cameraData;
        internal int pass;
        internal ShadowCrosshatchingRenderFeatureSettings.BlurQualityOptions BlurQuality;
        internal bool afterOpaque;
    }

    private class CrosshatchCompositePassData
    {
        internal TextureHandle sourceColor;
        internal TextureHandle aoTexture;
        internal TextureHandle outputColor;
        internal Material material;
        internal MaterialPropertyBlock materialPropertyBlock;
    }

    private class SSAOFinalPassData
    {
        internal float directLightingStrength;
    }

    private void InitSSAOPassData(SSAOPassData data)
    {
        data.material = m_SSAOMaterial;
        data.BlurQuality = m_CurrentSettings.BlurQuality;
        data.afterOpaque = m_CurrentSettings.AfterOpaque;
        data.directLightingStrength = m_CurrentSettings.DirectLightingStrength;
    }

    private void InitSSAOBlurPassData(SSAOBlurPassData data)
    {
        data.material = m_SSAOMaterial;
        data.BlurQuality = m_CurrentSettings.BlurQuality;
        data.afterOpaque = m_CurrentSettings.AfterOpaque;
    }

    private static Vector4 ComputeScaleBias(in TextureHandle source, bool yFlip)
    {
        RTHandle srcRTHandle = source;
        Vector2 viewportScale;
        if (srcRTHandle is { useScaling: true })
        {
            var scale = srcRTHandle.rtHandleProperties.rtHandleScale;
            viewportScale.x = scale.x;
            viewportScale.y = scale.y;
        }
        else
        {
            viewportScale = Vector2.one;
        }

        if (yFlip)
            return new Vector4(viewportScale.x, -viewportScale.y, 0, viewportScale.y);
        else
            return new Vector4(viewportScale.x, viewportScale.y, 0, 0);
    }

    private static readonly int _BlitScaleBias = Shader.PropertyToID(nameof(_BlitScaleBias));
    private static readonly int _BlitTexture = Shader.PropertyToID(nameof(_BlitTexture));

    private void RecordBlurStep(RenderGraph renderGraph, UniversalCameraData cameraData, string blurPassName, in TextureHandle src, in TextureHandle dst, int pass, bool isLastPass)
    {
        using (var builder = renderGraph.AddRasterRenderPass<SSAOBlurPassData>(blurPassName, out var passData, m_ProfilingSampler))
        {
            // Fill in the Pass data...
            InitSSAOBlurPassData(passData);
            passData.srcTexture = src;
            passData.dstTexture = dst;
            passData.cameraData = cameraData;
            passData.pass = pass;
            passData.materialPropertyBlock ??= new();

            builder.UseTexture(passData.srcTexture);

            AccessFlags finalDstAccess = passData.afterOpaque && isLastPass ? AccessFlags.Write : AccessFlags.WriteAll;
            builder.SetRenderAttachment(passData.dstTexture, 0, finalDstAccess);

            builder.SetRenderFunc(static (SSAOBlurPassData data, RasterGraphContext ctx) =>
            {
                bool yFlip = ctx.GetTextureUVOrigin(in data.srcTexture) != ctx.GetTextureUVOrigin(in data.dstTexture);
                Vector4 viewScaleBias = ComputeScaleBias(data.srcTexture, yFlip);

                data.materialPropertyBlock.Clear();
                data.materialPropertyBlock.SetVector(_BlitScaleBias, viewScaleBias);
                data.materialPropertyBlock.SetTexture(_BlitTexture, data.srcTexture);
                CoreUtils.DrawFullScreen(ctx.cmd, data.material, data.materialPropertyBlock, data.pass);
            });
        }
    }

    private void RecordCrosshatchCompositePass(
    RenderGraph renderGraph,
    in TextureHandle sourceColor,
    in TextureHandle aoTexture,
    in TextureHandle outputColor)
    {
        using (var builder = renderGraph.AddRasterRenderPass<CrosshatchCompositePassData>(
            "Crosshatch Composite",
            out var passData,
            m_ProfilingSampler))
        {
            passData.sourceColor = sourceColor;
            passData.aoTexture = aoTexture;
            passData.outputColor = outputColor;
            passData.material = m_CrosshatchMaterial;
            passData.materialPropertyBlock ??= new();

            builder.UseTexture(passData.sourceColor, AccessFlags.Read);
            builder.UseTexture(passData.aoTexture, AccessFlags.Read);
            builder.SetRenderAttachment(passData.outputColor, 0, AccessFlags.WriteAll);

            builder.SetRenderFunc(static (CrosshatchCompositePassData data, RasterGraphContext ctx) =>
            {
                data.materialPropertyBlock.Clear();
                data.materialPropertyBlock.SetTexture(s_CameraColorTextureID, data.sourceColor);
                data.materialPropertyBlock.SetTexture(s_CustomAOTextureID, data.aoTexture);
                data.materialPropertyBlock.SetVector(_BlitScaleBias, new Vector4(1, 1, 0, 0));

                CoreUtils.DrawFullScreen(ctx.cmd, data.material, data.materialPropertyBlock, 0);
            });
        }
    }

    private void RecordFinalCopyPass(
    RenderGraph renderGraph,
    in TextureHandle src,
    in TextureHandle dst)
    {
        using (var builder = renderGraph.AddRasterRenderPass<SSAOBlurPassData>(
            "Copy Crosshatch To Camera",
            out var passData,
            m_ProfilingSampler))
        {
            passData.srcTexture = src;
            passData.dstTexture = dst;
            passData.material = null;
            passData.materialPropertyBlock ??= new();

            builder.UseTexture(passData.srcTexture, AccessFlags.Read);
            builder.SetRenderAttachment(passData.dstTexture, 0, AccessFlags.WriteAll);

            builder.SetRenderFunc(static (SSAOBlurPassData data, RasterGraphContext ctx) =>
            {
                Blitter.BlitTexture(ctx.cmd, data.srcTexture, new Vector4(1, 1, 0, 0), 0, false);
            });
        }
    }


    /// <inheritdoc cref="IRenderGraphRecorder.RecordRenderGraph"/>
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

        // Create the texture handles...
        CreateRenderTextureHandles(renderGraph,
                                   resourceData,
                                   cameraData,
                                   out TextureHandle aoTexture,
                                   out TextureHandle blurTexture,
                                   out TextureHandle finalTexture);

        // Get the resources
        TextureHandle cameraDepthTexture = resourceData.cameraDepthTexture;
        TextureHandle cameraNormalsTexture = resourceData.cameraNormalsTexture;

        // Update keywords and other shader params
        SetupKeywordsAndParameters(ref m_CurrentSettings, ref cameraData);

        using (var builder = renderGraph.AddRasterRenderPass<SSAOPassData>("Blit SSAO", out var passData, m_ProfilingSampler))
        {
            // Shader keyword changes are considered as global state modifications
            builder.AllowGlobalStateModification(true);

            // Fill in the Pass data...
            InitSSAOPassData(passData);
            passData.cameraColor = resourceData.cameraColor;
            passData.AOTexture = aoTexture;
            passData.finalTexture = finalTexture;
            passData.blurTexture = blurTexture;
            passData.cameraData = cameraData;
            passData.materialPropertyBlock ??= new();

            // Declare input textures
            builder.SetRenderAttachment(passData.AOTexture, 0, AccessFlags.WriteAll);

            // TODO: Refactor to eliminate the need for 'UseTexture'.
            // Currently required only because 'PostProcessUtils.SetSourceSize' allocates an RTHandle,
            // which expects a valid graphicsResource. Without this call, 'cameraColor.graphicsResource'
            // may be null if it wasn't initialized in an earlier pass (e.g., DrawOpaque).
            if (passData.cameraColor.IsValid())
                builder.UseTexture(passData.cameraColor, AccessFlags.Read);

            if (cameraDepthTexture.IsValid())
                builder.UseTexture(cameraDepthTexture, AccessFlags.Read);

            if (m_CurrentSettings.Source == ShadowCrosshatchingRenderFeatureSettings.DepthSource.DepthNormals && cameraNormalsTexture.IsValid())
            {
                builder.UseTexture(cameraNormalsTexture, AccessFlags.Read);
                passData.cameraNormalsTexture = cameraNormalsTexture;
            }

            builder.SetRenderFunc(static (SSAOPassData data, RasterGraphContext ctx) =>
            {
                // Setup
                //PostProcessUtils.SetGlobalShaderSourceSize(ctx.cmd, data.cameraData.cameraTargetDescriptor.width, data.cameraData.cameraTargetDescriptor.height, data.cameraColor);

                data.materialPropertyBlock.Clear();

                if (data.cameraNormalsTexture.IsValid())
                    data.materialPropertyBlock.SetTexture(s_CameraNormalsTextureID, data.cameraNormalsTexture);

                Vector4 viewScaleBias = new(1, 1, 0, 0);
                data.materialPropertyBlock.SetVector(_BlitScaleBias, viewScaleBias);

                CoreUtils.DrawFullScreen(ctx.cmd, data.material, data.materialPropertyBlock, (int)ShaderPasses.AmbientOcclusion);
            });
        }

        switch (m_CurrentSettings.BlurQuality)
        {
            case ShadowCrosshatchingRenderFeatureSettings.BlurQualityOptions.High:
                RecordBlurStep(renderGraph, cameraData, "Blur SSAO Horizontal (High)", aoTexture, blurTexture, (int)ShaderPasses.BilateralBlurHorizontal, false);
                RecordBlurStep(renderGraph, cameraData, "Blur SSAO Vertical (High)", blurTexture, aoTexture, (int)ShaderPasses.BilateralBlurVertical, false);
                RecordBlurStep(renderGraph, cameraData, "Blur SSAO Final (High)", aoTexture, finalTexture, (int)ShaderPasses.BilateralBlurFinal, true);
                break;

            case ShadowCrosshatchingRenderFeatureSettings.BlurQualityOptions.Medium:
                RecordBlurStep(renderGraph, cameraData, "Blur SSAO Horizontal (Medium)", aoTexture, blurTexture, (int)ShaderPasses.GaussianBlurHorizontal, false);
                RecordBlurStep(renderGraph, cameraData, "Blur SSAO Final (Medium)", blurTexture, finalTexture, (int)ShaderPasses.GaussianBlurVertical, true);
                break;

            case ShadowCrosshatchingRenderFeatureSettings.BlurQualityOptions.Low:
                RecordBlurStep(renderGraph, cameraData, "Blur SSAO (Low)", aoTexture, finalTexture, (int)ShaderPasses.KawaseBlur, true);
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }

        RenderTextureDescriptor compositeDesc = cameraData.cameraTargetDescriptor;

        compositeDesc.depthStencilFormat = GraphicsFormat.None;
        compositeDesc.depthBufferBits = 0;
        compositeDesc.msaaSamples = 1;

        TextureHandle compositeColor =
            UniversalRenderer.CreateRenderGraphTexture(
                renderGraph,
                compositeDesc,
                "_CrosshatchCompositeColor",
                false,
                FilterMode.Bilinear
            );

        RecordCrosshatchCompositePass(
            renderGraph,
            resourceData.activeColorTexture,
            finalTexture,
            compositeColor
        );

        if (!m_CurrentSettings.AfterOpaque)
        {
            // Add cleanup pass to:
            // - Set global keywords for next passes
            // - Set global texture as there is a limitation in Render Graph where an input texture cannot be set as a global texture after the pass runs
            // A Raster pass is used so it can be merged easily with the blur passes.
            using (var builder = renderGraph.AddRasterRenderPass<SSAOFinalPassData>("Cleanup SSAO", out var passData, m_ProfilingSampler))
            {
                passData.directLightingStrength = m_CurrentSettings.DirectLightingStrength;

                builder.AllowGlobalStateModification(true);

                builder.UseTexture(finalTexture, AccessFlags.Read);
                builder.SetGlobalTextureAfterPass(finalTexture, s_SSAOFinalTextureID);

                builder.SetRenderFunc(static (SSAOFinalPassData data, RasterGraphContext ctx) =>
                {
                    // We only want URP shaders to sample SSAO if After Opaque is disabled...
                    //ctx.cmd.SetKeyword(ShaderGlobalKeywords.ScreenSpaceOcclusion, true);
                    ctx.cmd.SetGlobalVector(s_AmbientOcclusionParamID, new Vector4(1f, 0f, 0f, data.directLightingStrength));
                });
            }
        }

        RecordFinalCopyPass(renderGraph, compositeColor, resourceData.activeColorTexture);
    }

    private void CreateRenderTextureHandles(RenderGraph renderGraph, UniversalResourceData resourceData,
        UniversalCameraData cameraData, out TextureHandle aoTexture, out TextureHandle blurTexture, out TextureHandle finalTexture)
    {
        // Descriptor for the final blur pass
        RenderTextureDescriptor finalTextureDescriptor = cameraData.cameraTargetDescriptor;
        finalTextureDescriptor.colorFormat = m_SupportsR8RenderTextureFormat ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
        finalTextureDescriptor.depthStencilFormat = GraphicsFormat.None;
        finalTextureDescriptor.msaaSamples = 1;

        // Descriptor for the AO and Blur passes
        int downsampleDivider = m_CurrentSettings.Downsample ? 2 : 1;
        bool useRedComponentOnly = m_SupportsR8RenderTextureFormat && m_BlurType > BlurTypes.Bilateral;

        RenderTextureDescriptor aoBlurDescriptor = finalTextureDescriptor;
        aoBlurDescriptor.colorFormat = useRedComponentOnly ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
        aoBlurDescriptor.width /= downsampleDivider;
        aoBlurDescriptor.height /= downsampleDivider;

        // Handles
        aoTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, aoBlurDescriptor, "_SSAO_OcclusionTexture0", false, FilterMode.Bilinear);
        finalTexture = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph,
            finalTextureDescriptor,
            "_SSAO_FinalTexture",
            false,
            FilterMode.Bilinear
            );
        

        if (m_CurrentSettings.BlurQuality != ShadowCrosshatchingRenderFeatureSettings.BlurQualityOptions.Low)
            blurTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, aoBlurDescriptor, "_SSAO_OcclusionTexture1", false, FilterMode.Bilinear);
        else
            blurTexture = TextureHandle.nullHandle;

        // expose as your own global texture in a later pass
    }

    /// <inheritdoc/>
    public override void OnCameraCleanup(CommandBuffer cmd)
    {
        if (cmd == null)
            throw new ArgumentNullException("cmd");

        //if (!m_CurrentSettings.AfterOpaque)
            //cmd.SetKeyword(ShaderGlobalKeywords.ScreenSpaceOcclusion, false);
    }

    public void Dispose()
    {
        m_SSAOParamsPrev = default;
    }

}
