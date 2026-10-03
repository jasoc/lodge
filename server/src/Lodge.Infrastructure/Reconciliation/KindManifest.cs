using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// The optional <c>inventory/{kind}/kind.yaml</c> manifest. Only <c>name</c> (the display
/// name) is read today; a kind folder without one is still a kind, named by its code.
/// </summary>
internal static partial class KindManifest
{
    public const string FileName = "kind.yaml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Lowercase letters, digits, '-' and '_', starting with a letter or digit; fits the 64-char key.</summary>
    public static bool IsValidCode(string code) => code.Length <= 64 && CodePattern().IsMatch(code);

    /// <summary>The manifest's <c>name</c>, or null when absent/blank/unparseable — a bad manifest never hides the kind.</summary>
    public static string? ParseName(string yaml)
    {
        try
        {
            var name = Deserializer.Deserialize<Manifest?>(yaml)?.Name?.Trim();
            return string.IsNullOrEmpty(name) ? null : name[..Math.Min(name.Length, 256)];
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex CodePattern();

    private sealed class Manifest
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }
    }
}
