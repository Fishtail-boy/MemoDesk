namespace MemoDesk.Core.Domain;

/// <summary>
/// 正式事项的三种业务类型。每个正式事项有且只有一个类型（设计 3.1 / 7.3）。
/// 数值一旦发布不得更改，持久化数据依赖它。
/// </summary>
public enum ItemType
{
    Todo = 0,
    WorkLog = 1,
    Memo = 2,
}
