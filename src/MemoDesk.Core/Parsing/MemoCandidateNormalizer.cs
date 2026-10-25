namespace MemoDesk.Core.Parsing;

/// <summary>把 AI 候选规范成“可应用”的备忘列表：稳定 ID、去空、合并非独立拆分。</summary>
public static class MemoCandidateNormalizer
{
    public static IReadOnlyList<MemoParseCandidate> Normalize(
        IReadOnlyList<MemoParseCandidate> candidates,
        Guid captureId,
        int operationVersion)
    {
        var clean = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Content))
            .Take(10)
            .ToList();

        if (clean.Count > 1)
        {
            var distinctReminders = clean
                .Where(candidate => candidate.RemindAtUtc is not null)
                .Select(candidate => candidate.RemindAtUtc!.Value)
                .Distinct()
                .Count();

            if (distinctReminders <= 1)
            {
                clean = new List<MemoParseCandidate> { Merge(clean) };
            }
        }

        for (var index = 0; index < clean.Count; index++)
        {
            clean[index].SourceId = StableId.Candidate(captureId, operationVersion, index);
        }

        return clean;
    }

    private static MemoParseCandidate Merge(IReadOnlyList<MemoParseCandidate> candidates)
    {
        var reminderSource = candidates.FirstOrDefault(candidate => candidate.RemindAtUtc is not null)
            ?? candidates[0];
        return new MemoParseCandidate
        {
            Content = string.Join("\n", candidates.Select(candidate => candidate.Content.Trim())),
            SourceText = reminderSource.SourceText,
            RemindText = reminderSource.RemindText,
            ReminderIntent = reminderSource.ReminderIntent,
            ReminderPrecision = reminderSource.ReminderPrecision,
            RemindAtUtc = reminderSource.RemindAtUtc,
            RemindNow = reminderSource.RemindNow,
            WorkTime = candidates.Select(candidate => candidate.WorkTime).FirstOrDefault(value => value is not null),
            Priority = candidates.Select(candidate => candidate.Priority).FirstOrDefault(value => value is not null),
            Tags = candidates.SelectMany(candidate => candidate.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList(),
            Issues = candidates.SelectMany(candidate => candidate.Issues)
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToList(),
            ValidationErrors = candidates.SelectMany(candidate => candidate.ValidationErrors)
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToList(),
        };
    }
}
