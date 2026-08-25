using Winser.Helpers;

namespace Winser.ViewModels;

public enum SuggestionKind
{
    Search,
    Navigate,
    History,
    Bookmark,
}

/// <summary>One row in the address bar's dropdown.</summary>
public sealed record AddressSuggestion(SuggestionKind Kind, string Text, string Description, string Target)
{
    public string Glyph => Kind switch
    {
        SuggestionKind.Search => Glyphs.Search,
        SuggestionKind.Navigate => Glyphs.Globe,
        SuggestionKind.Bookmark => Glyphs.FavoriteFilled,
        _ => Glyphs.History,
    };
}
