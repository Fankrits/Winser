using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using Winser.Helpers;
using Winser.Models;
using Winser.Services;
using Winser.ViewModels;

namespace Winser.Views;

/// <summary>
/// The chrome around one tab: toolbar, bookmarks bar, find bar, and whichever content the tab
/// is showing — a live page or one of Winser's own pages.
/// </summary>
public sealed partial class BrowserTabPage : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(BrowserTabViewModel),
        typeof(BrowserTabPage),
        new PropertyMetadata(null, OnViewModelChanged));

    public BrowserTabPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public BrowserTabViewModel? ViewModel
    {
        get => (BrowserTabViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>Focuses the address bar and selects what is in it, the way Ctrl+L should.</summary>
    public void FocusAddress()
    {
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.FindDescendant<TextBox>()?.SelectAll();
    }

    // -------------------------------------------------------------------- lifecycle

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var page = (BrowserTabPage)d;

        page.Unsubscribe(e.OldValue as BrowserTabViewModel);

        // Only if already loaded; otherwise OnLoaded does it. Subscribing here unconditionally
        // would leak a handler on a page that never reaches the tree.
        if (page.IsLoaded)
        {
            page.Subscribe(e.NewValue as BrowserTabViewModel);
        }

        // The tab is handed to this control after construction, so the one-time bindings that
        // ran during InitializeComponent need a second pass.
        page.Bindings.Update();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // TabView shows the selected tab through a single content presenter, so switching tabs
        // unloads this page and switching back reloads it. Re-subscribing on every load - not
        // just when the view model is first handed over - is what keeps Ctrl+L and the find bar
        // working on a tab the user has left and come back to.
        Subscribe(ViewModel);

        // Every tab's page loads, not just the visible one, so a background new tab must not
        // steal the caret from whatever the user is looking at.
        if (ViewModel is { IsNewTabPage: true } tab && ReferenceEquals(tab.Shell.SelectedTab, tab))
        {
            FocusAddress();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe(ViewModel);

    private void Subscribe(BrowserTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        // Detach first so a repeated load cannot double-subscribe.
        tab.AddressFocusRequested -= OnAddressFocusRequested;
        tab.PropertyChanged -= OnViewModelPropertyChanged;

        tab.AddressFocusRequested += OnAddressFocusRequested;
        tab.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void Unsubscribe(BrowserTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        tab.AddressFocusRequested -= OnAddressFocusRequested;
        tab.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnAddressFocusRequested(object? sender, EventArgs e) => FocusAddress();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BrowserTabViewModel.IsFindOpen) || ViewModel is not { IsFindOpen: true })
        {
            return;
        }

        // FindBar is x:Load-deferred, so it only exists after the layout pass that follows.
        DispatcherQueue.TryEnqueue(() =>
        {
            FindBox?.Focus(FocusState.Programmatic);
            FindBox?.SelectAll();
        });
    }

    // ------------------------------------------------------------------ address bar

    private void OnAddressTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel?.UpdateSuggestions(sender.Text);
        }
    }

    private void OnAddressQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (ViewModel is not { } tab)
        {
            return;
        }

        if (args.ChosenSuggestion is AddressSuggestion suggestion)
        {
            tab.NavigateResolved(suggestion.Target);
        }
        else
        {
            tab.Navigate(args.QueryText);
        }

        tab.IsAddressFocused = false;
        tab.Suggestions.Clear();
        WebContent?.FocusContent();
    }

    private void OnAddressGotFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } tab)
        {
            tab.IsAddressFocused = true;
        }
    }

    private void OnAddressLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } tab)
        {
            return;
        }

        tab.IsAddressFocused = false;

        // Put back whatever the page's real address is if the edit was abandoned.
        AddressBox.Text = UrlHelper.ForDisplay(tab.Url);
    }

    // --------------------------------------------------------------------- find bar

    private void OnFindBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is not { } tab)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter:
                var shift = InputKeyboardSource
                    .GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(CoreVirtualKeyStates.Down);
                if (shift)
                {
                    tab.FindPreviousCommand.Execute(null);
                }
                else
                {
                    tab.FindNextCommand.Execute(null);
                }

                e.Handled = true;
                break;

            case VirtualKey.Escape:
                tab.CloseFindCommand.Execute(null);
                WebContent?.FocusContent();
                e.Handled = true;
                break;
        }
    }

    // --------------------------------------------------------------- bookmarks bar

    private void OnBookmarkClick(object sender, RoutedEventArgs e)
    {
        if (BookmarkOf(sender) is { } bookmark)
        {
            ViewModel?.NavigateResolved(bookmark.Url);
        }
    }

    private void OnBookmarkOpenInNewTab(object sender, RoutedEventArgs e)
    {
        if (BookmarkOf(sender) is { } bookmark)
        {
            ViewModel?.Shell.OpenInNewTab(bookmark.Url, background: true);
        }
    }

    private void OnBookmarkRemove(object sender, RoutedEventArgs e)
    {
        if (BookmarkOf(sender) is { } bookmark)
        {
            AppServices.Bookmarks.Remove(bookmark.Url);
        }
    }

    private void OnBookmarkPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var kind = e.GetCurrentPoint((UIElement)sender).Properties.PointerUpdateKind;
        if (kind != PointerUpdateKind.MiddleButtonPressed || BookmarkOf(sender) is not { } bookmark)
        {
            return;
        }

        ViewModel?.Shell.OpenInNewTab(bookmark.Url, background: true);
        e.Handled = true;
    }

    private static Bookmark? BookmarkOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as Bookmark;
}
