using Avalonia.Controls;
using Avalonia.Platform;

namespace TinyBoss.Installer;

public partial class InstallerWindow : Window
{
    public InstallerWindow()
    {
        InitializeComponent();
        DataContext = new InstallerViewModel();

        try
        {
            var uri = new Uri("avares://TinyBoss.Installer/Assets/TinyBoss.ico");
            using var stream = AssetLoader.Open(uri);
            Icon = new WindowIcon(stream);
        }
        catch { /* non-fatal */ }
    }
}
