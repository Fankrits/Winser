using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winser.Helpers;
using Winser.Services;
using Winser.ViewModels;

namespace Winser.Views;

/// <summary>The <c>winser://downloads</c> page.</summary>
public sealed partial class DownloadsView : UserControl
{
    public DownloadsView() => InitializeComponent();

    public ObservableCollection<DownloadItem> Items => AppServices.Downloads.Items;

    private void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
        {
            AppServices.Downloads.Remove(item);
        }
    }

    private void OnClearList(object sender, RoutedEventArgs e) => AppServices.Downloads.ClearList();

    private void OnOpenFolder(object sender, RoutedEventArgs e) =>
        SystemShell.OpenFolder(AppServices.Settings.EffectiveDownloadFolder);
}
