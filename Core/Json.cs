using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WhiteMC.Core;

public static class Json
{
    /// <summary>Опции с отступами. AllowTrailingCommas — чтобы ручные конфиги
    /// с лишней запятой не роняли лаунчер.</summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented       = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        TypeInfoResolver    = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>Опции для десериализации без учёта регистра ключей.</summary>
    public static JsonSerializerOptions CaseInsensitive { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        TypeInfoResolver            = new DefaultJsonTypeInfoResolver()
    };
}