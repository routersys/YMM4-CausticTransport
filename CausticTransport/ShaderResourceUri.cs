namespace CausticTransport;

internal static class ShaderResourceUri
{
    public static Uri Get(string shaderName) => new($"pack://application:,,,/CausticTransport;component/Resources/Shader/{shaderName}.cso", UriKind.Absolute);
}
