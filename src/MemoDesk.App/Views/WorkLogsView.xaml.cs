using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemoDesk.App.ViewModels;

namespace MemoDesk.App.Views;

public partial class WorkLogsView : UserControl
{
    public WorkLogsView()
    {
        InitializeComponent();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Query_Click(sender, e);
        }
    }

    private void Query_Click(object sender, RoutedEventArgs e)
    {
        FromField.CommitText();
        ToField.CommitText();
        if (!FromField.IsValid || !ToField.IsValid)
        {
            if (DataContext is WorkLogsViewModel viewModel)
            {
                viewModel.Message = FromField.ValidationError
                    ?? ToField.ValidationError
                    ?? "日期输入无效，请修改后再查询。";
            }

            return;
        }

        if (DataContext is WorkLogsViewModel validViewModel)
        {
            validViewModel.QueryCommand.Execute(null);
        }
    }
}
