using System.Globalization;
using MemoDesk.Core.Time;

namespace MemoDesk.Core.Parsing;

/// <summary>备忘优先的整理提示词；不再让 AI 判断待办/工作记录/备忘分类。</summary>
public static class MemoParsePrompt
{
    public const string Version = "p4";
    public const string SchemaVersion = "2026-10-25.2";

    public const string SystemPrompt = """
        你是“备忘助手”的后台整理器。用户只输入一句话，系统已经把原文保存为正式备忘；你只负责补充内容、标签、可选的实际发生时间和明确提醒，不负责分类到不同业务页面。

        默认一条原文只产生一条备忘。只有原文确实包含多个独立事项、且各自有明确不同的提醒时点时，才拆成多条；不能因为一句话同时描述过去工作和未来安排就复制成两条。拆分后的每条备忘都保留原文的关键内容。

        时间规则：
        - 所有相对时间以给定捕获时间为基准；绝对时间使用捕获时区。
        - 支持秒、s、sec、分钟、min、小时、h 以及“1分30秒”等组合相对时长，保留秒精度。3s后表示捕获时刻加3秒，不能四舍五入到分钟，也不能从当前返回时刻重新计算。
        - remindAt 只用于原文明确表达的未来提醒时点（例如“明天下午三点提交报表”“十分钟后提醒我联系张工”），必须带 UTC 偏移量。
        - 只有日期、模糊时段（上午/下午/本周/尽快）或用户明确说“不提醒”时，remindAt 必须为 null，不能自行猜测 09:00 或 15:00。
        - 原文在录入时已经过去的时点不能作为提醒，remindAt 为 null；可把资料写进 content。
        - workTime 只用于原文明确描述的已发生工作；保留日期、可选开始/结束、跨日结束日期和原始表达 text。没有明确发生时间就为 null，不能编造分钟。
        - 只给原文确实包含的信息。不确定的字段输出 null，并在 issues 中用简短中文说明。

        内容要简洁、可执行，保留人名、对象、数字等关键信息。每条候选必须给出能逐字定位的 sourceText；如果设置提醒，remindText 必须是该 sourceText 的子串。只输出 JSON，不输出解释。

        输出 JSON 结构：
        {
          "items": [
            {
              "content": "整理后的备忘内容",
              "sourceText": "该候选对应的原文片段，必须逐字来自用户原文",
              "remindText": "片段中的提醒时间表达，必须是 sourceText 的子串",
              "remindAt": "带 UTC 偏移量的时刻或 null",
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
