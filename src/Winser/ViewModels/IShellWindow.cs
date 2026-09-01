using Microsoft.UI.Xaml;

namespace Winser.ViewModels;

/// <summary>What a browser window can do on behalf of its view model.</summary>
public interface IShellWindow
{
    XamlRoot? XamlRoot { get; }

    /// <summary>Needed by WinRT pickers and dialogs, which want an owner HWND.</summary>
    nint WindowHandle { get; }

    bool IsFullScreen { get; }

    void SetFullScreen(bool fullScreen);

    /// <summary>
    /// Re-derives the vertical tab pane's chrome (width, drag region, whether the native
    /// horizontal strip is hidden) from the view model's current state. Cheap and idempotent,
    /// so callers never need to know which of several vertical-tabs properties actually changed.
    /// </summary>
    void RefreshTabChrome();

    void FocusAddressBar();

    void CloseWindow();
}
