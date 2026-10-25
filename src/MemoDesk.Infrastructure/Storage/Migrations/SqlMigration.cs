namespace MemoDesk.Infrastructure.Storage.Migrations;

/// <summary>一个按版本顺序执行的 SQL 迁移。</summary>
public sealed record SqlMigration(int Version, string Name, string Sql);
