// This file is part of the project licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Serilog.Events;
using System.Buffers;

namespace Serilog.Sinks.Loki.Internal
{
    /// <summary>
    /// Reusable buffer for building flattened structured metadata names (e.g. <c>Prop__Child__0</c>)
    /// without allocating an intermediate string per nesting level.
    /// Names are sanitized to the Prometheus label name charset (<c>[a-zA-Z_][a-zA-Z0-9_]*</c>), the same way
    /// Loki 3.5+ does on ingestion: older Loki versions store invalid names as-is and then fail every query on the stream.
    /// </summary>
    internal sealed class PropertyNameBuilder : IDisposable
    {
        private const string _separator = "__";
        private const string _null = "<null>";
        private const string _digitPrefix = "key_";

        private char[] _buffer;

        internal PropertyNameBuilder(int initialCapacity = 128)
        {
            _buffer = ArrayPool<char>.Shared.Rent(initialCapacity);
        }

        public int Length { get; private set; }

        public ReadOnlySpan<char> Name => _buffer.AsSpan(0, Length);

        public void Reset(string name)
        {
            Length = 0;

            if (name.Length > 0 && IsAsciiDigit(name[0]))
            {
                Append(_digitPrefix.AsSpan());
            }

            Append(name.AsSpan());
        }

        public void Truncate(int length)
        {
            Length = length;
        }

        public void AppendSeparator() => Append(_separator.AsSpan());

        public void Append(ReadOnlySpan<char> value)
        {
            EnsureCapacity(value.Length);
            var start = Length;
            value.CopyTo(_buffer.AsSpan(Length));
            Length += value.Length;
            Sanitize(start);
        }

        public void Append(int value)
        {
#if NETCOREAPP
            int charsWritten;
            while (!value.TryFormat(_buffer.AsSpan(Length), out charsWritten, default, System.Globalization.CultureInfo.InvariantCulture))
            {
                Grow(_buffer.Length * 2);
            }
            Length += charsWritten;
#else
            Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture).AsSpan());
#endif
        }

        /// <summary>
        /// Appends a dictionary key. Output matches <see cref="object.ToString"/> of the key value.
        /// </summary>
        public void Append(ScalarValue key)
        {
            switch (key.Value)
            {
                case null:
                    Append(_null.AsSpan());
                    break;
                case string stringValue:
                    Append(stringValue.AsSpan());
                    break;
#if NETCOREAPP
                case ISpanFormattable formattable:
                    int charsWritten;
                    // null provider == current culture, same as ToString()
                    while (!formattable.TryFormat(_buffer.AsSpan(Length), out charsWritten, default, null))
                    {
                        Grow(_buffer.Length * 2);
                    }
                    var start = Length;
                    Length += charsWritten;
                    Sanitize(start);
                    break;
#endif
                default:
                    Append((key.Value.ToString() ?? _null).AsSpan());
                    break;
            }
        }

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private static bool IsValidNameChar(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || IsAsciiDigit(c) || c == '_';

        /// <summary>
        /// Replaces every invalid character (a surrogate pair counts as one) from <paramref name="start"/> with '_'.
        /// </summary>
        private void Sanitize(int start)
        {
            var write = start;

            for (int read = start; read < Length; read++, write++)
            {
                var c = _buffer[read];

                if (IsValidNameChar(c))
                {
                    _buffer[write] = c;
                    continue;
                }

                if (char.IsHighSurrogate(c) && read + 1 < Length && char.IsLowSurrogate(_buffer[read + 1]))
                {
                    read++;
                }

                _buffer[write] = '_';
            }

            Length = write;
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint <= _buffer.Length - Length)
            {
                return;
            }

            Grow(Math.Max(_buffer.Length * 2, Length + sizeHint));
        }

        private void Grow(int newSize)
        {
            var newBuffer = ArrayPool<char>.Shared.Rent(newSize);
            _buffer.AsSpan(0, Length).CopyTo(newBuffer);
            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        public void Dispose()
        {
            if (_buffer is null)
            {
                return;
            }

            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = null!;
            Length = 0;
        }
    }
}
