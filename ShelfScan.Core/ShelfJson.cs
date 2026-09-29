using System.Text.Json.Serialization;

namespace ShelfScan.Core;

/// <summary>
/// Source-generated System.Text.Json metadata. The serializer never reflects over types,
/// which is what full trimming and Native AOT require.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<Book>))]
internal sealed partial class ShelfJson : JsonSerializerContext
{
}
