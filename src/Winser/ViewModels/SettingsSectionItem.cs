using Winser.Models;

namespace Winser.ViewModels;

/// <summary>One row in the <c>winser://settings</c> sidebar.</summary>
public sealed record SettingsSectionItem(SettingsSection Section, string Label, string Glyph);
