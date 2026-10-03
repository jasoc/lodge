using System.Text;
using System.Text.RegularExpressions;
using Lodge.Core.Abstractions;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Reads a slice of a local <c>{runId}.log</c> file for <see cref="IRunbookLogReader"/>.
/// The run id is validated against the executor's own id shape before it ever touches a
/// path, so a crafted id can't read outside the log directory.
/// </summary>
internal static partial class RunLogFile
{
    public static async Task<RunbookLogChunk?> ReadAsync(
        string logDirectory, string expectedPrefix, string runId, long offset, int maxBytes, CancellationToken cancellationToken)
    {
        if (!runId.StartsWith(expectedPrefix, StringComparison.Ordinal) || !RunIdPattern().IsMatch(runId))
        {
            return null;
        }

        var path = Path.Combine(logDirectory, $"{runId}.log");
        if (!File.Exists(path))
        {
            // Started but nothing written yet, or a run that predates log capture.
            return File.Exists(Path.Combine(logDirectory, $"{runId}.json")) ? new RunbookLogChunk(string.Empty, 0) : null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        offset = Math.Clamp(offset, 0, stream.Length);
        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[(int)Math.Min(maxBytes, stream.Length - offset)];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);

        var complete = CompleteUtf8Length(buffer, read);
        return new RunbookLogChunk(Encoding.UTF8.GetString(buffer, 0, complete), offset + complete);
    }

    /// <summary>Length of the longest prefix that doesn't end inside a multi-byte UTF-8 sequence.</summary>
    private static int CompleteUtf8Length(byte[] buffer, int length)
    {
        // Walk back over at most 3 continuation bytes to the last lead byte.
        for (var i = length - 1; i >= 0 && i >= length - 4; i--)
        {
            var b = buffer[i];
            if ((b & 0b1100_0000) == 0b1000_0000)
            {
                continue; // continuation byte
            }

            var needed = (b & 0b1000_0000) == 0 ? 1
                : (b & 0b1110_0000) == 0b1100_0000 ? 2
                : (b & 0b1111_0000) == 0b1110_0000 ? 3
                : (b & 0b1111_1000) == 0b1111_0000 ? 4
                : 1; // invalid lead byte: let the decoder replace it
            return length - i >= needed ? length : i;
        }

        return length;
    }

    [GeneratedRegex("^[a-z]+-[0-9a-f]{32}$")]
    private static partial Regex RunIdPattern();
}
