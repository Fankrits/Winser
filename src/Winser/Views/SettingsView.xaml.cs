using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Winser.Models;
using Winser.Services;
using Winser.ViewModels;

namespace Winser.Views;

/// <summary>The <c>winser://settings</c> page.</summary>
public sealed partial class SettingsView : UserControl
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab),
        typeof(BrowserTabViewModel),
        typeof(SettingsView),
        new PropertyMetadata(null));

    public SettingsView() => InitializeComponent();

    public SettingsViewModel ViewModel { get; } = new();

    public BrowserTabViewModel? Tab
    {
        get => (BrowserTabViewModel?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    private void OnForgetPermission(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SitePermission permission)
        {
            AppServices.Permissions.Revoke(permission);
        }
    }

    private async void OnChooseDownloadFolder(object sender, RoutedEventArgs e)
    {
        if (Tab?.Shell.WindowHandle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            return;
        }

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.DownloadFolder = folder.Path;
        }
    }

    private async void OnClearBrowsingData(object sender, RoutedEventArgs e)
    {
        var includeHistory = new CheckBox { Content = "Also clear browsing history", IsChecked = true };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear browsing data",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Cookies, cached files and site data will be removed for every site. " +
                               "You will be signed out of most websites.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    includeHistory,
                },
            },
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var cleared = Tab is null || await Tab.Shell.ClearSiteDataAsync();

        if (includeHistory.IsChecked == true)
        {
            AppServices.History.Clear();
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = cleared ? "Browsing data cleared" : "Partly cleared",
            Content = cleared
                ? "Cookies, cache and site data have been removed."
                : "History was cleared. Open a web page first so Winser can clear cookies and cache too.",
            CloseButtonText = "OK",
        }.ShowAsync();
    }
}
