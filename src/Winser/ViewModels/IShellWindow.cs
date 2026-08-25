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

    void FocusAddressBar();

    void CloseWindow();
}
