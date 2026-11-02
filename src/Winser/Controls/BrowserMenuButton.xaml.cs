using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winser.ViewModels;

namespace Winser.Controls;

public sealed partial class BrowserMenuButton : UserControl
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab), typeof(BrowserTabViewModel), typeof(BrowserMenuButton), new PropertyMetadata(null, OnTabChanged));

    public BrowserMenuButton() => InitializeComponent();

    public BrowserTabViewModel? Tab
    {
        get => (BrowserTabViewModel?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    private static void OnTabChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((BrowserMenuButton)sender).Bindings.Update();
}
