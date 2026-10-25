using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoDesk.App.Services;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.App.ViewModels;

public sealed record TypeOption(ItemType? Type, string Label);

public sealed record StatusOption(SearchStatusFilter Value, string Label);

/// <summary>搜索（设计 8）。</summary>
public partial class SearchViewModel : ObservableObject
{
    private readonly SearchService _search;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;

    public SearchViewModel(SearchService search, AppSettings settings, DialogService dialogs)
    {
        _search = search;
        _settings = settings;
        _dialogs = dialogs;
        SelectedType = TypeOptions[0];
        SelectedStatus = StatusOptions[0];
    }

    public IReadOnlyList<TypeOption> TypeOptions { get; } = new[]
    {
        new TypeOption(null, "全部类型"),
        new TypeOption(ItemType.Todo, "待办"),
        new TypeOption(ItemType.WorkLog, "工作记录"),
        new TypeOption(ItemType.Memo, "备忘"),
    };

    public IReadOnlyList<StatusOption> StatusOptions { get; } = new[]
    {
        new StatusOption(SearchStatusFilter.Any, "全部状态"),
        new StatusOption(SearchStatusFilter.Open, "未完成/未处理"),
        new StatusOption(SearchStatusFilter.Completed, "已完成"),
        new StatusOption(SearchStatusFilter.Archived, "已归档"),
    };

    public ObservableCollection<SearchRow> Results { get; } = new();

    [ObservableProperty]
    private string queryText = string.Empty;

    [ObservableProperty]
    private TypeOption selectedType;

    [ObservableProperty]
    private StatusOption selectedStatus;

    [ObservableProperty]
    private DateTime? fromDate;

    [ObservableProperty]
    private DateTime? toDate;

    [ObservableProperty]
    private string hint = "输入关键词后按 Enter 搜索。";

    public void Refresh()
    {
        if (string.IsNullOrWhiteSpace(QueryText))
        {
            Results.Clear();
            Hint = "输入关键词后按 Enter 搜索。";
            return;
        }

        var timeZone = _settings.TimeZone;
        var hits = _search.Search(new SearchQuery(
            QueryText,
            SelectedType.Type,
            SelectedStatus.Value,
            FromDate is { } from ? DateOnly.FromDateTime(from) : null,
            ToDate is { } to ? DateOnly.FromDateTime(to) : null));

        Results.Clear();
        foreach (var hit in hits)
        {
            Results.Add(new SearchRow(
                hit,
                RowFormat.TypeText(hit.Item.Type),
                BuildTimeText(hit.Item, timeZone),
                RowFormat.StatusText(hit.Item),
                hit.Snippet));
        }

        Hint = hits.Count == 0 ? "没有匹配的结果。" : $"共 {hits.Count} 条结果。";
    }

    [RelayCommand]
    private void Search() => Refresh();

    [RelayCommand]
    private void Edit(SearchRow row)
    {
        if (_dialogs.EditItem(row.Item, _settings.TimeZone))
        {
            Refresh();
        }
    }

    private static string BuildTimeText(Item item, TimeZoneInfo timeZone) => item.Type switch
    {
        ItemType.Todo => item.ItemDate is { } date
            ? date.ToString("MM-dd")
            : item.DueAtUtc is { } due
                ? RowFormat.Local(due, timeZone)
                : string.Empty,
        ItemType.Memo => item.MemoDate is { } memoDate ? memoDate.ToString("MM-dd") : string.Empty,
        _ => RowFormat.BuildWorkTimeText(item, timeZone),
    };
}
