using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Images;
using Stride.Rendering.Materials;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Buffer = Stride.Graphics.Buffer;

namespace TR.Stride.PostProcess
{
    [DataContract]
    public class BloomSettings
    {
        [DataMember, DefaultValue(1.0f)] public float Strength { get; set; } = 0.5f;
        [DataMember, DefaultValue(1.0f)] public float Radius { get; set; } = 1.0f;
        [DataMember, DefaultValue(5)] public int NumberOfDownSamples { get; set; } = 5;
        [DataMember, DefaultValue(2.0f)] public float BrightPassSteepness { get; set; } = 2.0f;
        [DataMember, DefaultValue(4.0f)] public float ThresholdOffset { get; set; } = 4.0f;
    }

    [DataContract]
    public class ExposureSettings
    {
        [DataMember, DefaultValue(true)] public bool AutoKey { get; set; } = true;
        [DataMember, DefaultValue(0.08f)] public float Key { get; set; }  = 0.08f;
        [DataMember, DefaultValue(1.0f / 64.0f)] public float MinExposure { get; set; }  = 1.0f / 64.0f;
        [DataMember, DefaultValue(64.0f)] public float MaxExposure { get; set; } = 64.0f;
        [DataMember, DefaultValue(1.1f)] public float AdaptionSpeed { get; set; } = 1.1f;
        [DataMember, DefaultValue(2.0f)] public float Exposure { get; set; } = 2.0f;
        [DataMember, DefaultValue(true)] public bool AutoExposure { get; set; } = true;
    }

    [DataContract(nameof(CustomPostProcessingEffects))]
    [Display("Custom Post-processing effects")]
    public class CustomPostProcessingEffects : ImageEffect, IImageEffectRenderer, IPostProcessingEffects
    {
        private ColorTransformGroup colorTransformsGroup;

        public bool RequiresVelocityBuffer => Antialiasing?.RequiresVelocityBuffer ?? false;
        public bool RequiresNormalBuffer => AmbientOcclusion?.RequiresNormalBuffer ?? false || LocalReflections.Enabled;
        public bool RequiresSpecularRoughnessBuffer => LocalReflections.Enabled;

        public Exception LastException { get; protected set; }

        [DataMember] public Guid Id { get; set; } = Guid.NewGuid();

        private ImageEffectShader _brightPassShader;
        private ImageEffectShader _bloomDownSample;
        private ImageEffectShader _bloomUpSample;
        private ImageEffectShader _toneMapShader;
        private ImageEffectShader _sanitizer;
        private ComputeEffectShader _histogramShader;
        private ComputeEffectShader _histogramReduceShader;
        private ComputeEffectShader _histogramDrawDebug;

        private Buffer _histogram = null;
        private Buffer _exposure = null;

        [DataMember] public IAmbientOcclusionEffect AmbientOcclusion { get; set; }

        /// <summary>
        /// Gets the local reflections effect.
        /// </summary>
        /// <value>The local reflection technique.</value>
        /// <userdoc>Reflect the scene in glossy materials</userdoc>
        [DataMember(9)]
        [Category]
        public LocalReflections LocalReflections { get; private set; }

        /// <summary>
        /// Gets the light streak effect.
        /// </summary>
        /// <value>The light streak.</value>
        /// <userdoc>Bleed bright points along streaks</userdoc>
        [DataMember(40)]
        [Category]
        public LightStreak LightStreak { get; private set; }

        /// <summary>
        /// Gets the lens flare effect.
        /// </summary>
        /// <value>The lens flare.</value>
        /// <userdoc>Simulate artifacts produced by the internal reflection or scattering of light within camera lenses</userdoc>
        [DataMember(50)]
        [Category]
        public LensFlare LensFlare { get; private set; }

        /// <summary>
        /// Gets the final color transforms.
        /// </summary>
        /// <value>The color transforms.</value>
        /// <userdoc>Perform a transformation onto the image colors</userdoc>
        [DataMember(70)]
        [Category]
        public ColorTransformGroup ColorTransforms => colorTransformsGroup;

        /// <summary>
        /// Gets the antialiasing effect.
        /// </summary>
        /// <value>The antialiasing.</value>
        /// <userdoc>Perform anti-aliasing filtering, smoothing the jagged edges of models</userdoc>
        [DataMember(80)]
        [Display("Type", "Antialiasing")]
        public IScreenSpaceAntiAliasingEffect Antialiasing { get; set; } // TODO: Unload previous anti aliasing
        private ImageEffectShader rangeCompress;
        private ImageEffectShader rangeDecompress;

        /// Gets the fog effect.
        /// </summary>
        [DataMember(7)]
        [Category]
        public Fog Fog { get; private set; }

        /// <summary>
        /// Gets the depth of field effect.
        /// </summary>
        /// <value>The depth of field.</value>
        /// <userdoc>Accentuate regions of the image by blurring objects in the foreground or background</userdoc>
        [DataMember(10)]
        [Category]
        public DepthOfField DepthOfField { get; private set; }

        [DataMember("Bloom"), Category] public BloomSettings BloomSettings { get; set; } = new BloomSettings();
        [DataMember("Exposure"), Category] public ExposureSettings ExposureSettings { get; set; } = new ExposureSettings();
        [DataMember] public bool DebugHistogram { get; set; } = false;

        private List<Texture> _bloomRenderTargets = new(5);

        public CustomPostProcessingEffects(IServiceRegistry services)
            : this(RenderContext.GetShared(services))
        {
        }

        public CustomPostProcessingEffects(RenderContext context)
            : this()
        {
            Initialize(context);
        }

        public CustomPostProcessingEffects()
        {
            //Outline = new Outline
            //{
            //    Enabled = false
            //};

            Fog = new Fog
            {
                Enabled = false
            };

            //AmbientOcclusion = new AmbientOcclusion();
            LocalReflections = new LocalReflections();
            DepthOfField = new DepthOfField();
            //luminanceEffect = new LuminanceEffect();
            //BrightFilter = new BrightFilter();
            //Bloom = new Bloom();
            LightStreak = new LightStreak();
            LensFlare = new LensFlare();
            Antialiasing = new FXAAEffect();
            rangeCompress = new ImageEffectShader("RangeCompressorShader");
            rangeDecompress = new ImageEffectShader("RangeDecompressorShader");
            colorTransformsGroup = new ColorTransformGroup();
        }

        protected override void InitializeCore()
        {
            base.InitializeCore();

            _sanitizer = ToLoadAndUnload(new ImageEffectShader("Sanitizer"));
            _brightPassShader = ToLoadAndUnload(new ImageEffectShader("BloomBrightPass"));
            _bloomDownSample = ToLoadAndUnload(new ImageEffectShader("BloomDownSample"));
            _bloomUpSample = ToLoadAndUnload(new ImageEffectShader("BloomUpSample"));
            _toneMapShader = ToLoadAndUnload(new ImageEffectShader("ToneMapASEC"));
            _histogramShader = ToLoadAndUnload(new ComputeEffectShader(Context) { ShaderSourceName = "Histogram", Name = "Histogram" });
            _histogramReduceShader = ToLoadAndUnload(new ComputeEffectShader(Context) { ShaderSourceName = "HistogramReduce", Name = "Histogram Reduce" });
            _histogramDrawDebug = ToLoadAndUnload(new ComputeEffectShader(Context) { ShaderSourceName = "HistogramDrawDebug", Name = "Histogram Draw Debug" });

            _histogram = Buffer.Raw.New(GraphicsDevice, new uint[256], BufferFlags.ShaderResource | BufferFlags.UnorderedAccess);
            _exposure = Buffer.Structured.New(
                GraphicsDevice,
                GetManualExposureSettings(), 
                true);

            _bloomUpSample.BlendState = new BlendStateDescription(Blend.One, Blend.One);
            
            Fog = ToLoadAndUnload(Fog);
            LocalReflections = ToLoadAndUnload(LocalReflections);
            DepthOfField = ToLoadAndUnload(DepthOfField);
            //luminanceEffect = ToLoadAndUnload(luminanceEffect);
            //BrightFilter = ToLoadAndUnload(BrightFilter);
            //Bloom = ToLoadAndUnload(Bloom);
            LightStreak = ToLoadAndUnload(LightStreak);
            LensFlare = ToLoadAndUnload(LensFlare);
            //this can be null if no SSAA is selected in the editor
            if (Antialiasing != null) Antialiasing = ToLoadAndUnload(Antialiasing);

            rangeCompress = ToLoadAndUnload(rangeCompress);
            rangeDecompress = ToLoadAndUnload(rangeDecompress);
        }

        protected override void Unload()
        {
            base.Unload();

            _exposure?.Dispose();
            _histogram?.Dispose();
        }

        private float[] GetManualExposureSettings()
        {
            float initalMinLog = -12.0f;
            float initialMaxLog = 2.0f;

            return new float[]
            {
                ExposureSettings.Exposure, 1.0f / ExposureSettings.Exposure, ExposureSettings.Exposure, 0.0f,
                initalMinLog, initialMaxLog, initialMaxLog - initalMinLog, 1.0f / (initialMaxLog - initalMinLog)
            };
        }

        public void Collect(RenderContext context)
        {
        }

        public void Draw(RenderDrawContext drawContext, RenderOutputValidator outputValidator, Texture[] inputs, Texture inputDepthStencil, Texture outputTarget)
        {
            try
            {
                var colorIndex = outputValidator.Find<ColorTargetSemantic>();
                if (colorIndex < 0)
                    return;

                SetInput(0, inputs[colorIndex]);
                SetInput(1, inputDepthStencil);

                var normalsIndex = outputValidator.Find<NormalTargetSemantic>();
                if (normalsIndex >= 0)
                {
                    SetInput(2, inputs[normalsIndex]);
                }

                var specularRoughnessIndex = outputValidator.Find<SpecularColorRoughnessTargetSemantic>();
                if (specularRoughnessIndex >= 0)
                {
                    SetInput(3, inputs[specularRoughnessIndex]);
                }

                var reflectionIndex0 = outputValidator.Find<OctahedronNormalSpecularColorTargetSemantic>();
                var reflectionIndex1 = outputValidator.Find<EnvironmentLightRoughnessTargetSemantic>();
                if (reflectionIndex0 >= 0 && reflectionIndex1 >= 0)
                {
                    SetInput(4, inputs[reflectionIndex0]);
                    SetInput(5, inputs[reflectionIndex1]);
                }

                var velocityIndex = outputValidator.Find<VelocityTargetSemantic>();
                if (velocityIndex != -1)
                {
                    SetInput(6, inputs[velocityIndex]);
                }

                SetOutput(outputTarget);
                Draw(drawContext);
            }
            catch (Exception e)
            {

                LastException = e;
            }
        }

        protected override void DrawCore(RenderDrawContext context)
        {
            var input = GetInput(0);
            var output = GetOutput(0);
            if (input == null || output == null)
            {
                return;
            }

            //if (input == output)
            {
                var newInput = NewScopedRenderTarget2D(input.Description);
                //context.CommandList.Copy(input, newInput);

                _sanitizer.SetInput(0, input);
                _sanitizer.SetOutput(newInput);
                _sanitizer.Draw(context, "Sanitizer");
                input = newInput;
            }

            var inputDepthTexture = GetInput(1); // Depth

            var currentInput = input;

            var fxaa = Antialiasing as FXAAEffect;
            bool aaFirst = true;// BloomSettings.Enabled;
            bool needAA = Antialiasing != null && Antialiasing.Enabled;

            // do AA here, first. (hybrid method from Karis2013)
            if (aaFirst && needAA)
            {
                // do AA:
                if (fxaa != null)
                    fxaa.InputLuminanceInAlpha = true;

                var bufferIndex = 1;
                if (Antialiasing.RequiresDepthBuffer)
                    Antialiasing.SetInput(bufferIndex++, inputDepthTexture);

                bool requiresVelocityBuffer = Antialiasing.RequiresVelocityBuffer;
                if (requiresVelocityBuffer)
                {
                    Antialiasing.SetInput(bufferIndex++, GetInput(6));
                }

                var aaSurface = NewScopedRenderTarget2D(input.Width, input.Height, input.Format);
                if (Antialiasing.NeedRangeDecompress)
                {
                    // explanation:
                    // The Karis method (Unreal Engine 4.1x), uses a hybrid pipeline to execute AA.
                    // The AA is usually done at the end of the pipeline, but we don't benefit from
                    // AA for the posteffects, which is a shame.
                    // The Karis method, executes AA at the beginning, but for AA to be correct, it must work post tonemapping,
                    // and even more in fact, in gamma space too. Plus, it waits for the alpha=luma to be a "perceptive luma" so also gamma space.
                    // in our case, working in gamma space created monstruous outlining artefacts around eggageratedely strong constrasted objects (way in hdr range).
                    // so AA works in linear space, but still with gamma luma, as a light tradeoff to supress artefacts.

                    // create a 16 bits target for FXAA:

                    // render range compression & perceptual luma to alpha channel:
                    rangeCompress.SetInput(currentInput);
                    rangeCompress.SetOutput(aaSurface);
                    rangeCompress.Draw(context);

                    Antialiasing.SetInput(0, aaSurface);
                    Antialiasing.SetOutput(currentInput);
                    Antialiasing.Draw(context);

                    // reverse tone LDR to HDR:
                    rangeDecompress.SetInput(currentInput);
                    rangeDecompress.SetOutput(aaSurface);
                    rangeDecompress.Draw(context);
                }
                else
                {
                    Antialiasing.SetInput(0, currentInput);
                    Antialiasing.SetOutput(aaSurface);
                    Antialiasing.Draw(context);
                }

                currentInput = aaSurface;
            }

            if (LocalReflections.Enabled && inputDepthTexture != null)
            {
                var normalsBuffer = GetInput(2);
                var specularRoughnessBuffer = GetInput(3);

                if (normalsBuffer != null && specularRoughnessBuffer != null)
                {
                    // Local reflections
                    var rlrOutput = NewScopedRenderTarget2D(input.Width, input.Height, input.Format);
                    LocalReflections.SetInputSurfaces(currentInput, inputDepthTexture, normalsBuffer, specularRoughnessBuffer);
                    LocalReflections.SetOutput(rlrOutput);
                    LocalReflections.Draw(context);
                    currentInput = rlrOutput;
                }
            }

            // Ambient occlusion
            if (AmbientOcclusion != null)
            {
                var normals = InputCount > 2 ? GetInput(2) : null;
                AmbientOcclusion.Draw(context, currentInput, inputDepthTexture, normals, out var aoOutput);

                currentInput = aoOutput;
            }

            if (Fog.Enabled && inputDepthTexture != null)
            {
                // Fog
                var fogOutput = NewScopedRenderTarget2D(input.Width, input.Height, input.Format);
                Fog.SetColorDepthInput(currentInput, inputDepthTexture, context.RenderContext.RenderView.NearClipPlane, context.RenderContext.RenderView.FarClipPlane);
                Fog.SetOutput(fogOutput);
                Fog.Draw(context);
                currentInput = fogOutput;
            }

            if (DepthOfField.Enabled && inputDepthTexture != null)
            {
                // DoF
                var dofOutput = NewScopedRenderTarget2D(input.Width, input.Height, input.Format);
                DepthOfField.SetColorDepthInput(currentInput, inputDepthTexture);
                DepthOfField.SetOutput(dofOutput);
                DepthOfField.Draw(context);
                currentInput = dofOutput;
            }

            if (ExposureSettings?.AutoExposure ?? false)
            {
                context.CommandList.ClearReadWrite(_histogram, UInt4.Zero);

                // Calculate histogram
                _histogramShader.ThreadNumbers = new Int3(16, 16, 1);
                _histogramShader.ThreadGroupCounts = new Int3((int)Math.Ceiling(currentInput.Width / (float)16), (int)Math.Ceiling(currentInput.Height / (float)16), 1);
                _histogramShader.Parameters.Set(HistogramKeys.ColorInput, currentInput);
                _histogramShader.Parameters.Set(HistogramKeys.HistogramBuffer, _histogram);
                _histogramShader.Parameters.Set(HistogramKeys.Exposure, _exposure);
                _histogramShader.Draw(context);

                // Reduce histogram to get average luminance
                _histogramReduceShader.ThreadNumbers = new Int3(256, 1, 1);
                _histogramReduceShader.ThreadGroupCounts = new Int3(1, 1, 1);
                _histogramReduceShader.Parameters.Set(HistogramReduceKeys.Histogram, _histogram);
                _histogramReduceShader.Parameters.Set(HistogramReduceKeys.Exposure, _exposure);
                _histogramReduceShader.Parameters.Set(HistogramReduceKeys.PixelCount, (uint)(currentInput.Width * currentInput.Height));
                _histogramReduceShader.Parameters.Set(HistogramReduceKeys.Tau, ExposureSettings.AdaptionSpeed);
                _histogramReduceShader.Parameters.Set(HistogramReduceKeys.AutoKey, ExposureSettings.AutoKey);
                _histogramReduceShader.Parameters.Set(HistogramReduceKeys.TargetLuminance, ExposureSettings.Key);
                _histogramReduceShader.Parameters.Set(HistogramReduceKeys.MinExposure, ExposureSettings.MinExposure);
                _histogramReduceShader.Parameters.Set(HistogramReduceKeys.MaxExposure, ExposureSettings.MaxExposure);
                _histogramReduceShader.Parameters.Set(HistogramReduceKeys.TimeDelta, (float)context.RenderContext.Time.Elapsed.TotalSeconds);
                _histogramReduceShader.Draw(context);
            }
            else
            {
                _exposure.SetData(context.CommandList, GetManualExposureSettings());
            }

            // Bright Pass
            var brightPassTexture = NewScopedRenderTarget2D(currentInput.Width, currentInput.Height, currentInput.Format, 1);
            _brightPassShader.Parameters.Set(ExposureCommonKeys.Exposure, _exposure);
            _brightPassShader.Parameters.Set(BloomBrightPassKeys.BrightPassSteepness, BloomSettings.BrightPassSteepness);
            _brightPassShader.Parameters.Set(BloomBrightPassKeys.ThresholdOffset, BloomSettings.ThresholdOffset);
            _brightPassShader.SetInput(0, currentInput);
            _brightPassShader.SetOutput(brightPassTexture);
            _brightPassShader.Draw(context, "Bright pass");

            // Bloom down sample
            var bloomInput = brightPassTexture;
            _bloomRenderTargets.Add(bloomInput);
            for (var i = 0; i < BloomSettings.NumberOfDownSamples; i++)
            {
                var bloomRenderTarget = NewScopedRenderTarget2D(bloomInput.Width / 2, bloomInput.Height / 2, bloomInput.Format, 1);
                _bloomRenderTargets.Add(bloomRenderTarget);

                _bloomDownSample.SetInput(0, bloomInput);
                _bloomDownSample.SetOutput(bloomRenderTarget);
                _bloomDownSample.Draw(context, $"Bloom Down Sample {i}");

                bloomInput = bloomRenderTarget;
            }

            // Up sample
            for (var i = BloomSettings.NumberOfDownSamples - 1; i >= 0; i--)
            {
                var bloomRenderTarget = _bloomRenderTargets[i];

                _bloomUpSample.SetInput(0, bloomInput);
                _bloomUpSample.SetOutput(bloomRenderTarget);

                _bloomUpSample.Parameters.Set(BloomUpSampleKeys.Strength, BloomSettings.Strength);
                _bloomUpSample.Parameters.Set(BloomUpSampleKeys.Radius, BloomSettings.Radius);

                _bloomUpSample.Draw(context, $"Bloom up Sample {i}");

                bloomInput = bloomRenderTarget;
            }

            _bloomRenderTargets.Clear();

            var bloomOutput = bloomInput;

            if (DebugHistogram)
            {
                // Create UAV enabled debug target
                var debugOutput = NewScopedRenderTarget2D(output.Width, output.Height, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource | TextureFlags.RenderTarget | TextureFlags.UnorderedAccess);
                context.CommandList.Copy(output, debugOutput);
                context.CommandList.Reset();

                _histogramDrawDebug.ThreadNumbers = new Int3(256, 1, 1);
                _histogramDrawDebug.ThreadGroupCounts = new Int3(1, 32, 1);
                _histogramDrawDebug.Parameters.Set(HistogramDrawDebugKeys.Histogram, _histogram);
                _histogramDrawDebug.Parameters.Set(HistogramDrawDebugKeys.Exposure, _exposure);
                _histogramDrawDebug.Parameters.Set(HistogramDrawDebugKeys.ColorBuffer, debugOutput);
                _histogramDrawDebug.Draw(context);

                context.CommandList.Copy(debugOutput, output);
            }

            // Light streak pass
            if (LightStreak.Enabled)
            {
                LightStreak.SetInput(brightPassTexture);
                LightStreak.SetOutput(currentInput);
                LightStreak.Draw(context);
            }

            // Lens flare pass
            if (LensFlare.Enabled)
            {
                LensFlare.SetInput(brightPassTexture);
                LensFlare.SetOutput(currentInput);
                LensFlare.Draw(context);
            }

            bool aaLast = needAA && !aaFirst;
            var toneOutput = NewScopedRenderTarget2D(input.Width, input.Height, input.Format);


            // When FXAA is enabled we need to detect whether the ColorTransformGroup should output the Luminance into the alpha or not
            var luminanceToChannelTransform = colorTransformsGroup.PostTransforms.Get<LuminanceToChannelTransform>();
            if (fxaa != null)
            {
                if (luminanceToChannelTransform == null)
                {
                    luminanceToChannelTransform = new LuminanceToChannelTransform { ColorChannel = ColorChannel.A };
                    colorTransformsGroup.PostTransforms.Add(luminanceToChannelTransform);
                }

                // Only enabled when FXAA is enabled and InputLuminanceInAlpha is true
                luminanceToChannelTransform.Enabled = fxaa.Enabled && fxaa.InputLuminanceInAlpha;
            }
            else if (luminanceToChannelTransform != null)
            {
                luminanceToChannelTransform.Enabled = false;
            }

            // Tone map
            _toneMapShader.Parameters.Set(ExposureCommonKeys.Exposure, _exposure);
            _toneMapShader.SetInput(0, currentInput);
            _toneMapShader.SetInput(1, bloomOutput);
            _toneMapShader.SetOutput(toneOutput);
            _toneMapShader.Draw(context, "ToneMap ASEC");

            currentInput = toneOutput;

            var colorTransformOutput = aaLast ? NewScopedRenderTarget2D(input.Width, input.Height, input.Format) : output;

            // Color transform group pass (tonemap, color grading)
            var lastEffect = colorTransformsGroup.Enabled ? (ImageEffect)colorTransformsGroup : Scaler;
            lastEffect.SetInput(currentInput);
            lastEffect.SetOutput(colorTransformOutput);
            lastEffect.Draw(context);

            // do AA here, last, if not already done.
            if (aaLast)
            {
                Antialiasing.SetInput(toneOutput);
                Antialiasing.SetOutput(output);
                Antialiasing.Draw(context);
            }
        }

        /// <summary>
        /// Disables all post processing effects.
        /// </summary>
        public void DisableAll()
        {
            //Outline.Enabled = false;
            Fog.Enabled = false;
            //AmbientOcclusion.Enabled = false;
            LocalReflections.Enabled = false;
            DepthOfField.Enabled = false;
            //Bloom.Enabled = false;
            LightStreak.Enabled = false;
            LensFlare.Enabled = false;
            Antialiasing.Enabled = false;
            rangeCompress.Enabled = false;
            rangeDecompress.Enabled = false;
            colorTransformsGroup.Enabled = false;
        }
    }
}
