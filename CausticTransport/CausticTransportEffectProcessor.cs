using System.ComponentModel;
using System.Numerics;
using ComputeWeave;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;

namespace CausticTransport;

internal sealed class CausticTransportEffectProcessor : VideoEffectProcessorBase
{
    private readonly IGraphicsDevicesAndContext _devices;
    private readonly CausticTransportEffect _item;
    private ComputeExternalQueueScheduler? _scheduler;
    private CausticTransportInteropProvider? _interopProvider;
    private ComputeInteropDomain? _interopDomain;
    private CausticTransportResourceSet? _resourceSet;
    private ExternalTextureLease<ExternalDirect3D11TextureView>? _outputLease;
    private CausticTransportPipeline? _pipeline;
    private CausticTransportCustomEffect? _effect;
    private Crop? _outputCrop;
    private ID2D1Image? _outputCropOutput;
    private AffineTransform2D? _outputTransform;
    private ID2D1Image? _outputTransformOutput;
    private bool _isFirst = true;
    private bool _hasOutput;
    private bool _hasOutputOffset;
    private bool _hasCropRect;
    private Vector2 _outputOffset;
    private Vector4 _cropRect;
    private Parameters _parameters;

    public CausticTransportEffectProcessor(IGraphicsDevicesAndContext devices, CausticTransportEffect item)
        : base(devices)
    {
        _devices = devices;
        _item = item;
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        try
        {
            return UpdateCore(effectDescription);
        }
        catch (Exception exception)
        {
            CausticTransportTelemetry.Report(exception);
            throw;
        }
    }

    private DrawDescription UpdateCore(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _effect is null || _outputCrop is null || _outputTransform is null || _outputTransformOutput is null || _resourceSet is null || _interopProvider is null || _pipeline is null || input is null)
            return effectDescription.DrawDescription;

        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;
        var parameters = new Parameters(
            (float)(_item.Amount.GetValue(frame, length, fps) / 100.0),
            (float)(_item.Focus.GetValue(frame, length, fps) / 100.0),
            _item.LightShape,
            _item.Quality,
            (float)(_item.ApertureSize.GetValue(frame, length, fps) / 100.0),
            (float)(_item.Dispersion.GetValue(frame, length, fps) / 100.0),
            (float)(_item.Roughness.GetValue(frame, length, fps) / 100.0),
            _item.Seed);

        if (_isFirst || _parameters.Amount != parameters.Amount)
            _effect.Amount = parameters.Amount;

        if (parameters.Amount <= 0f)
        {
            _parameters = parameters;
            _isFirst = false;
            return effectDescription.DrawDescription;
        }

        if (parameters.Focus >= 1f)
        {
            _effect.Amount = 0f;
            _parameters = parameters;
            _isFirst = true;
            return effectDescription.DrawDescription;
        }

        var bounds = _devices.DeviceContext.GetImageLocalBounds(input);
        var widthValue = Math.Ceiling((double)bounds.Right - bounds.Left);
        var heightValue = Math.Ceiling((double)bounds.Bottom - bounds.Top);
        if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) ||
            !CausticTransportSettings.IsSupportedSize(widthValue, heightValue))
        {
            _effect.Amount = 0f;
            _isFirst = true;
            return effectDescription.DrawDescription;
        }
        var width = (int)widthValue;
        var height = (int)heightValue;

        if (!_resourceSet.TryEnsureSource(width, height, out _))
        {
            _effect.Amount = 0f;
            _isFirst = true;
            return effectDescription.DrawDescription;
        }

        RenderInput(new Vortice.RawRectF(bounds.Left, bounds.Top, bounds.Left + width, bounds.Top + height));

        var pipelineParameters = new CausticTransportPipeline.Parameters(
            (int)parameters.LightShape,
            parameters.Quality,
            Math.Clamp(parameters.Focus, 0f, 1f),
            Math.Clamp(parameters.Aperture, 0.05f, 1f),
            Math.Clamp(parameters.Dispersion, 0f, 1f),
            Math.Clamp(parameters.Roughness, 0f, 1f),
            Math.Max(parameters.Seed, 0));

        if (!OutputCovers(width, height))
            _outputCrop.SetInput(0, null, true);
        if (!EnsureOutput(width, height, out var outputChanged))
        {
            _effect.Amount = 0f;
            _isFirst = true;
            _hasOutput = false;
            return effectDescription.DrawDescription;
        }

        _pipeline.Process(
            _resourceSet.GetSourceComputeBinding(),
            _resourceSet.GetOutputComputeBinding(),
            width,
            height,
            in pipelineParameters);

        _outputLease ??= _resourceSet.AcquireOutputExternalViewLease();

        if (outputChanged || !_hasOutput)
        {
            using var outputBitmap = new ID2D1Bitmap1(_outputLease.DangerousGetView().AddRefBitmap());
            _outputCrop.SetInput(0, outputBitmap, true);
            _effect.SetInput(1, _outputTransformOutput, true);
        }
        var cropRect = new Vector4(0f, 0f, width, height);
        if (!_hasCropRect || _cropRect != cropRect)
        {
            _outputCrop.Rectangle = cropRect;
            _cropRect = cropRect;
            _hasCropRect = true;
        }
        var outputOffset = new Vector2(bounds.Left, bounds.Top);
        if (!_hasOutputOffset || _outputOffset != outputOffset)
        {
            _outputTransform.TransformMatrix = Matrix3x2.CreateTranslation(outputOffset);
            _outputOffset = outputOffset;
            _hasOutputOffset = true;
        }
        _hasOutput = true;
        _parameters = parameters;
        _isFirst = false;
        return effectDescription.DrawDescription;
    }

    private bool OutputCovers(int width, int height)
        => _outputLease is { IsDisposed: false } lease &&
           lease.Width >= width &&
           lease.Height >= height;

    private bool EnsureOutput(int width, int height, out bool changed)
    {
        changed = false;
        if (OutputCovers(width, height))
            return true;

        _outputLease?.Dispose();
        _outputLease = null;
        return _resourceSet!.TryEnsureOutput(width, height, out changed);
    }

    private void RenderInput(Vortice.RawRectF bounds)
    {
        var renderContext = _interopProvider!.RenderContext;
        using var borrow = _resourceSet!.BeginSourceExternalOperation();
        var previousTarget = renderContext.Target;
        using var sourceBitmap = new ID2D1Bitmap1(borrow.DangerousGetView().AddRefBitmap());
        renderContext.Target = sourceBitmap;
        renderContext.BeginDraw();
        renderContext.Clear(null);
        renderContext.DrawImage(
            input,
            new Vector2(-bounds.Left, -bounds.Top),
            null,
            InterpolationMode.NearestNeighbor,
            CompositeMode.SourceCopy);
        renderContext.EndDraw();
        renderContext.Target = previousTarget;
    }

    private void ReleaseInterop()
    {
        _outputLease?.Dispose();
        _outputLease = null;
        _pipeline?.Dispose();
        _pipeline = null;
        _resourceSet?.Dispose();
        _resourceSet?.WaitForDisposal();
        _resourceSet = null;
        _interopDomain?.Dispose();
        _interopDomain?.WaitForDisposal();
        _interopDomain = null;
        _interopProvider?.Dispose();
        _interopProvider = null;
        _scheduler?.Dispose();
        _scheduler = null;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        var scheduler = ComputeExternalQueueScheduler.Create();
        var interopProvider = CausticTransportInteropProvider.TryCreate(devices, scheduler, out var interopDevice);
        if (interopProvider is null || interopDevice is null)
        {
            scheduler.Dispose();
            return null;
        }

        _scheduler = scheduler;

        try
        {
            _interopProvider = interopProvider;
            _interopDomain = interopDevice.RegisterExternalDomain(interopProvider);
            _resourceSet = CausticTransportResourceSet.Create(interopDevice, _interopDomain);
            _pipeline = CausticTransportPipeline.TryCreate(interopDevice);
        }
        catch (Win32Exception)
        {
            ReleaseInterop();
            return null;
        }
        catch
        {
            ReleaseInterop();
            throw;
        }

        if (_pipeline is null)
        {
            ReleaseInterop();
            return null;
        }

        CausticTransportCustomEffect? effect = null;
        Crop? outputCrop = null;
        ID2D1Image? outputCropOutput = null;
        AffineTransform2D? outputTransform = null;
        ID2D1Image? outputTransformOutput = null;
        ID2D1Image? output = null;
        try
        {
            effect = new CausticTransportCustomEffect(devices);
            if (!effect.IsEnabled)
            {
                effect.Dispose();
                ReleaseInterop();
                return null;
            }
            outputCrop = new Crop(devices.DeviceContext);
            outputCropOutput = outputCrop.Output;
            outputTransform = new AffineTransform2D(devices.DeviceContext)
            {
                BorderMode = BorderMode.Hard,
            };
            outputTransform.SetInput(0, outputCropOutput, true);
            outputTransformOutput = outputTransform.Output;
            output = effect.Output;
            _effect = effect;
            _outputCrop = outputCrop;
            _outputCropOutput = outputCropOutput;
            _outputTransform = outputTransform;
            _outputTransformOutput = outputTransformOutput;
            disposer.Collect(effect);
            disposer.Collect(outputCrop);
            disposer.Collect(outputCropOutput);
            disposer.Collect(outputTransform);
            disposer.Collect(outputTransformOutput);
            disposer.Collect(output);
            return output;
        }
        catch
        {
            output?.Dispose();
            outputTransformOutput?.Dispose();
            outputTransform?.Dispose();
            outputCropOutput?.Dispose();
            outputCrop?.Dispose();
            effect?.Dispose();
            ReleaseInterop();
            throw;
        }
    }

    protected override void setInput(ID2D1Image? inputImage)
    {
        try
        {
            SetInputCore(inputImage);
        }
        catch (Exception exception)
        {
            CausticTransportTelemetry.Report(exception);
            throw;
        }
    }

    private void SetInputCore(ID2D1Image? inputImage)
    {
        _effect?.SetInput(0, inputImage, true);
        if (!_hasOutput)
            _effect?.SetInput(1, inputImage, true);
    }

    protected override void ClearEffectChain()
    {
        _effect?.SetInput(0, null, true);
        _effect?.SetInput(1, null, true);
        _outputCrop?.SetInput(0, null, true);
        _isFirst = true;
        _hasOutput = false;
        _hasOutputOffset = false;
        _hasCropRect = false;
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                ClearEffectChain();
                ReleaseInterop();
            }
        }
        catch (Exception exception)
        {
            CausticTransportTelemetry.Report(exception);
            throw;
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    private readonly record struct Parameters(
        float Amount,
        float Focus,
        CausticLightShape LightShape,
        CausticTransportQuality Quality,
        float Aperture,
        float Dispersion,
        float Roughness,
        int Seed);
}
