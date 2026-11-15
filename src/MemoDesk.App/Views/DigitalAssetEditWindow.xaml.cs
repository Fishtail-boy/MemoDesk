using System.Windows;
using MemoDesk.App.ViewModels;
using MemoDesk.Core.Domain;
using Microsoft.Data.Sqlite;

namespace MemoDesk.App.Views;

public partial class DigitalAssetEditWindow : Window
{
    private readonly DigitalAsset? _original;
    private readonly Func<DigitalAsset, bool> _save;

    public DigitalAssetEditWindow(DigitalAsset? original, Func<DigitalAsset, bool> save)
    {
        _original = original;
        _save = save;
        InitializeComponent();
        Title = original is null ? "添加数字资产" : "编辑数字资产";

        if (original is not null)
        {
            NameBox.Text = original.Name;
            CategoryBox.Text = original.Category;
            LinkBox.Text = original.Link?.AbsoluteUri ?? string.Empty;
            DescriptionBox.Text = original.Description;
            ExpirationDateField.SelectedDate = original.ExpirationDate?.ToDateTime(TimeOnly.MinValue);
            if (original.ExpirationTime is { } time)
                ExpirationTimeField.Text = DigitalAssetRow.FormatTime(time);
        }

        Loaded += (_, _) => NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "请输入资产名称。";
            NameBox.Focus();
            return;
        }

        ExpirationDateField.CommitText();
        ExpirationTimeField.CommitText();
        if (!ExpirationDateField.IsValid || !ExpirationTimeField.IsValid)
        {
            ErrorText.Text = ExpirationDateField.ValidationError ?? ExpirationTimeField.ValidationError;
            return;
        }

        var date = ExpirationDateField.SelectedDate;
        var time = ExpirationTimeField.SelectedTime;
        if (date is null && time is not null)
        {
            ErrorText.Text = "填写到期时刻时，请同时填写到期日期。";
            return;
        }

        if (!DigitalAsset.TryParseLink(LinkBox.Text, out var link))
        {
            ErrorText.Text = "请输入完整的 http:// 或 https:// 链接。";
            LinkBox.Focus();
            return;
        }

        var category = CategoryBox.Text.Trim();
        var asset = new DigitalAsset(
            _original?.Id ?? Guid.NewGuid(), name, category.Length == 0 ? "其他" : category, link,
            DescriptionBox.Text.Trim(), date.HasValue ? DateOnly.FromDateTime(date.Value) : null, time);
        try
        {
            if (_save(asset))
                DialogResult = true;
            else
                ErrorText.Text = "这项资产已被删除。";
        }
        catch (SqliteException)
        {
            ErrorText.Text = "保存数字资产失败。请稍后重试。";
        }
    }
}
