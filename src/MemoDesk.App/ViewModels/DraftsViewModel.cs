using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoDesk.App.Services;
using MemoDesk.Core;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Parsing;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.App.ViewModels;

/// <summary>待确认草稿（设计 2.2）：编辑自动保存、刷新对账、确认原子入库。</summary>
public partial class DraftsViewModel : ObservableObject
{
    private readonly IParseDraftRepository _drafts;
    private readonly ICaptureRepository _captures;
    private readonly DraftConfirmationService _confirmation;
    private readonly ParseCoordinator _coordinator;
    private readonly AppSettings _settings;
    private readonly TimeProvider _clock;
    private readonly DialogService _dialogs;
    private readonly Core.Items.ItemService _items;

    private CancellationTokenSource? _saveDebounce;

    public DraftsViewModel(
        IParseDraftRepository drafts,
        ICaptureRepository captures,
        DraftConfirmationService confirmation,
        ParseCoordinator coordinator,
        AppSettings settings,
        TimeProvider clock,
        DialogService dialogs,
        Core.Items.ItemService items)
    {
        _drafts = drafts;
        _captures = captures;
        _confirmation = confirmation;
        _coordinator = coordinator;
        _settings = settings;
        _clock = clock;
        _dialogs = dialogs;
        _items = items;
    }

    public ObservableCollection<DraftCardViewModel> Cards { get; } = new();

    [ObservableProperty]
    private int pendingCount;

    [ObservableProperty]
    private string message = string.Empty;

    [ObservableProperty]
    private bool isEmpty = true;

    public event EventHandler<int>? DraftCountChanged;

    public void Refresh()
    {
        PersistEdits();

        var timeZone = _settings.TimeZone;
        var existing = Cards.ToDictionary(card => card.Draft.Id);
        var next = new List<DraftCardViewModel>();

        foreach (var status in new[] { DraftStatus.AwaitingConfirmation, DraftStatus.Failed, DraftStatus.Parsing })
        {
            foreach (var draft in _drafts.GetByStatus(status))
            {
                var capture = _captures.GetById(draft.CaptureId);
                if (capture is null)
                {
                    continue;
                }

                if (existing.TryGetValue(draft.Id, out var card) && card.Draft.Version == draft.Version)
                {
                    // 保留当前卡片与候选编辑器实例，避免刷新清空正在输入的内容。
                    card.UpdateFrom(draft);
                }
                else
                {
                    card = new DraftCardViewModel(draft, capture, timeZone);
                    card.Edited += OnCardEdited;
                }

                next.Add(card);
            }
        }

        Cards.Clear();
        foreach (var card in next)
        {
            Cards.Add(card);
        }

        PendingCount = _drafts.CountByStatus(DraftStatus.AwaitingConfirmation)
            + _drafts.CountByStatus(DraftStatus.Failed);
        IsEmpty = Cards.Count == 0;
        DraftCountChanged?.Invoke(this, PendingCount);
    }

    /// <summary>保存所有有编辑的草稿；使用 edit_version 乐观并发，旧卡片不能覆盖新结果。</summary>
    public void PersistEdits()
    {
        var now = _clock.GetUtcNow();
        foreach (var card in Cards.Where(card => card.IsAwaiting && card.HasEdits && !card.HasConflict))
        {
            var json = JsonDefaults.Serialize(card.ToEditState());
            if (_drafts.SaveEditState(card.Draft.Id, card.EditVersion, json, now))
            {
                card.SetEditVersion(card.EditVersion + 1);
                card.HasEdits = false;
            }
            else
            {
                card.HasConflict = true;
                card.ErrorText = card.ConflictMessage;
            }
        }
    }

    [RelayCommand]
    private void Confirm(DraftCardViewModel card)
    {
        if (card.HasConflict)
        {
            card.ErrorText = card.ConflictMessage;
            return;
        }

        PersistEdits();

        if (card.HasConflict)
        {
            return;
        }

        var timeZone = _settings.TimeZone;
        var candidates = card.BuildCandidates(timeZone);
        var result = _confirmation.Confirm(card.Draft, card.Capture, candidates, card.EditVersion);

        switch (result.Outcome)
        {
            case DraftConfirmOutcome.Confirmed:
                Message = "已保存所选事项。";
                _items.NotifyChanged();
                Refresh();
                break;
            case DraftConfirmOutcome.AlreadyConfirmed:
                Message = "该草稿已经确认过。";
                Refresh();
                break;
            case DraftConfirmOutcome.VersionConflict:
                card.ErrorText = "草稿已在别处更新，已为你刷新，请重新确认。";
                Refresh();
                break;
            case DraftConfirmOutcome.Invalid:
                card.ErrorText = string.Join("；", result.Errors);
                break;
            default:
                Message = "草稿不存在，可能已被处理。";
                Refresh();
                break;
        }
    }

    [RelayCommand]
    private void Discard(DraftCardViewModel card)
    {
        if (!_dialogs.Confirm("丢弃这张草稿？原始输入会保留，但不会生成任何事项。"))
        {
            return;
        }

        _drafts.UpdateStatus(card.Draft.Id, DraftStatus.Discarded, null, _clock.GetUtcNow());
        Message = "草稿已丢弃。";
        Refresh();
    }

    [RelayCommand]
    private void Retry(DraftCardViewModel card)
    {
        _coordinator.Retry(card.Capture.Id);
        Message = "已重新提交解析。";
        Refresh();
    }

    [RelayCommand]
    private void Manual(DraftCardViewModel card)
    {
        _coordinator.CreateManualDraft(card.Capture.Id);
        Message = "已创建手动整理草稿。";
        Refresh();
    }

    [RelayCommand]
    private void RemoveCandidate(DraftCandidateViewModel candidate)
    {
        var card = Cards.FirstOrDefault(item => item.Candidates.Contains(candidate));
        if (card is null)
        {
            return;
        }

        card.Candidates.Remove(candidate);
        card.HasEdits = true;
        PersistEdits();
    }

    /// <summary>冲突处理：丢弃本地编辑，按数据库最新版本重新加载。</summary>
    [RelayCommand]
    private void ReloadCard(DraftCardViewModel card)
    {
        Cards.Remove(card);
        Message = "已按数据库最新版本重新加载草稿。";
        Refresh();
    }

    private void OnCardEdited(DraftCardViewModel card)
    {
        card.HasEdits = true;
        _saveDebounce?.Cancel();
        _saveDebounce = new CancellationTokenSource();
        var token = _saveDebounce.Token;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (dispatcher is not null)
            {
                dispatcher.Invoke(PersistEdits);
            }
            else
            {
                PersistEdits();
            }
        });
    }
}
