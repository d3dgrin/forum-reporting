using System.Text.Json;

namespace ForumReporting.Contracts.Messaging;

/// <summary>
/// The JSON wire format is part of the contract, so publisher and consumer must share these options.
/// </summary>
public static class ContractJson
{
    /// <summary>
    /// camelCase (Web defaults) and strict: missing constructor parameters or nulls in non-nullable
    /// properties throw <see cref="JsonException"/>, so a malformed message is rejected (dead-lettered)
    /// instead of being processed with default values. Read-only, so no service can change the wire format.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
