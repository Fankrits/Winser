using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winser.Models;
using Winser.Services;
using Winser.ViewModels;

namespace Winser.Views;

/// <summary>The <c>winser://bookmarks</c> manager.</summary>
public sealed partial class BookmarksView : UserControl
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab),
        typeof(BrowserTabViewModel),
        typeof(BookmarksView),
        new PropertyMetadata(null));

    public BookmarksView() => InitializeComponent();

    public ObservableCollection<Bookmark> Items => AppServices.Bookmarks.Items;

    public BrowserTabViewModel? Tab
    {
        get => (BrowserTabViewModel?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    private void OnBookmarkClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Bookmark bookmark)
        {
            Tab?.NavigateResolved(bookmark.Url);
        }
    }

    private void OnRemoveBookmark(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Bookmark bookmark)
        {
            AppServices.Bookmarks.Remove(bookmark.Url);
        }
    }

    /// <summary>
    /// Bookmarks the page in the tab the user came from, which is the previously selected tab
    /// — this tab is the manager itself.
    /// </summary>
    private void OnAddCurrentPage(object sender, RoutedEventArgs e)
    {
        var source = Tab?.Shell.Tabs.FirstOrDefault(t => t.IsWeb && !t.IsNewTabPage);
        if (source is null)
        {
            return;
        }

        AppServices.Bookmarks.Add(source.Url, source.Title);
    }

    private async void OnEditBookmark(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Bookmark bookmark)
        {
            return;
        }

        var title = new TextBox { Header = "Name", Text = bookmark.Title };
        var url = new TextBox { Header = "Address", Text = bookmark.Url };
        var folder = new TextBox
        {
            Header = "Folder (optional)",
            Text = bookmark.Folder ?? string.Empty,
            PlaceholderText = "Leave empty to pin to the bookmarks bar",
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Edit bookmark",
            Content = new StackPanel { Spacing = 12, Children = { title, url, folder } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(url.Text))
        {
            return;
        }

        AppServices.Bookmarks.Update(bookmark, title.Text.Trim(), url.Text.Trim(), folder.Text.Trim());
    }
}
