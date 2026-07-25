using System.Numerics;
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
    private CausticTransportGpuInterop? _interop;
    private CausticTransportPipeline? _pipeline;
    private CausticTransportCustomEffect? _effect;
    private AffineTransform2D? _outputTransform;
    private ID2D1Image? _outputTransformOutput;
    private bool _isFirst = true;
    private bool _hasOutput;
    private bool _hasOutputOffset;
    private Vector2 _outputOffset;
    private Parameters _parameters;

    public CausticTransportEffectProcessor(IGraphicsDevicesAndContext devices, CausticTransportEffect item)
        : base(devices)
    {
        _devices = devices;
        _item = item;
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _effect is null || _outputTransform is null || _outputTransformOutput is null || _interop is null || _pipeline is null || input is null)
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

        if (!_interop.MatchesSize(width, height))
            _outputTransform.SetInput(0, null, true);
        var resourcesChanged = _interop.EnsureResources(width, height);
        var outputOffset = new Vector2(bounds.Left, bounds.Top);
        if (!_hasOutputOffset || _outputOffset != outputOffset)
        {
            _outputTransform.TransformMatrix = Matrix3x2.CreateTranslation(outputOffset);
            _outputOffset = outputOffset;
            _hasOutputOffset = true;
        }
        _interop.RenderInput(input, bounds);

        var pipelineParameters = new CausticTransportPipeline.Parameters(
            (int)parameters.LightShape,
            parameters.Quality,
            Math.Clamp(parameters.Focus, 0f, 1f),
            Math.Clamp(parameters.Aperture, 0.05f, 1f),
            Math.Clamp(parameters.Dispersion, 0f, 1f),
            Math.Clamp(parameters.Roughness, 0f, 1f),
            Math.Max(parameters.Seed, 0));

        _interop.BeginCompute();
        try
        {
            _pipeline.Process(
                _interop.SourceTexture,
                _interop.OutputTexture,
                width,
                height,
                in pipelineParameters);
        }
        finally
        {
            _interop.EndCompute();
        }

        if (resourcesChanged || !_hasOutput)
        {
            _outputTransform.SetInput(0, _interop.OutputBitmap, true);
            _effect.SetInput(1, _outputTransformOutput, true);
        }
        _hasOutput = true;
        _parameters = parameters;
        _isFirst = false;
        return effectDescription.DrawDescription;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        var interop = CausticTransportGpuInterop.TryCreate(devices);
        if (interop is null)
            return null;
        var pipeline = CausticTransportPipeline.TryCreate(interop.Device);
        if (pipeline is null)
        {
            interop.Dispose();
            return null;
        }

        CausticTransportCustomEffect? effect = null;
        AffineTransform2D? outputTransform = null;
        ID2D1Image? outputTransformOutput = null;
        ID2D1Image? output = null;
        try
        {
            effect = new CausticTransportCustomEffect(devices);
            if (!effect.IsEnabled)
            {
                effect.Dispose();
                pipeline.Dispose();
                interop.Dispose();
                return null;
            }
            outputTransform = new AffineTransform2D(devices.DeviceContext)
            {
                BorderMode = BorderMode.Hard,
            };
            outputTransformOutput = outputTransform.Output;
            output = effect.Output;
            _interop = interop;
            _pipeline = pipeline;
            _effect = effect;
            _outputTransform = outputTransform;
            _outputTransformOutput = outputTransformOutput;
            disposer.Collect(effect);
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
            effect?.Dispose();
            pipeline.Dispose();
            interop.Dispose();
            throw;
        }
    }

    protected override void setInput(ID2D1Image? inputImage)
    {
        _effect?.SetInput(0, inputImage, true);
        if (!_hasOutput)
            _effect?.SetInput(1, inputImage, true);
    }

    protected override void ClearEffectChain()
    {
        _effect?.SetInput(0, null, true);
        _effect?.SetInput(1, null, true);
        _outputTransform?.SetInput(0, null, true);
        _isFirst = true;
        _hasOutput = false;
        _hasOutputOffset = false;
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                ClearEffectChain();
                _interop?.WaitForIdle();
                _pipeline?.Dispose();
                _pipeline = null;
                _interop?.Dispose();
                _interop = null;
            }
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
