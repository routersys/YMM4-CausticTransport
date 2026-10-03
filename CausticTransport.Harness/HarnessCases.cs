using System.Globalization;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Json;

namespace CausticTransport.Harness;

internal static class HarnessCases
{
    public static IEnumerable<(string Name, CausticTransportEffect Effect, IReadOnlyList<int> Frames)> All()
    {
        yield return ("default", Create(), [0]);
        yield return ("focus-animated-frames", Create(effect => effect.Focus.CopyFrom(Linear(20d, 80d))), [0, 100, 200, 299]);
        yield return ("amount-0", Create(effect => effect.Amount.Values[0].Value = 0), [0]);
        yield return ("amount-50", Create(effect => effect.Amount.Values[0].Value = 50), [0]);
        yield return ("focus-0", Create(effect => effect.Focus.Values[0].Value = 0), [0]);
        yield return ("focus-90", Create(effect => effect.Focus.Values[0].Value = 90), [0]);
        yield return ("quality-balanced", Create(effect => effect.Quality = CausticTransportQuality.Balanced), [0]);
        yield return ("quality-ultra", Create(effect => effect.Quality = CausticTransportQuality.Ultra), [0]);
        yield return ("shape-circle", Create(effect => effect.LightShape = CausticLightShape.Circle), [0]);
        yield return ("shape-horizontal-slit", Create(effect => effect.LightShape = CausticLightShape.HorizontalSlit), [0]);
        yield return ("shape-vertical-slit", Create(effect => effect.LightShape = CausticLightShape.VerticalSlit), [0]);
        yield return ("circle-aperture-5", Create(effect =>
        {
            effect.LightShape = CausticLightShape.Circle;
            effect.ApertureSize.Values[0].Value = 5;
        }), [0]);
        yield return ("circle-aperture-100", Create(effect =>
        {
            effect.LightShape = CausticLightShape.Circle;
            effect.ApertureSize.Values[0].Value = 100;
        }), [0]);
        yield return ("dispersion-0", Create(effect => effect.Dispersion.Values[0].Value = 0), [0]);
        yield return ("dispersion-100", Create(effect => effect.Dispersion.Values[0].Value = 100), [0]);
        yield return ("roughness-0", Create(effect => effect.Roughness.Values[0].Value = 0), [0]);
        yield return ("roughness-100", Create(effect => effect.Roughness.Values[0].Value = 100), [0]);
        yield return ("seed-42", Create(effect => effect.Seed = 42), [0]);
    }

    public static IEnumerable<(string Name, Func<CausticTransportEffect> Create, Action<CausticTransportEffect> Change, int Frame)> Transitions()
    {
        yield return ("amount-100-to-0", () => Create(), effect => effect.Amount.Values[0].Value = 0, 0);
        yield return ("amount-0-to-100", () => Create(effect => effect.Amount.Values[0].Value = 0), effect => effect.Amount.Values[0].Value = 100, 0);
        yield return ("amount-100-to-50", () => Create(), effect => effect.Amount.Values[0].Value = 50, 0);
        yield return ("focus-50-to-100", () => Create(), effect => effect.Focus.Values[0].Value = 100, 0);
        yield return ("focus-100-to-50", () => Create(effect => effect.Focus.Values[0].Value = 100), effect => effect.Focus.Values[0].Value = 50, 0);
        yield return ("focus-50-to-0", () => Create(), effect => effect.Focus.Values[0].Value = 0, 0);
        yield return ("quality-high-to-ultra", () => Create(), effect => effect.Quality = CausticTransportQuality.Ultra, 0);
        yield return ("quality-high-to-balanced", () => Create(), effect => effect.Quality = CausticTransportQuality.Balanced, 0);
        yield return ("shape-plane-to-circle", () => Create(), effect => effect.LightShape = CausticLightShape.Circle, 0);
        yield return ("shape-circle-to-slit", () => Create(effect => effect.LightShape = CausticLightShape.Circle), effect => effect.LightShape = CausticLightShape.HorizontalSlit, 0);
        yield return ("aperture-50-to-100", () => Create(effect => effect.LightShape = CausticLightShape.Circle), effect => effect.ApertureSize.Values[0].Value = 100, 0);
        yield return ("dispersion-30-to-0", () => Create(), effect => effect.Dispersion.Values[0].Value = 0, 0);
        yield return ("dispersion-0-to-100", () => Create(effect => effect.Dispersion.Values[0].Value = 0), effect => effect.Dispersion.Values[0].Value = 100, 0);
        yield return ("roughness-20-to-100", () => Create(), effect => effect.Roughness.Values[0].Value = 100, 0);
        yield return ("roughness-20-to-0", () => Create(), effect => effect.Roughness.Values[0].Value = 0, 0);
        yield return ("seed-0-to-42", () => Create(), effect => effect.Seed = 42, 0);
    }

    public static IEnumerable<(string Name, CausticTransportEffect Effect)> Benchmarks()
    {
        yield return ("quality-balanced", Create(effect => effect.Quality = CausticTransportQuality.Balanced));
        yield return ("default", Create());
        yield return ("quality-ultra", Create(effect => effect.Quality = CausticTransportQuality.Ultra));
        yield return ("shape-circle", Create(effect => effect.LightShape = CausticLightShape.Circle));
        yield return ("dispersion-0", Create(effect => effect.Dispersion.Values[0].Value = 0));
        yield return ("focus-animated", Create(effect => effect.Focus.CopyFrom(Linear(20d, 80d))));
        yield return ("aperture-animated", Create(effect =>
        {
            effect.LightShape = CausticLightShape.Circle;
            effect.ApertureSize.CopyFrom(Linear(30d, 80d));
        }));
        yield return ("amount-0", Create(effect => effect.Amount.Values[0].Value = 0));
    }

    static Animation Linear(double from, double to)
        => Json.LoadFromText<Animation>(string.Create(CultureInfo.InvariantCulture, $$"""{"AnimationType":"直線移動","Values":[{"Value":{{from}}},{"Value":{{to}}}]}""")) ?? throw new HarnessException("アニメーションを読み込めません。");

    public static CausticTransportEffect Create(Action<CausticTransportEffect>? configure = null)
    {
        var effect = new CausticTransportEffect();
        configure?.Invoke(effect);
        return effect;
    }
}
