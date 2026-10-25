using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoDesk.App.Services;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.App.ViewModels;

/// <summary>工作记录（设计 2.4 / 8）：默认今天，显示发生时间、整理后内容、原文与录入时间。</summary>
public partial class WorkLogsViewModel : ObservableObject
{
    private readonly ItemQueryService _query;
    private readonly ItemService _items;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly ICaptureRepository _captures;
    private readonly TimeProvider _clock;

    public WorkLogsViewModel(
        ItemQueryService query,
        ItemService items,
        AppSettings settings,
        DialogService dialogs,
        ICaptureRepository captures,
        TimeProvider clock)
    {
        _query = query;
        _items = items;
        _settings = settings;
        _dialogs = dialogs;
        _captures = captures;
        _clock = clock;

        var today = BusinessTime.Today(clock.GetUtcNow(), settings.TimeZone);
        FromDate = today.ToDateTime(TimeOnly.MinValue);
        ToDate = FromDate;
    }

    public ObservableCollection<WorkLogRow> Rows { get; } = new();

    [ObservableProperty]
    private DateTime? fromDate;

    [ObservableProperty]
    private DateTime? toDate;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string emptyHint = "所选日期没有工作记录。";

    [ObservableProperty]
    private string message = string.Empty;

    public void Refresh()
    {
        var timeZone = _settings.TimeZone;
        var from = DateOnly.FromDateTime(FromDate ?? DateTime.Today);
        var to = DateOnly.FromDateTime(ToDate ?? DateTime.Today);
        if (to < from)
        {
            Rows.Clear();
            Message = "结束日期不能早于开始日期。";
            EmptyHint = "日期范围无效。";
            return;
        }

        Message = string.Empty;
        Rows.Clear();
        foreach (var entry in _query.GetWorkRecords(from, to, timeZone, SearchText))
        {
            Rows.Add(new WorkLogRow(
                entry.Item,
                entry.DisplayTime,
                entry.Content,
                entry.RawText,
                entry.Note,
                entry.Note));
        }

        EmptyHint = "所选日期没有工作记录。";
    }

    [RelayCommand]
    private void Query() => Refresh();

    [RelayCommand]
    private void ThisWeek()
    {
        var today = BusinessTime.Today(_clock.GetUtcNow(), _settings.TimeZone).ToDateTime(TimeOnly.MinValue);
        FromDate = today.AddDays(-6);
        ToDate = today;
        Refresh();
    }

    [RelayCommand]
    private void Edit(WorkLogRow row)
    {
        if (_dialogs.EditItem(row.Item, _settings.TimeZone))
        {
            Refresh();
        }
    }

    [RelayCommand]
    private void Delete(WorkLogRow row)
    {
        if (_dialogs.Confirm($"删除这条工作记录？删除后不再出现在列表和搜索中。"))
        {
            _items.Delete(row.Item.Id);
            Refresh();
        }
    }
}
