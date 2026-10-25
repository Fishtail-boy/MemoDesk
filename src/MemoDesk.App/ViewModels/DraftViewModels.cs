using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MemoDesk.Core;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Time;

namespace MemoDesk.App.ViewModels;

public sealed record PriorityOption(ItemPriority? Value, string Label);

/// <summary>
/// 候选事项编辑器：保留用户原始输入（含非法时间文本）与脏字段，
/// 转换交给 Core 的 DraftCandidateEditor，未修改字段原样往返。
/// </summary>
public partial class DraftCandidateViewModel : ObservableObject
{
    private readonly DraftCandidate _original;
    private readonly TimeZoneInfo _timeZone;
    private readonly DateOnly _captureDate;
    private readonly HashSet<string> _dirtyFields;
    private bool _loading;

    public DraftCandidateViewModel(DraftCandidate candidate, TimeZoneInfo timeZone)
        : this(
            candidate,
            timeZone,
            candidate.BusinessDate ?? candidate.WorkTime?.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
            editState: null)
    {
    }

    public DraftCandidateViewModel(
        DraftCandidate candidate,
        TimeZoneInfo timeZone,
        DateOnly captureDate,
        DraftCandidateEditState? editState)
    {
        _original = candidate ?? throw new ArgumentNullException(nameof(candidate));
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _captureDate = captureDate;

        var state = editState ?? CreateInitialState(candidate, timeZone);
        _dirtyFields = new HashSet<string>(state.DirtyFields, StringComparer.Ordinal);

        _loading = true;
        Type = state.Type;
        Content = state.Content;
        Date = ToDateTime(state.Date);
        RemindDate = ToDateTime(state.RemindDate);
        RemindTime = state.RemindTime;
        WorkStart = state.WorkStart;
        WorkEnd = state.WorkEnd;
        WorkEndDate = ToDateTime(state.WorkEndDate);
        TagsText = state.TagsText;
        Priority = state.Priority;
        IssuesText = state.IssuesText;
        _loading = false;

        PropertyChanged += (_, args) =>
        {
            if (_loading || args.PropertyName is null)
            {
                return;
            }

            _dirtyFields.Add(args.PropertyName);
            Revalidate();
            Edited?.Invoke();
        };
        Revalidate();
    }

    public Guid SourceId => _original.SourceId;

    public event Action? Edited;

    public static IReadOnlyList<TypeOption> TypeChoices { get; } = new[]
    {
        new TypeOption(ItemType.Todo, "待办"),
        new TypeOption(ItemType.WorkLog, "工作记录"),
        new TypeOption(ItemType.Memo, "备忘"),
    };

    public static IReadOnlyList<PriorityOption> PriorityChoices { get; } = new[]
    {
        new PriorityOption(null, "无"),
        new PriorityOption(ItemPriority.Low, "低"),
        new PriorityOption(ItemPriority.Normal, "中"),
        new PriorityOption(ItemPriority.High, "高"),
    };

    [ObservableProperty]
    private ItemType type;

    [ObservableProperty]
    private string content = string.Empty;

    [ObservableProperty]
    private DateTime? date;

    [ObservableProperty]
    private DateTime? remindDate;

    [ObservableProperty]
    private string remindTime = string.Empty;

    [ObservableProperty]
    private string workStart = string.Empty;

    [ObservableProperty]
    private string workEnd = string.Empty;

    [ObservableProperty]
    private DateTime? workEndDate;

    [ObservableProperty]
    private string tagsText = string.Empty;

    [ObservableProperty]
    private ItemPriority? priority;

    [ObservableProperty]
    private string issuesText = string.Empty;

    [ObservableProperty]
    private string validationText = string.Empty;

    public bool IsTodo => Type == ItemType.Todo;

    public bool IsMemo => Type == ItemType.Memo;

    public bool IsWorkLog => Type == ItemType.WorkLog;

    public bool HasReminderFields => Type is ItemType.Todo or ItemType.Memo;

    partial void OnTypeChanged(ItemType value)
    {
        OnPropertyChanged(nameof(IsTodo));
        OnPropertyChanged(nameof(IsMemo));
        OnPropertyChanged(nameof(IsWorkLog));
        OnPropertyChanged(nameof(HasReminderFields));
    }

    public DraftCandidateEditState ToEditState() => new()
    {
        SourceId = SourceId,
        Type = Type,
        Content = Content,
        Date = ToDateOnly(Date),
        RemindDate = ToDateOnly(RemindDate),
        RemindTime = RemindTime,
        WorkStart = WorkStart,
        WorkEnd = WorkEnd,
        WorkEndDate = ToDateOnly(WorkEndDate),
        TagsText = TagsText,
        Priority = Priority,
        IssuesText = IssuesText,
        DirtyFields = _dirtyFields.OrderBy(field => field, StringComparer.Ordinal).ToList(),
    };

    public DraftCandidate ToCandidate(TimeZoneInfo timeZone, DateOnly captureDate) =>
        DraftCandidateEditor.Apply(_original, ToEditState(), timeZone, captureDate);

    private void Revalidate()
    {
        var candidate = ToCandidate(_timeZone, _captureDate);
        ValidationText = string.Join("；", candidate.ValidationErrors);
    }

    private static DraftCandidateEditState CreateInitialState(DraftCandidate candidate, TimeZoneInfo timeZone)
    {
        var state = new DraftCandidateEditState
        {
            SourceId = candidate.SourceId,
            Type = candidate.Type,
            Content = candidate.Content,
            Date = candidate.Type == ItemType.WorkLog ? candidate.WorkTime?.Date : candidate.BusinessDate,
            WorkStart = candidate.WorkTime?.Start?.ToString("HH:mm") ?? string.Empty,
            WorkEnd = candidate.WorkTime?.End?.ToString("HH:mm") ?? string.Empty,
            WorkEndDate = candidate.WorkTime?.EndDate,
            TagsText = string.Join(", ", candidate.Tags),
            Priority = candidate.Priority,
            IssuesText = string.Join("\n", candidate.Issues),
        };

        if (candidate.RemindAtUtc is { } remind)
        {
            var local = TimeZoneInfo.ConvertTime(remind, timeZone);
            state.RemindDate = DateOnly.FromDateTime(local.Date);
            state.RemindTime = local.ToString("HH:mm");
        }

        return state;
    }

    private static DateTime? ToDateTime(DateOnly? value) =>
        value?.ToDateTime(TimeOnly.MinValue);

    private static DateOnly? ToDateOnly(DateTime? value) =>
        value is { } date ? DateOnly.FromDateTime(date) : null;
}

/// <summary>一张待确认草稿卡片；刷新时保留编辑中的实例。</summary>
public partial class DraftCardViewModel : ObservableObject
{
    private readonly TimeZoneInfo _timeZone;

    public DraftCardViewModel(ParseDraft draft, Core.Capturing.Capture capture, TimeZoneInfo timeZone)
    {
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        Draft = draft;
        Capture = capture;
        EditVersion = draft.EditVersion;
        CapturedText = $"提交于 {BusinessTime.FormatLocal(capture.CapturedAtUtc, timeZone, "MM-dd HH:mm")}";
        ApplyDraftState(draft);
        InitializeCandidates(draft);
    }

    private void InitializeCandidates(ParseDraft draft)
    {
        Candidates.Clear();
        var captureDate = BusinessTime.DateOf(Capture.CapturedAtUtc, _timeZone);
        var editState = ParseEditState(draft.EditStateJson);
        var originals = draft.Candidates.ToDictionary(candidate => candidate.SourceId);

        if (editState is not null)
        {
            // 编辑状态是候选集合的唯一来源：用户移除的候选不会因回退原始 AI 结果而复活。
            foreach (var edit in editState.Candidates)
            {
                if (originals.TryGetValue(edit.SourceId, out var original))
                {
                    AddCandidate(original, edit, _timeZone, captureDate);
                }
            }
        }
        else
        {
            foreach (var original in draft.Candidates)
            {
                AddCandidate(original, editState: null, _timeZone, captureDate);
            }
        }
    }

    private void AddCandidate(
        DraftCandidate original,
        DraftCandidateEditState? editState,
        TimeZoneInfo timeZone,
        DateOnly captureDate)
    {
        var vm = new DraftCandidateViewModel(original, timeZone, captureDate, editState);
        vm.Edited += () => Edited?.Invoke(this);
        Candidates.Add(vm);
    }

    public ParseDraft Draft { get; private set; }

    public Core.Capturing.Capture Capture { get; }

    public string RawText => Capture.RawText;

    public string CapturedText { get; }

    public int EditVersion { get; private set; }

    [ObservableProperty]
    private bool hasEdits;

    public event Action<DraftCardViewModel>? Edited;

    public void SetEditVersion(int editVersion)
    {
        EditVersion = editVersion;
        Draft = Draft with { EditVersion = editVersion };
    }

    public ObservableCollection<DraftCandidateViewModel> Candidates { get; } = new();

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string errorText = string.Empty;

    [ObservableProperty]
    private bool isAwaiting;

    [ObservableProperty]
    private bool isFailed;

    [ObservableProperty]
    private bool isParsing;

    [ObservableProperty]
    private bool hasConflict;

    public string ConflictMessage => "草稿已被其他编辑更新。请重新加载后再修改，避免覆盖较新的内容。";

    public void UpdateFrom(ParseDraft draft)
    {
        if (draft.Version != Draft.Version)
        {
            return;
        }

        if (draft.EditVersion != EditVersion)
        {
            // 版本变化时不能只换版本号保留旧内容；有本地编辑则冲突，否则按新状态重建候选。
            if (HasEdits || HasConflict)
            {
                HasConflict = true;
                ErrorText = ConflictMessage;
                ApplyDraftState(draft, preserveError: true);
                return;
            }

            Draft = draft;
            EditVersion = draft.EditVersion;
            InitializeCandidates(draft);
            ApplyDraftState(draft);
            return;
        }

        if (HasConflict)
        {
            // 冲突未解决前不采纳数据库的新版本号/内容，也不清除冲突提示。
            ApplyDraftState(draft, preserveError: true);
            return;
        }

        Draft = draft;
        EditVersion = draft.EditVersion;
        ApplyDraftState(draft);
    }

    private void ApplyDraftState(ParseDraft draft, bool preserveError = false)
    {
        StatusText = draft.Status switch
        {
            DraftStatus.AwaitingConfirmation => "待确认",
            DraftStatus.Failed => "解析失败",
            DraftStatus.Parsing => "解析中",
            _ => draft.Status.ToString(),
        };
        IsAwaiting = draft.Status == DraftStatus.AwaitingConfirmation;
        IsFailed = draft.Status == DraftStatus.Failed;
        IsParsing = draft.Status == DraftStatus.Parsing;
        if (!preserveError && !HasConflict)
        {
            ErrorText = draft.LastError ?? string.Empty;
        }
    }

    public IReadOnlyList<DraftCandidate> BuildCandidates(TimeZoneInfo timeZone)
    {
        var captureDate = BusinessTime.DateOf(Capture.CapturedAtUtc, timeZone);
        return Candidates.Select(candidate => candidate.ToCandidate(timeZone, captureDate)).ToList();
    }

    public DraftEditState ToEditState() => new()
    {
        Candidates = Candidates.Select(candidate => candidate.ToEditState()).ToList(),
    };

    public static DraftEditState? ParseEditState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDefaults.Deserialize<DraftEditState>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
