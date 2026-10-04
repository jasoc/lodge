using YamlDotNet.Serialization;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// The optional <c>inventory/{kind}/kind.yaml</c> manifest. Only <c>name</c> (the display
/// name) is read today; a kind folder without one is still a kind, named by its code.
/// </summary>
internal static class KindManifest
{
    public const string FileName = Lodge.Core.Catalog.InventoryLayout.KindManifestFileName;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public static bool IsValidCode(string code) => Lodge.Core.Catalog.InventoryLayout.IsValidKindCode(code);

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

    private sealed class Manifest
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }
    }
}
