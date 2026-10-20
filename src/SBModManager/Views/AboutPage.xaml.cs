using Microsoft.UI.Xaml.Controls;

namespace SBModManager.Views;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private static string AssemblyVersion => typeof(AboutPage).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
