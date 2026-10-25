using System.Globalization;
using System.Reflection;

namespace MemoDesk.Infrastructure.Storage.Migrations;

/// <summary>
/// 从程序集内嵌资源加载全部迁移脚本。文件命名约束：<c>NNNN_name.sql</c>，NNNN 即版本号。
/// </summary>
public static class MigrationsCatalog
{
    private static readonly Lazy<IReadOnlyList<SqlMigration>> LazyMigrations = new(Load);

    public static IReadOnlyList<SqlMigration> All => LazyMigrations.Value;

    private static IReadOnlyList<SqlMigration> Load()
    {
        var assembly = typeof(MigrationsCatalog).Assembly;
        var prefix = $"{typeof(MigrationsCatalog).Namespace}.Sql.";
        var migrations = new List<SqlMigration>();

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

        foreach (var resourceName in resourceNames)
        {
            var fileName = resourceName[prefix.Length..];
            var version = int.Parse(fileName.AsSpan(0, 4), CultureInfo.InvariantCulture);

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"无法读取迁移资源 {resourceName}。");
            using var reader = new StreamReader(stream);

            migrations.Add(new SqlMigration(version, fileName, reader.ReadToEnd()));
        }

        return migrations;
    }
}
