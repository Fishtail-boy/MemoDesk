using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoDesk.App.Services;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.App.ViewModels;

/// <summary>待办管理（设计 2.4）。</summary>
public partial class TodosViewModel : ObservableObject
{
    private readonly ItemQueryService _query;
    private readonly ItemService _items;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;

    public TodosViewModel(ItemQueryService query, ItemService items, AppSettings settings, DialogService dialogs)
    {
        _query = query;
        _items = items;
        _settings = settings;
        _dialogs = dialogs;
        Filter = StatusFilter.Open;
    }

    public ObservableCollection<TodoRow> Rows { get; } = new();

    [ObservableProperty]
    private StatusFilter filter;

    [ObservableProperty]
    private string emptyHint = "暂无待办。";

    public void Refresh()
    {
        var timeZone = _settings.TimeZone;
        Rows.Clear();
        foreach (var item in _query.GetTodos(Filter))
        {
            Rows.Add(new TodoRow(
                item,
                item.Content,
                RowFormat.BuildTodoMeta(item, timeZone),
                RowFormat.StatusText(item),
                item.Status == ItemStatus.Open));
        }

        EmptyHint = Filter switch
        {
            StatusFilter.Open => "没有未完成的待办。",
            StatusFilter.Completed => "还没有已完成的待办。",
            _ => "暂无待办。",
        };
    }

    [RelayCommand]
    private void SetFilter(string filter)
    {
        Filter = filter switch
        {
            "completed" => StatusFilter.Completed,
            "all" => StatusFilter.All,
            _ => StatusFilter.Open,
        };
        Refresh();
    }

    [RelayCommand]
    private void Complete(TodoRow row)
    {
        if (row.IsOpen)
        {
            _items.Complete(row.Item.Id);
            Refresh();
        }
    }

    [RelayCommand]
    private void Reopen(TodoRow row)
    {
        if (!row.IsOpen)
        {
            _items.Reopen(row.Item.Id);
            Refresh();
        }
    }

    [RelayCommand]
    private void Edit(TodoRow row)
    {
        if (_dialogs.EditItem(row.Item, _settings.TimeZone))
        {
            Refresh();
        }
    }

    [RelayCommand]
    private void Delete(TodoRow row)
    {
        if (_dialogs.Confirm($"删除“{row.Content}”？删除后不再出现在列表和搜索中。"))
        {
            _items.Delete(row.Item.Id);
            Refresh();
        }
    }
}
