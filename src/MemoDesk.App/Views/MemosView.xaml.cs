using System.Windows.Controls;

namespace MemoDesk.App.Views;

public partial class MemosView : UserControl
{
    public MemosView()
    {
        InitializeComponent();
    }

    private void OnMore(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
