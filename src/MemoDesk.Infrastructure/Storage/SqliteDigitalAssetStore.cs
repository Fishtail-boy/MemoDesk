using MemoDesk.Core.Domain;

namespace MemoDesk.Infrastructure.Storage;

public sealed class SqliteDigitalAssetStore(SqliteConnectionFactory connections)
{
    public IReadOnlyList<DigitalAsset> List()
    {
        using var connection = connections.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, category, link, description, expiration_date, expiration_time
            FROM digital_assets
            ORDER BY expiration_date IS NULL, expiration_date, expiration_time IS NULL, expiration_time, name, id;
            """;

        using var reader = command.ExecuteReader();
        var assets = new List<DigitalAsset>();
        while (reader.Read())
        {
            DigitalAsset.TryParseLink(reader.IsDBNull(3) ? string.Empty : reader.GetString(3), out var link);
            assets.Add(new DigitalAsset(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                link,
                reader.GetString(4),
                reader.IsDBNull(5) ? null : SqliteMapping.ParseDate(reader.GetString(5)),
                reader.IsDBNull(6) ? null : SqliteMapping.ParseTimeOrNull(reader.GetString(6))));
        }
        return assets;
    }

    public void Add(DigitalAsset asset) => Write(asset, """
        INSERT INTO digital_assets (id, name, category, link, description, expiration_date, expiration_time)
        VALUES ($id, $name, $category, $link, $description, $date, $time);
        """);

    public bool Update(DigitalAsset asset) => Write(asset, """
        UPDATE digital_assets SET name = $name, category = $category, link = $link,
            description = $description, expiration_date = $date, expiration_time = $time
        WHERE id = $id;
        """);

    public bool Delete(Guid id)
    {
        using var connection = connections.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM digital_assets WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        return command.ExecuteNonQuery() == 1;
    }

    private bool Write(DigitalAsset asset, string sql)
    {
        using var connection = connections.Create();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", asset.Id.ToString("N"));
        command.Parameters.AddWithValue("$name", asset.Name);
        command.Parameters.AddWithValue("$category", asset.Category);
        command.Parameters.AddWithValue("$link", (object?)asset.Link?.AbsoluteUri ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", asset.Description);
        command.Parameters.AddWithValue("$date", SqliteMapping.ToDb(asset.ExpirationDate));
        command.Parameters.AddWithValue("$time", SqliteMapping.ToDb(asset.ExpirationTime));
        return command.ExecuteNonQuery() == 1;
    }
}
