using System.Text;
using System.Text.Json;

namespace Lodge.Validation;

/// <summary>The output formats of <c>lodge validate</c>.</summary>
public enum ReportFormat
{
    /// <summary>One human-readable line per diagnostic, then a summary.</summary>
    Text,

    /// <summary>One JSON document: <c>{"valid": bool, "diagnostics": [...]}</c>, snake_case.</summary>
    Json,

    /// <summary>GitHub Actions workflow commands (<c>::error file=...::message</c>), shown as annotations.</summary>
    Github
}

public static class DiagnosticFormatter
{
    public static bool TryParseFormat(string? name, out ReportFormat format)
    {
        format = ReportFormat.Text;
        return name?.ToLowerInvariant() switch
        {
            "text" => true,
            "json" => (format = ReportFormat.Json) == ReportFormat.Json,
            "github" => (format = ReportFormat.Github) == ReportFormat.Github,
            _ => false
        };
    }

    public static string Format(IReadOnlyList<Diagnostic> diagnostics, ReportFormat format) => format switch
    {
        ReportFormat.Json => FormatJson(diagnostics),
        ReportFormat.Github => FormatGithub(diagnostics),
        _ => FormatText(diagnostics)
    };

    private static string FormatText(IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return "OK: inventory is valid." + Environment.NewLine;
        }

        var sb = new StringBuilder();
        foreach (var d in diagnostics)
        {
            var scope = d.Kind is null ? "" : d.Instance is null ? $" [{d.Kind}]" : $" [{d.Kind}/{d.Instance}]";
            sb.Append("error: ").Append(d.File).Append(scope).Append(" (").Append(d.Check).Append("): ")
              .Append(d.Message).AppendLine();
        }
        sb.Append(diagnostics.Count).Append(diagnostics.Count == 1 ? " error." : " errors.").AppendLine();
        return sb.ToString();
    }

    private static string FormatJson(IReadOnlyList<Diagnostic> diagnostics)
        => JsonSerializer.Serialize(
            new
            {
                valid = diagnostics.Count == 0,
                diagnostics = diagnostics.Select(d => new
                {
                    severity = d.Severity.ToString().ToLowerInvariant(),
                    check = d.Check,
                    file = d.File,
                    kind = d.Kind,
                    instance = d.Instance,
                    message = d.Message
                })
            },
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    private static string FormatGithub(IReadOnlyList<Diagnostic> diagnostics)
    {
        var sb = new StringBuilder();
        foreach (var d in diagnostics)
        {
            sb.Append("::error file=").Append(EscapeProperty(d.File))
              .Append(",title=").Append(EscapeProperty($"lodge validate ({d.Check})"))
              .Append("::").Append(EscapeData(d.Message)).AppendLine();
        }
        return sb.ToString();
    }

    // https://docs.github.com/actions/reference/workflow-commands-for-github-actions
    private static string EscapeData(string s) => s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    private static string EscapeProperty(string s) => EscapeData(s).Replace(":", "%3A").Replace(",", "%2C");
}
