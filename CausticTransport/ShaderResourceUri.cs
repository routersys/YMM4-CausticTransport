namespace CausticTransport;

internal static class ShaderResourceUri
{
    public static Uri Get(string shaderName) => new($"pack://application:,,,/CausticTransport;component/Shaders/{shaderName}.cso", UriKind.Absolute);
}
