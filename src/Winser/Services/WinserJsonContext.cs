using System.Text.Json.Serialization;
using Winser.Models;

namespace Winser.Services;

/// <summary>
/// Source-generated serializers so persistence stays reflection-free (and trim/AOT clean if
/// the project is ever published that way).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(List<Bookmark>))]
[JsonSerializable(typeof(List<HistoryEntry>))]
[JsonSerializable(typeof(List<DownloadRecord>))]
[JsonSerializable(typeof(List<TopSite>))]
[JsonSerializable(typeof(List<SitePermission>))]
public sealed partial class WinserJsonContext : JsonSerializerContext;
