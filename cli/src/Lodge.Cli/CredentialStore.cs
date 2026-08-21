using System.Text.Json;

namespace Lodge.Cli;

/// <summary>Stores the remote server URL and personal token from `lodge login`, gh-cli style.</summary>
public sealed record StoredCredentials(string ServerUrl, string Token);

public static class CredentialStore
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "lodge", "credentials.json");

    public static void Save(StoredCredentials credentials)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(credentials));

        // Best-effort: keep the credentials file readable only by the current user.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static StoredCredentials? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }
        return JsonSerializer.Deserialize<StoredCredentials>(File.ReadAllText(FilePath));
    }
}
