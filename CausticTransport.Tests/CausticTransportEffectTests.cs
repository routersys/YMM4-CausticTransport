using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Json;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Project;

namespace CausticTransport.Tests;

public sealed class CausticTransportEffectTests
{
    static PropertyInfo Property(string name) => typeof(CausticTransportEffect).GetProperty(name)!;

    static T Attribute<T>(string property) where T : Attribute => Property(property).GetCustomAttribute<T>()!;

    static Animation[] Animations(CausticTransportEffect effect)
        => [effect.Amount, effect.Focus, effect.ApertureSize, effect.Dispersion, effect.Roughness];

    [Theory]
    [InlineData(nameof(CausticTransportEffect.Amount), 100d, 0d, 100d)]
    [InlineData(nameof(CausticTransportEffect.Focus), 50d, 0d, 100d)]
    [InlineData(nameof(CausticTransportEffect.ApertureSize), 50d, 5d, 100d)]
    [InlineData(nameof(CausticTransportEffect.Dispersion), 30d, 0d, 100d)]
    [InlineData(nameof(CausticTransportEffect.Roughness), 20d, 0d, 100d)]
    public void AnimatedParametersStartFromTheirDefaultsWithinTheirRange(string name, double defaultValue, double minimum, double maximum)
    {
        var effect = new CausticTransportEffect();

        var animation = (Animation)Property(name).GetValue(effect)!;

        Assert.Equal(defaultValue, animation.DefaultValue);
        Assert.Equal(minimum, animation.MinValue);
        Assert.Equal(maximum, animation.MaxValue);
        Assert.Equal(defaultValue, animation.GetValue(0, 1, EffectDescriptions.Fps));
    }

    [Fact]
    public void ShapeQualityAndSeedStartFromTheirDefaults()
    {
        var effect = new CausticTransportEffect();

        Assert.Equal(CausticLightShape.Plane, effect.LightShape);
        Assert.Equal(CausticTransportQuality.High, effect.Quality);
        Assert.Equal(0, effect.Seed);
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1234, 1234)]
    [InlineData(10000, 10000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void SeedNeverDropsBelowZero(int value, int expected)
    {
        var effect = new CausticTransportEffect { Seed = 5 };

        effect.Seed = value;

        Assert.Equal(expected, effect.Seed);
        Assert.False(effect.HasErrors);
    }

    [Fact]
    public void ChangingShapeQualityOrSeedNotifiesTheEditor()
    {
        var effect = new CausticTransportEffect();
        var changed = new List<string?>();
        effect.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        effect.LightShape = CausticLightShape.Circle;
        effect.Quality = CausticTransportQuality.Ultra;
        effect.Seed = 7;

        Assert.Equal([nameof(CausticTransportEffect.LightShape), nameof(CausticTransportEffect.Quality), nameof(CausticTransportEffect.Seed)], changed);
    }

    [Fact]
    public void AssigningAnUnchangedOrClampedValueDoesNotNotify()
    {
        var effect = new CausticTransportEffect();
        var changed = new List<string?>();
        effect.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        effect.LightShape = CausticLightShape.Plane;
        effect.Quality = CausticTransportQuality.High;
        effect.Seed = 0;
        effect.Seed = -1;

        Assert.Empty(changed);
    }

    [Fact]
    public void TheAnimatedParametersAreListedOnceAndReused()
    {
        var effect = new CausticTransportEffect();
        var method = typeof(CausticTransportEffect).GetMethod("GetAnimatables", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var first = (IEnumerable<IAnimatable>)method.Invoke(effect, null)!;
        var second = (IEnumerable<IAnimatable>)method.Invoke(effect, null)!;

        Assert.Same(first, second);
        Assert.Equal(Animations(effect), first);
    }

    [Fact]
    public void TheLabelIsTheLocalizedEffectName()
    {
        var effect = new CausticTransportEffect();

        Assert.Equal(Texts.CausticTransport, effect.Label);
    }

    [Fact]
    public void TheFiveNumericParametersReceiveTheAnimationParameters()
    {
        var effect = new CausticTransportEffect();

        effect.SetAnimationParameters(120, EffectDescriptions.Fps);

        Assert.All(Animations(effect), animation => Assert.Equal(120, animation.Length));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void NoExoFilterIsWrittenForAviUtl(int keyFrameIndex)
    {
        var effect = new CausticTransportEffect();

        var description = new ExoOutputDescription(new VideoInfo(), string.Empty, new AviUtlDirectories(string.Empty, string.Empty));

        Assert.Empty(effect.CreateExoVideoFilters(keyFrameIndex, description));
    }

    [Fact]
    public void TheEffectIsRegisteredForFilteringAndDecorationWithoutAviUtlSupport()
    {
        var attribute = typeof(CausticTransportEffect).GetCustomAttribute<VideoEffectAttribute>()!;

        Assert.Equal(nameof(Texts.CausticTransport), attribute.Name);
        Assert.Equal([VideoEffectCategories.Filtering, VideoEffectCategories.Decoration], attribute.Categories);
        Assert.Equal([nameof(Texts.TagCaustic), nameof(Texts.TagLight), nameof(Texts.TagTransition)], attribute.Keywords);
        Assert.False(attribute.IsAviUtlSupported);
        Assert.True(attribute.IsEffectItemSupported);
        Assert.Equal(typeof(Texts), attribute.ResourceType);
        Assert.Equal(Texts.CausticTransport, attribute.GetName());
    }

    [Theory]
    [InlineData(nameof(CausticTransportEffect.Amount), nameof(Texts.BasicGroup), nameof(Texts.Amount), nameof(Texts.AmountDescription), 0)]
    [InlineData(nameof(CausticTransportEffect.Focus), nameof(Texts.BasicGroup), nameof(Texts.Focus), nameof(Texts.FocusDescription), 1)]
    [InlineData(nameof(CausticTransportEffect.Quality), nameof(Texts.BasicGroup), nameof(Texts.Quality), nameof(Texts.QualityDescription), 2)]
    [InlineData(nameof(CausticTransportEffect.LightShape), nameof(Texts.LightGroup), nameof(Texts.LightShape), nameof(Texts.LightShapeDescription), 10)]
    [InlineData(nameof(CausticTransportEffect.ApertureSize), nameof(Texts.LightGroup), nameof(Texts.ApertureSize), nameof(Texts.ApertureSizeDescription), 11)]
    [InlineData(nameof(CausticTransportEffect.Dispersion), nameof(Texts.OpticsGroup), nameof(Texts.Dispersion), nameof(Texts.DispersionDescription), 20)]
    [InlineData(nameof(CausticTransportEffect.Roughness), nameof(Texts.OpticsGroup), nameof(Texts.Roughness), nameof(Texts.RoughnessDescription), 21)]
    [InlineData(nameof(CausticTransportEffect.Seed), nameof(Texts.OpticsGroup), nameof(Texts.Seed), nameof(Texts.SeedDescription), 22)]
    public void EveryParameterIsDisplayedInItsGroupInOrder(string property, string group, string name, string description, int order)
    {
        var display = Attribute<DisplayAttribute>(property);

        Assert.Equal(group, display.GroupName);
        Assert.Equal(name, display.Name);
        Assert.Equal(description, display.Description);
        Assert.Equal(order, display.Order);
        Assert.Equal(typeof(Texts), display.ResourceType);
    }

    [Theory]
    [InlineData(nameof(CausticTransportEffect.Amount), 0d, 100d)]
    [InlineData(nameof(CausticTransportEffect.Focus), 0d, 100d)]
    [InlineData(nameof(CausticTransportEffect.ApertureSize), 5d, 100d)]
    [InlineData(nameof(CausticTransportEffect.Dispersion), 0d, 100d)]
    [InlineData(nameof(CausticTransportEffect.Roughness), 0d, 100d)]
    public void AnimatedParametersAreEditedAsPercentagesWithAnimationSliders(string property, double minimum, double maximum)
    {
        var slider = Attribute<AnimationSliderAttribute>(property);

        Assert.Equal("F1", slider.StringFormat);
        Assert.Equal("%", slider.UnitText);
        Assert.Equal(minimum, slider.DefaultMin);
        Assert.Equal(maximum, slider.DefaultMax);
    }

    [Theory]
    [InlineData(nameof(CausticTransportEffect.Quality))]
    [InlineData(nameof(CausticTransportEffect.LightShape))]
    public void TheChoicesAreMadeFromACombo(string property)
    {
        Assert.NotNull(Attribute<EnumComboBoxAttribute>(property));
    }

    [Fact]
    public void TheQualitiesAndShapesAreListedInOrder()
    {
        Assert.Equal([CausticTransportQuality.Balanced, CausticTransportQuality.High, CausticTransportQuality.Ultra], Enum.GetValues<CausticTransportQuality>());
        Assert.Equal([CausticLightShape.Plane, CausticLightShape.Circle, CausticLightShape.HorizontalSlit, CausticLightShape.VerticalSlit], Enum.GetValues<CausticLightShape>());
        Assert.Equal([0, 1, 2], Enum.GetValues<CausticTransportQuality>().Select(quality => (int)quality));
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<CausticLightShape>().Select(shape => (int)shape));
    }

    [Theory]
    [InlineData(CausticTransportQuality.Balanced, nameof(Texts.QualityBalanced), nameof(Texts.QualityBalancedDescription))]
    [InlineData(CausticTransportQuality.High, nameof(Texts.QualityHigh), nameof(Texts.QualityHighDescription))]
    [InlineData(CausticTransportQuality.Ultra, nameof(Texts.QualityUltra), nameof(Texts.QualityUltraDescription))]
    public void EveryQualityIsDisplayedWithItsLocalizedName(CausticTransportQuality quality, string name, string description)
    {
        var display = typeof(CausticTransportQuality).GetField(quality.ToString())!.GetCustomAttribute<DisplayAttribute>()!;

        Assert.Equal(name, display.Name);
        Assert.Equal(description, display.Description);
        Assert.Equal(typeof(Texts), display.ResourceType);
    }

    [Theory]
    [InlineData(CausticLightShape.Plane, nameof(Texts.ShapePlane), nameof(Texts.ShapePlaneDescription))]
    [InlineData(CausticLightShape.Circle, nameof(Texts.ShapeCircle), nameof(Texts.ShapeCircleDescription))]
    [InlineData(CausticLightShape.HorizontalSlit, nameof(Texts.ShapeHorizontalSlit), nameof(Texts.ShapeHorizontalSlitDescription))]
    [InlineData(CausticLightShape.VerticalSlit, nameof(Texts.ShapeVerticalSlit), nameof(Texts.ShapeVerticalSlitDescription))]
    public void EveryShapeIsDisplayedWithItsLocalizedName(CausticLightShape shape, string name, string description)
    {
        var display = typeof(CausticLightShape).GetField(shape.ToString())!.GetCustomAttribute<DisplayAttribute>()!;

        Assert.Equal(name, display.Name);
        Assert.Equal(description, display.Description);
        Assert.Equal(typeof(Texts), display.ResourceType);
    }

    [Fact]
    public void TheSeedIsEditedWithoutAUnitFromZero()
    {
        var slider = Attribute<TextBoxSliderAttribute>(nameof(CausticTransportEffect.Seed));
        var range = Attribute<RangeAttribute>(nameof(CausticTransportEffect.Seed));

        Assert.Equal("F0", slider.StringFormat);
        Assert.Equal(string.Empty, slider.UnitText);
        Assert.Equal(0d, slider.DefaultMin);
        Assert.Equal(10000d, slider.DefaultMax);
        Assert.Equal(0, range.Minimum);
        Assert.Equal(int.MaxValue, range.Maximum);
        Assert.Equal(0, Attribute<DefaultValueAttribute>(nameof(CausticTransportEffect.Seed)).Value);
    }

    [Fact]
    public void EverySettingSurvivesAProjectRoundTrip()
    {
        var effect = new CausticTransportEffect { LightShape = CausticLightShape.VerticalSlit, Quality = CausticTransportQuality.Ultra, Seed = 42 };
        var values = new[] { 55d, 45d, 70d, 20d, 65d };
        foreach (var (animation, value) in Animations(effect).Zip(values))
            animation.Values[0].Value = value;

        var clone = Json.GetClone(effect)!;

        Assert.NotSame(effect, clone);
        Assert.Equal(CausticLightShape.VerticalSlit, clone.LightShape);
        Assert.Equal(CausticTransportQuality.Ultra, clone.Quality);
        Assert.Equal(42, clone.Seed);
        Assert.Equal(values, Animations(clone).Select(animation => animation.GetValue(0, 1, EffectDescriptions.Fps)));
    }
}
