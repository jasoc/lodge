using System.Text;
using System.Text.Json;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Replaces resolved secret values in text that is about to be logged. Besides each value
/// itself it masks its JSON-escaped forms (a secret travels inside <c>LODGE_PARAMS_JSON</c>)
/// and, for a multi-line secret (a PEM key), every line of it. What it cannot catch is a
/// secret the action transforms (base64, URL encoding, a hash): only the exact values Lodge
/// resolved are known to it.
/// </summary>
public sealed class SecretMasker
{
    public const string Mask = "***";

    /// <summary>A multi-line secret's individual lines shorter than this aren't masked on their own (they'd mask ordinary text).</summary>
    private const int MinLineLength = 8;

    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly SecretMasker None = new(Array.Empty<string>());

    private readonly IReadOnlyList<string> _needles;

    public SecretMasker(IEnumerable<string?> secretValues)
    {
        var needles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in secretValues)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            needles.Add(value);
            // As JSON text: the executor's own (System.Text.Json escapes quotes and '<' as \uXXXX)
            // and the common minimal form other tools print (\" and \\ only).
            foreach (var escaped in new[] { JsonSerializer.Serialize(value), JsonSerializer.Serialize(value, RelaxedJson) })
            {
                needles.Add(escaped[1..^1]);
            }
            if (value.Contains('\n'))
            {
                foreach (var line in value.Split('\n', StringSplitOptions.TrimEntries))
                {
                    if (line.Length >= MinLineLength)
                    {
                        needles.Add(line);
                    }
                }
            }
        }

        // Longest first, so a secret that contains another is masked whole.
        _needles = needles.OrderByDescending(n => n.Length).ToList();
    }

    /// <summary>The values of the named parameters, i.e. the ones that hold resolved secrets.</summary>
    public static SecretMasker For(IReadOnlyCollection<string>? secretNames, IReadOnlyDictionary<string, string?> parameters)
        => new((secretNames ?? Array.Empty<string>()).Select(n => parameters.TryGetValue(n, out var v) ? v : null));

    public bool IsEmpty => _needles.Count == 0;

    public string Apply(string text)
    {
        foreach (var needle in _needles)
        {
            text = text.Replace(needle, Mask, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>
    /// A stream that masks the secrets in the text written through it and passes the rest
    /// to <paramref name="inner"/>. It works line by line (a secret never spans lines except
    /// a multi-line one, which is masked per line), buffering a partial line up to
    /// <paramref name="maxLineBytes"/> before it has to flush it as it is.
    /// </summary>
    public Stream Wrap(Stream inner, int maxLineBytes = 1 << 20) => new MaskingStream(inner, this, maxLineBytes);

    private sealed class MaskingStream(Stream inner, SecretMasker masker, int maxLineBytes) : Stream
    {
        private readonly List<byte> _pending = new();

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (var i = offset; i < offset + count; i++)
            {
                _pending.Add(buffer[i]);
                if (buffer[i] == (byte)'\n' || _pending.Count >= maxLineBytes)
                {
                    FlushPending();
                }
            }
        }

        public override void Flush()
        {
            FlushPending();
            inner.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushPending();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        private void FlushPending()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var text = Encoding.UTF8.GetString(_pending.ToArray());
            _pending.Clear();
            var bytes = Encoding.UTF8.GetBytes(masker.Apply(text));
            inner.Write(bytes, 0, bytes.Length);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
