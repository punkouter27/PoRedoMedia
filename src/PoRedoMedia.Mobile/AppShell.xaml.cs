using PoRedoMedia.Mobile.Pages;

namespace PoRedoMedia.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("login", typeof(LoginPage));
        Routing.RegisterRoute("media", typeof(MediaPage));
    }
}
