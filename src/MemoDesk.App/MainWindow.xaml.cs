using System.Windows;
using MemoDesk.App.ViewModels;

namespace MemoDesk.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        MemoDesk.Platform.Windows.Windowing.WindowEffects.Attach(this);
        DataContext = viewModel;
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && DataContext is MainViewModel viewModel)
        {
            viewModel.NavigateTo(key);
        }
    }
}
