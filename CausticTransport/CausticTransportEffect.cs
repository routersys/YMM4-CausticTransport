using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

namespace CausticTransport;

[VideoEffect(nameof(Texts.CausticTransport), [VideoEffectCategories.Filtering, VideoEffectCategories.Decoration], [nameof(Texts.TagCaustic), nameof(Texts.TagLight), nameof(Texts.TagTransition)], IsAviUtlSupported = false, ResourceType = typeof(Texts))]
public sealed class CausticTransportEffect : VideoEffectBase
{
    public override string Label => Texts.CausticTransport;

    public CausticTransportEffect()
    {
        CausticTransportUpdateNotifier.EnsureCheckedOnce();
    }

    [Display(GroupName = nameof(Texts.BasicGroup), Name = nameof(Texts.Amount), Description = nameof(Texts.AmountDescription), Order = 0, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation Amount { get; } = new Animation(100, 0, 100);

    [Display(GroupName = nameof(Texts.BasicGroup), Name = nameof(Texts.Focus), Description = nameof(Texts.FocusDescription), Order = 1, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation Focus { get; } = new Animation(50, 0, 100);

    [Display(GroupName = nameof(Texts.BasicGroup), Name = nameof(Texts.Quality), Description = nameof(Texts.QualityDescription), Order = 2, ResourceType = typeof(Texts))]
    [EnumComboBox]
    public CausticTransportQuality Quality { get => _quality; set => Set(ref _quality, value); }
    private CausticTransportQuality _quality = CausticTransportQuality.High;

    [Display(GroupName = nameof(Texts.LightGroup), Name = nameof(Texts.LightShape), Description = nameof(Texts.LightShapeDescription), Order = 10, ResourceType = typeof(Texts))]
    [EnumComboBox]
    public CausticLightShape LightShape { get => _lightShape; set => Set(ref _lightShape, value); }
    private CausticLightShape _lightShape = CausticLightShape.Plane;

    [Display(GroupName = nameof(Texts.LightGroup), Name = nameof(Texts.ApertureSize), Description = nameof(Texts.ApertureSizeDescription), Order = 11, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 5, 100)]
    public Animation ApertureSize { get; } = new Animation(50, 5, 100);

    [Display(GroupName = nameof(Texts.OpticsGroup), Name = nameof(Texts.Dispersion), Description = nameof(Texts.DispersionDescription), Order = 20, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation Dispersion { get; } = new Animation(30, 0, 100);

    [Display(GroupName = nameof(Texts.OpticsGroup), Name = nameof(Texts.Roughness), Description = nameof(Texts.RoughnessDescription), Order = 21, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation Roughness { get; } = new Animation(20, 0, 100);

    [Display(GroupName = nameof(Texts.OpticsGroup), Name = nameof(Texts.Seed), Description = nameof(Texts.SeedDescription), Order = 22, ResourceType = typeof(Texts))]
    [Range(0, int.MaxValue)]
    [DefaultValue(0)]
    [TextBoxSlider("F0", "", 0, 10000)]
    public int Seed
    {
        get => _seed;
        set => Set(ref _seed, Math.Max(value, 0));
    }
    private int _seed;

    private IAnimatable[]? _animatables;

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new CausticTransportEffectProcessor(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => _animatables ??= [Amount, Focus, ApertureSize, Dispersion, Roughness];
}
