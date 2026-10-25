namespace MemoDesk.Core.Parsing;

public enum MemoApplyOutcome
{
    Applied = 0,
    AlreadyApplied = 1,
    NotFound = 2,
    VersionConflict = 3,
    HumanRevised = 4,
    ItemNotOpen = 5,
    MissingMemo = 6,
    EmptyResult = 7,
}

/// <summary>把已持久化的整理结果应用到正式备忘；实现必须在一个事务内完成。</summary>
public interface IMemoApplyStore
{
    MemoApplyOutcome Apply(Guid parseResultId, DateTimeOffset now);
}
