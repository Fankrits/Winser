using Windows.System;

namespace Winser.Helpers;

/// <summary>Translates DOM <c>KeyboardEvent.key</c> values back into WinRT virtual keys.</summary>
public static class KeyNames
{
    public static VirtualKey? FromJavaScript(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            return c switch
            {
                >= 'A' and <= 'Z' => (VirtualKey)c,
                >= '0' and <= '9' => (VirtualKey)c,
                '+' or '=' => (VirtualKey)187,
                '-' or '_' => (VirtualKey)189,
                _ => null,
            };
        }

        if (key[0] == 'F' && int.TryParse(key.AsSpan(1), out var index) && index is >= 1 and <= 12)
        {
            return VirtualKey.F1 + (index - 1);
        }

        return key switch
        {
            "Tab" => VirtualKey.Tab,
            "Escape" => VirtualKey.Escape,
            "ArrowLeft" => VirtualKey.Left,
            "ArrowRight" => VirtualKey.Right,
            "Home" => VirtualKey.Home,
            _ => null,
        };
    }
}
