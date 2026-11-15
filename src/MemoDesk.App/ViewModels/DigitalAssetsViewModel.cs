using CommunityToolkit.Mvvm.ComponentModel;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MemoDesk.App.ViewModels;

public sealed partial class DigitalAssetsViewModel(
    SqliteDigitalAssetStore store, AppSettings settings, TimeProvider clock) : ObservableObject
{
    private static readonly AssetStatusFilter AllStatuses = new("全部状态", null);
    private IReadOnlyList<DigitalAsset> _assets = [];

    public IReadOnlyList<AssetStatusFilter> StatusFilters { get; } =
    [
        AllStatuses,
        new($"即将到期（{DigitalAsset.DueSoonDays} 天内）", AssetExpirationStatus.DueSoon),
        new("已过期", AssetExpirationStatus.Expired),
        new("有效", AssetExpirationStatus.Active),
        new("无到期日期", AssetExpirationStatus.NoDate),
    ];

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private AssetStatusFilter? selectedFilter = AllStatuses;

    [ObservableProperty]
    private IReadOnlyList<DigitalAssetRow> rows = [];

    [ObservableProperty]
    private string emptyHint = "还没有数字资产。";

    [ObservableProperty]
    private string? errorText;

    partial void OnSearchTextChanged(string value) => UpdateRows();

    partial void OnSelectedFilterChanged(AssetStatusFilter? value) => UpdateRows();

    public void Refresh()
    {
        try
        {
            _assets = store.List();
            ErrorText = null;
            UpdateRows();
        }
        catch (SqliteException)
        {
            ErrorText = "读取数字资产失败。请稍后重试。";
        }
    }

    public bool Save(DigitalAsset asset, bool isNew)
    {
        if (!isNew)
            return store.Update(asset);

        store.Add(asset);
        return true;
    }

    public void Delete(DigitalAsset asset)
    {
        try
        {
            var deleted = store.Delete(asset.Id);
            Refresh();
            if (!deleted && ErrorText is null)
                ErrorText = "这项资产已被删除。";
        }
        catch (SqliteException)
        {
            ErrorText = "删除数字资产失败。请稍后重试。";
        }
    }

    public void UpdateRows()
    {
        var query = SearchText.Trim();
        var now = clock.GetUtcNow();
        var zone = settings.TimeZone;
        var today = BusinessTime.Today(now, zone);
        var filter = SelectedFilter?.Status;
        var visible = new List<DigitalAssetRow>();
        foreach (var asset in _assets)
        {
            if (!Matches(asset, query))
                continue;

            var status = asset.GetExpirationStatus(now, zone);
            if (filter is null || status == filter)
                visible.Add(new DigitalAssetRow(asset, status, today));
        }

        if (!Rows.SequenceEqual(visible))
            Rows = visible;
        EmptyHint = _assets.Count == 0 ? "还没有数字资产。" : "没有符合条件的资产。";
    }

    private static bool Matches(DigitalAsset asset, string query) =>
        query.Length == 0 ||
        asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        asset.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        asset.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        asset.Link?.AbsoluteUri.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record AssetStatusFilter(string Title, AssetExpirationStatus? Status);

public sealed record DigitalAssetRow(DigitalAsset Asset, AssetExpirationStatus Status, DateOnly Today)
{
    public string? LinkText { get; } = Asset.Link?.AbsoluteUri;

    public string ExpirationText => Asset.ExpirationDate is { } date
        ? $"{BusinessTime.FormatDate(date)}{(Asset.ExpirationTime is { } time ? $" {FormatTime(time)}" : "")}"
        : "无到期日期";

    public string StatusText => Status switch
    {
        AssetExpirationStatus.Expired => "已过期",
        AssetExpirationStatus.DueSoon when Asset.ExpirationDate == Today => "今天到期",
        AssetExpirationStatus.DueSoon when Asset.ExpirationDate is { } date => $"{date.DayNumber - Today.DayNumber} 天后到期",
        AssetExpirationStatus.Active => "有效",
        _ => string.Empty,
    };

    public static string FormatTime(TimeOnly time) =>
        time.ToString(time.Second == 0 ? "HH:mm" : "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
