using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WhiteMC.Core;

public static class Json
{
    /// <summary>Опции с отступами. Явно задаём TypeInfoResolver — иначе .NET 8 может бросить
    /// "must specify a TypeInfoResolver setting before being marked as read-only".</summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>Опции для десериализации без учёта регистра ключей.</summary>
    public static JsonSerializerOptions CaseInsensitive { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}