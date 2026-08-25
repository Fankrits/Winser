using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winser.Models;
using Winser.ViewModels;

namespace Winser.Views;

/// <summary>The <c>winser://history</c> page.</summary>
public sealed partial class HistoryView : UserControl
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab),
        typeof(BrowserTabViewModel),
        typeof(HistoryView),
        new PropertyMetadata(null));

    public HistoryView()
    {
        InitializeComponent();

        GroupedHistory.Source = ViewModel.Groups;
        HistoryList.ItemsSource = GroupedHistory.View;

        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public HistoryViewModel ViewModel { get; } = new();

    public BrowserTabViewModel? Tab
    {
        get => (BrowserTabViewModel?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    private void OnEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryEntry entry)
        {
            Tab?.NavigateResolved(entry.Url);
        }
    }

    private void OnRemoveEntry(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryEntry entry)
        {
            ViewModel.Remove(entry);
        }
    }

    private async void OnClearAll(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear all history?",
            Content = "Every page Winser has recorded on this device will be removed. This cannot be undone.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ClearAllCommand.Execute(null);
        }
    }
}
