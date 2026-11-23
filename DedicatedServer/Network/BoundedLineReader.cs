using System.Text;

namespace SimsOnline.DedicatedServer.Network;

/// <summary>
/// Reads newline-terminated lines from a public-protocol connection, like
/// <see cref="StreamReader.ReadLineAsync(CancellationToken)"/>, but refuses a line longer than a limit instead of
/// buffering it without bound: a peer that never sends a newline could otherwise grow one line until the
/// controller runs out of memory. Lines end at <c>\n</c>; a trailing <c>\r</c> is dropped. The protocol is
/// newline-delimited JSON, so a lone <c>\r</c> is not a line break.
/// </summary>
internal sealed class BoundedLineReader
{
    private readonly TextReader _reader;
    private readonly int _maxLineChars;
    private readonly char[] _buffer = new char[4096];
    private readonly StringBuilder _line = new();
    private int _start;
    private int _end;

    public BoundedLineReader(TextReader reader, int maxLineChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineChars, 1);
        _reader = reader;
        _maxLineChars = maxLineChars;
    }

    public int MaxLineChars => _maxLineChars;

    /// <summary>The next line, or null at end of stream.</summary>
    /// <exception cref="LineTooLongException">The line exceeds the limit; the connection should be closed.</exception>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        _line.Clear();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
                if (_end == 0)
                {
                    return _line.Length > 0 ? TrimCarriageReturn() : null;
                }
            }

            int newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
            int stop = newline >= 0 ? newline : _end;
            if (_line.Length + (stop - _start) > _maxLineChars)
            {
                throw new LineTooLongException(_maxLineChars);
            }

            _line.Append(_buffer, _start, stop - _start);
            if (newline >= 0)
            {
                _start = newline + 1;
                return TrimCarriageReturn();
            }

            _start = _end;
        }
    }

    private string TrimCarriageReturn()
    {
        if (_line.Length > 0 && _line[^1] == '\r')
        {
            _line.Length--;
        }

        return _line.ToString();
    }
}

/// <summary>A public-protocol line exceeded <see cref="BoundedLineReader.MaxLineChars"/>.</summary>
internal sealed class LineTooLongException(int maxLineChars)
    : IOException($"Command line exceeds {maxLineChars} characters.")
{
    public int MaxLineChars { get; } = maxLineChars;
}
