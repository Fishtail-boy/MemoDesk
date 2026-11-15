using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MemoDesk.App.Services;
using MemoDesk.App.ViewModels;
using MemoDesk.Core.Domain;

namespace MemoDesk.App.Views;

public partial class DigitalAssetsView : UserControl
{
    private readonly DispatcherTimer _expirationTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public DigitalAssetsView()
    {
        InitializeComponent();
        _expirationTimer.Tick += (_, _) => (DataContext as DigitalAssetsViewModel)?.UpdateRows();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                (DataContext as DigitalAssetsViewModel)?.UpdateRows();
                _expirationTimer.Start();
            }
            else
            {
                _expirationTimer.Stop();
            }
        };
    }

    private void Add_Click(object sender, RoutedEventArgs e) => ShowEditor(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DigitalAsset asset })
            ShowEditor(asset);
    }

    private void ShowEditor(DigitalAsset? asset)
    {
        if (DataContext is not DigitalAssetsViewModel model)
            return;

        var editor = new DigitalAssetEditWindow(asset, edited => model.Save(edited, asset is null))
        {
            Owner = Window.GetWindow(this),
        };
        if (editor.ShowDialog() == true)
            model.Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DigitalAsset asset } || DataContext is not DigitalAssetsViewModel model)
            return;

        var result = MessageBox.Show(Window.GetWindow(this), $"删除「{asset.Name}」？", "删除数字资产",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes)
            model.Delete(asset);
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Uri link } || DataContext is not DigitalAssetsViewModel model)
            return;

        if (!DigitalAssetLinks.Open(link))
            model.ErrorText = "无法在默认浏览器中打开链接。";
    }
}
