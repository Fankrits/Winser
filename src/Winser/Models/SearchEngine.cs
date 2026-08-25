namespace Winser.Models;

/// <summary>
/// A search provider the address bar can hand a query to. <see cref="QueryTemplate"/> and
/// <see cref="SuggestionsTemplate"/> both use <c>{0}</c> as the URL-escaped query placeholder.
/// </summary>
public sealed record SearchEngine(string Id, string Name, string QueryTemplate, string? SuggestionsTemplate)
{
    public const string DefaultId = "duckduckgo";

    public string BuildSearchUrl(string query) =>
        string.Format(QueryTemplate, Uri.EscapeDataString(query));

    public string? BuildSuggestionsUrl(string query) => SuggestionsTemplate is null
        ? null
        : string.Format(SuggestionsTemplate, Uri.EscapeDataString(query));

    public static IReadOnlyList<SearchEngine> All { get; } =
    [
        new("duckduckgo", "DuckDuckGo",
            "https://duckduckgo.com/?q={0}",
            "https://duckduckgo.com/ac/?q={0}&type=list"),
        new("google", "Google",
            "https://www.google.com/search?q={0}",
            "https://suggestqueries.google.com/complete/search?client=firefox&q={0}"),
        new("bing", "Bing",
            "https://www.bing.com/search?q={0}",
            "https://api.bing.com/osjson.aspx?query={0}"),
        new("brave", "Brave Search",
            "https://search.brave.com/search?q={0}",
            null),
        new("ecosia", "Ecosia",
            "https://www.ecosia.org/search?q={0}",
            "https://ac.ecosia.org/autocomplete?q={0}&type=list"),
        new("startpage", "Startpage",
            "https://www.startpage.com/sp/search?query={0}",
            null),
    ];

    public static SearchEngine Default => Resolve(DefaultId);

    public static SearchEngine Resolve(string? id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.First(e => e.Id == DefaultId);
}
