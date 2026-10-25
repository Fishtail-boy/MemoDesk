using MemoDesk.Core.Domain;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Queries;

public sealed record SearchQuery(
    string Text,
    ItemType? Type = null,
    SearchStatusFilter Status = SearchStatusFilter.Any,
    DateOnly? From = null,
    DateOnly? To = null);

public sealed record SearchHit(Item Item, string Snippet, string MatchedField);

/// <summary>
/// 关键词搜索（设计 8）：SQLite 参数化子串匹配，处理通配符，英文大小写统一；
/// 中文按连续文本匹配。同一事项即使多个字段命中只返回一次。
/// </summary>
public sealed class SearchService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SearchService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public IReadOnlyList<SearchHit> Search(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var text = query.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<SearchHit>();
        }

        var pattern = "%" + EscapeLike(text.ToLowerInvariant()) + "%";
        var conditions = new List<string>
        {
            "items.status <> 'Deleted'",
            """
            (
                lower(items.content) LIKE $pattern ESCAPE '\'
                OR lower(IFNULL(c.raw_text, '')) LIKE $pattern ESCAPE '\'
                OR EXISTS (
                    SELECT 1
                    FROM json_each(
                        CASE WHEN json_valid(items.tags_json) THEN items.tags_json ELSE '[]' END
                    ) AS tag
                    WHERE lower(tag.value) LIKE $pattern ESCAPE '\'
                )
            )
            """,
        };
        var parameters = new List<(string Name, object Value)> { ("$pattern", pattern) };

        if (query.Type is { } type)
        {
            conditions.Add("items.type = $type");
            parameters.Add(("$type", type.ToString()));
        }

        if (query.Status != SearchStatusFilter.Any)
        {
            conditions.Add("items.status = $status");
            parameters.Add(("$status", query.Status.ToString()));
        }

        if (query.From is not null || query.To is not null)
        {
            var effectiveDate = "CASE items.type WHEN 'Todo' THEN items.item_date WHEN 'Memo' THEN items.memo_date ELSE items.work_date END";
            if (query.From is { } from)
            {
                conditions.Add($"{effectiveDate} >= $from");
                parameters.Add(("$from", SqliteMapping.ToDb(from)));
            }

            if (query.To is { } to)
            {
                conditions.Add($"{effectiveDate} <= $to");
                parameters.Add(("$to", SqliteMapping.ToDb(to)));
            }
        }

        var sql = $"""
            SELECT items.id, items.capture_id, items.candidate_source_id, items.type, items.content,
                   items.tags_json, items.priority, items.status, items.item_date, items.due_at_utc,
                   items.memo_date, items.work_date, items.work_start_time, items.work_end_time,
                   items.work_end_date, items.work_precision, items.work_time_text,
                   items.created_at_utc, items.updated_at_utc, items.completed_at_utc,
                   items.archived_at_utc, items.deleted_at_utc, items.row_version,
                   IFNULL(c.raw_text, '')
            FROM items
            LEFT JOIN captures c ON c.id = items.capture_id
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY items.updated_at_utc DESC
            LIMIT 200;
            """;

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var hits = new List<SearchHit>();
        var seen = new HashSet<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = SqliteItemStore.Map(reader);
            if (!seen.Add(item.Id))
            {
                continue;
            }

            var rawText = reader.GetString(23);
            var (source, field) = LocateMatch(item, rawText, text);
            hits.Add(new SearchHit(item, MakeSnippet(source, text, 30), field));
        }

        return hits;
    }

    private static (string Source, string Field) LocateMatch(Item item, string rawText, string text)
    {
        if (item.Content.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return (item.Content, "内容");
        }

        if (rawText.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return (rawText, "原文");
        }

        var tag = item.Tags.FirstOrDefault(t => t.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (tag is not null)
        {
            return (tag, "标签");
        }

        return (item.Content, "内容");
    }

    private static string MakeSnippet(string source, string query, int radius)
    {
        var index = source.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return source.Length <= radius * 2 ? source : source[..(radius * 2)] + "…";
        }

        var start = Math.Max(0, index - radius);
        var end = Math.Min(source.Length, index + query.Length + radius);
        var snippet = source[start..end].Replace('\n', ' ').Replace('\r', ' ');
        return (start > 0 ? "…" : string.Empty) + snippet + (end < source.Length ? "…" : string.Empty);
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
