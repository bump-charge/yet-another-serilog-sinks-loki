// This file is part of the project licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Serilog.Events;
using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace Serilog.Sinks.Loki.Internal
{
    internal class LokiMessageWriter
    {
        private const string _levelLiteral = "level";
        private static readonly JsonEncodedText _stream = JsonEncodedText.Encode("stream");
        private static readonly JsonEncodedText _streams = JsonEncodedText.Encode("streams");
        private static readonly JsonEncodedText _level = JsonEncodedText.Encode(_levelLiteral);
        private static readonly JsonEncodedText _values = JsonEncodedText.Encode("values");
        private static readonly JsonEncodedText _message = JsonEncodedText.Encode("Message");
        private static readonly JsonEncodedText _messageTemplate = JsonEncodedText.Encode("MessageTemplate");
        private static readonly JsonEncodedText _exception = JsonEncodedText.Encode("Exception");
        private static readonly JsonEncodedText _traceId = JsonEncodedText.Encode("TraceId");
        private static readonly JsonEncodedText _spanId = JsonEncodedText.Encode("SpanId");
        private static readonly JsonEncodedText _trace = JsonEncodedText.Encode("trace");
        private static readonly JsonEncodedText _debug = JsonEncodedText.Encode("debug");
        private static readonly JsonEncodedText _info = JsonEncodedText.Encode("info");
        private static readonly JsonEncodedText _warning = JsonEncodedText.Encode("warning");
        private static readonly JsonEncodedText _error = JsonEncodedText.Encode("error");
        private static readonly JsonEncodedText _critical = JsonEncodedText.Encode("critical");
        private static readonly JsonEncodedText _unknown = JsonEncodedText.Encode("unknown");
        private static readonly JsonEncodedText _type = JsonEncodedText.Encode("type");
        private static readonly JsonEncodedText _nullValue = JsonEncodedText.Encode("<null>");

        private readonly LokiSinkConfigurations _configurations;
        private readonly LokiLogEventComparer _comparer;
        private readonly ILokiExceptionFormatter _exceptionFormatter;
        internal LokiMessageWriter(LokiSinkConfigurations configurations, LokiLogEventComparer comparer, ILokiExceptionFormatter exceptionFormatter)
        {
            _configurations = configurations;
            _comparer = comparer;
            _exceptionFormatter = exceptionFormatter;
        }

        private static JsonEncodedText MapLogLevel(LogEventLevel logLevel)
        {
            return logLevel switch
            {
                LogEventLevel.Verbose => _trace,
                LogEventLevel.Debug => _debug,
                LogEventLevel.Information => _info,
                LogEventLevel.Warning => _warning,
                LogEventLevel.Error => _error,
                LogEventLevel.Fatal => _critical,
                _ => _unknown
            };
        }

        public void Write(Utf8JsonWriter writer, IReadOnlyCollection<LogEvent> events)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(_streams);
            writer.WriteStartArray();

            using var context = new WriteContext();

            foreach (var stream in events.GroupBy(x => x, _comparer))
            {
                WriteStream(writer, stream.Key, stream, context);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private void WriteLabels(Utf8JsonWriter writer, LogEvent head)
        {
            writer.WritePropertyName(_stream);
            writer.WriteStartObject();

            if (_configurations.HandleLogLevelAsLabel)
            {
                writer.WritePropertyName(_level);
                writer.WriteStringValue(MapLogLevel(head.Level));
            }

            for (int i = 0; i < _configurations.Labels.Length; i++)
            {
                var label = _configurations.Labels[i];

                if (label.Key == _levelLiteral && _configurations.HandleLogLevelAsLabel)
                {
                    continue;
                }

                writer.WriteString(label.Key, label.Value);
            }

            for (int i = 0; i < _configurations.PropertiesAsLabels.Length; i++)
            {
                var label = _configurations.PropertiesAsLabels[i];

                if (head.Properties.TryGetValue(label, out var value))
                {
                    if (value is ScalarValue scalarValue && scalarValue.Value != null)
                    {
                        //if user want to expose log level as label and there are property with same name
                        //then we need to rename property name
                        if (label == _levelLiteral && _configurations.HandleLogLevelAsLabel)
                        {
                            writer.WritePropertyName(_level);
                        }
                        //if user already declared the same property as lable in global labels and in properties as labels
                        //then we need to rename property name
                        else if (Array.Exists(_configurations.Labels, x => x.Key == label))
                        {
                            writer.WritePropertyName($"_{label}");
                        }
                        else
                        {
                            writer.WritePropertyName(label);
                        }

                        if (!scalarValue.WriteAsStringValue(writer))
                        {
                            writer.WriteStringValue("");
                        }
                    }
                }
            }

            writer.WriteEndObject();
        }

        private static void WriteLogMessageStringValue(Utf8JsonWriter writer, LogEvent logEvent, Utf8TextWriter messageWriter)
        {
            messageWriter.Clear();
            logEvent.RenderMessage(messageWriter, CultureInfo.InvariantCulture);
            messageWriter.Flush();
            writer.WriteStringValue(messageWriter.WrittenMemory.Span);
        }

        private static void WriteStructuredValue(PropertyNameBuilder name, Utf8JsonWriter writer, StructureValue structureValue)
        {
            var prefixLength = name.Length;

            foreach (var item in structureValue.Properties)
            {
                name.AppendSeparator();
                name.Append(item.Name.AsSpan());
                WriteProperty(name, writer, item.Value);
                name.Truncate(prefixLength);
            }
        }

        private static void WriteSequenceValue(PropertyNameBuilder name, Utf8JsonWriter writer, SequenceValue sequence)
        {
            var prefixLength = name.Length;
            var elements = sequence.Elements;

            for (int i = 0; i < elements.Count; i++)
            {
                name.AppendSeparator();
                name.Append(i);
                WriteProperty(name, writer, elements[i]);
                name.Truncate(prefixLength);
            }
        }

        private static void WriteDictionaryValue(PropertyNameBuilder name, Utf8JsonWriter writer, DictionaryValue value)
        {
            if (value.Elements is null)
            {
                writer.WritePropertyName(name.Name);
                writer.WriteStringValue(_nullValue);
                return;
            }

            var prefixLength = name.Length;

            foreach (var item in value.Elements)
            {
                name.AppendSeparator();
                name.Append(item.Key);
                WriteProperty(name, writer, item.Value);
                name.Truncate(prefixLength);
            }
        }

        private static void WriteProperty(PropertyNameBuilder name, Utf8JsonWriter writer, LogEventPropertyValue? value)
        {
            switch (value)
            {
                case null:
                    writer.WritePropertyName(name.Name);
                    writer.WriteStringValue(_nullValue);
                    break;
                case ScalarValue scalarValue:
                    writer.WritePropertyName(name.Name);
                    scalarValue.WriteAsNonNullableStringValue(writer);
                    break;
                case DictionaryValue dictionaryValue:
                    WriteDictionaryValue(name, writer, dictionaryValue);
                    break;
                case SequenceValue sequenceValue:
                    WriteSequenceValue(name, writer, sequenceValue);
                    break;
                case StructureValue structureValue:
                    WriteStructuredValue(name, writer, structureValue);
                    break;
                default:
                    writer.WritePropertyName(name.Name);
                    writer.WriteStringValue(value.ToString());
                    break;
            }
        }

        private void WriteLogMessageAsJson(Utf8JsonWriter destination, LogEvent logEvent, WriteContext context)
        {
            WriteLogMessageStringValue(destination, logEvent, context.MessageWriter);

            destination.WriteStartObject();

            if (logEvent.Exception is not null)
            {
                _exceptionFormatter.Format(destination, logEvent.Exception);
            }

            if (_configurations.EnrichTraceId && logEvent.TraceId.HasValue)
            {
                destination.WriteString(_traceId, logEvent.TraceId.Value.ToString());
            }

            if (_configurations.EnrichSpanId && logEvent.SpanId.HasValue)
            {
                destination.WriteString(_spanId, logEvent.SpanId.Value.ToString());
            }

            foreach (var item in logEvent.Properties)
            {
                context.Name.Reset(item.Key);
                WriteProperty(context.Name, destination, item.Value);
            }

            destination.WriteEndObject();
        }

        private void WriteLogs(Utf8JsonWriter writer, IEnumerable<LogEvent> events, WriteContext context)
        {
            writer.WritePropertyName(_values);

            writer.WriteStartArray();

            Span<byte> buffer = stackalloc byte[25];

            foreach (var logEvent in events)
            {
                writer.WriteStartArray();

                var timestamp = logEvent.Timestamp.ToUnixNanoseconds();

                Utf8Formatter.TryFormat(timestamp, buffer, out var charsWritten);

                writer.WriteStringValue(buffer.Slice(0, charsWritten));

                WriteLogMessageAsJson(writer, logEvent, context);

                writer.WriteEndArray();

                buffer.Clear();
            }

            writer.WriteEndArray();
        }

        private void WriteStream(Utf8JsonWriter writer, LogEvent head, IEnumerable<LogEvent> events, WriteContext context)
        {
            writer.WriteStartObject();

            WriteLabels(writer, head);

            WriteLogs(writer, events, context);

            writer.WriteEndObject();
        }

        /// <summary>
        /// Scratch buffers shared by all events of a single batch.
        /// </summary>
        private sealed class WriteContext : IDisposable
        {
            private readonly PooledByteBufferWriter _messageBuffer = new();

            public WriteContext()
            {
                MessageWriter = new Utf8TextWriter(_messageBuffer);
            }

            public Utf8TextWriter MessageWriter { get; }

            public PropertyNameBuilder Name { get; } = new();

            public void Dispose()
            {
                MessageWriter.Dispose();
                _messageBuffer.Dispose();
                Name.Dispose();
            }
        }
    }
}
