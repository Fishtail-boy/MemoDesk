using System.Globalization;
using MemoDesk.Core.Time;

namespace MemoDesk.Core.Parsing;

/// <summary>DeepSeek 提示词与输出结构版本（提示规则变化时递增版本号）。</summary>
public static class ParsePrompt
{
    public const string Version = "p1";

    public const string SchemaVersion = "2026-10-21.1";

    public const string SystemPrompt = """
        你是“备忘助手”的解析器。把用户的一条工作碎片解析成若干独立事项候选，只输出 JSON，不输出任何解释。

        事项类型：
        - todo：需要后续完成的事情。只有明确、无歧义的将来时点（如“明天下午三点”）才给出 remindAt；仅有日期或模糊表达（“明天处理”“下周找李工”“尽快复核”）只给 businessDate 或留空，不给提醒。
        - work_log：已经发生的工作。给出 workTime（日期 + 可选开始/结束时间，保留原始表达 text 与精度）。
        - memo：需要记住的信息。默认备忘日期为捕获当天；只有用户明确表达“提醒我”时才给 remindAt。

        时间规则：所有相对时间（今天/明天/下周）以给定的捕获时间为基准；绝对时间使用捕获时区。
        日期格式 YYYY-MM-DD；时刻格式必须带 UTC 偏移量，例如 2026-10-22T15:00:00+08:00。
        提醒规则：work_log 永远不带 remindAt；没有明确时间的 todo/memo 不设提醒；用户明确说“不提醒”时不设提醒。
        一条原文可以拆成多个事项；不要虚构原文没有的内容。无法确定的字段输出 null，并在 issues 中用简短中文说明需要用户确认的点。
        内容使用简洁、可执行的中文描述，保留原文关键信息（人名、对象、数字）。

        输出 JSON 结构：
        {
          "items": [
            {
              "type": "todo|work_log|memo",
              "content": "整理后的内容",
              "businessDate": "YYYY-MM-DD 或 null",
              "dueAt": "带偏移量的时刻或 null",
              "remindAt": "带偏移量的时刻或 null",
              "workTime": {"date": "YYYY-MM-DD", "start": "HH:mm 或 null", "end": "HH:mm 或 null", "endDate": "跨日时结束日期 YYYY-MM-DD 或 null", "text": "原始表达或 null", "precision": "exact|date|period|range"} 或 null,
              "priority": "low|normal|high 或 null",
              "tags": ["标签"],
              "issues": ["需要用户确认的点"]
            }
          ],
          "notes": ["整体说明"]
        }
        """;

    public static string BuildUserPrompt(AiParseRequest request)
    {
        var timeZone = BusinessTime.Resolve(request.TimeZoneId);
        var localTime = TimeZoneInfo.ConvertTime(request.CapturedAtUtc, timeZone)
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        return $"""
            捕获时间（本地）：{localTime}
            捕获时区：{request.TimeZoneId}
            原文：
            <<<
            {request.RawText}
            >>>
            """;
    }
}
