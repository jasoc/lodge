using Microsoft.Extensions.Configuration;

namespace Lodge.Infrastructure.Persistence;

/// <summary>
/// Builds the Npgsql connection string from the same POSTGRES_* names the official
/// postgres Docker image itself reads — one set of env vars configures both the database
/// container and this connection string, with nothing to rename or map in between.
/// </summary>
public static class LodgeConnectionString
{
    public static string Build(IConfiguration configuration) =>
        $"Host={configuration["POSTGRES_HOST"] ?? "localhost"};" +
        $"Port={configuration["POSTGRES_PORT"] ?? "5432"};" +
        $"Database={configuration["POSTGRES_DB"] ?? "lodge"};" +
        $"Username={configuration["POSTGRES_USER"] ?? "lodge"};" +
        $"Password={configuration["POSTGRES_PASSWORD"] ?? "lodge"}";
}
