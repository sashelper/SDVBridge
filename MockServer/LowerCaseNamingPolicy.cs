using System.Text.Json;

namespace MockServer;

internal sealed class LowerCaseNamingPolicy : JsonNamingPolicy
{
    public static LowerCaseNamingPolicy Instance { get; } = new LowerCaseNamingPolicy();

    private LowerCaseNamingPolicy()
    {
    }

    public override string ConvertName(string name)
    {
        return name?.ToLowerInvariant() ?? string.Empty;
    }
}
