// This file is part of the project licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Serilog.Events;
using Serilog.Parsing;
using Serilog.Sinks.Loki.Internal;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Serilog.Sinks.Loki.Tests
{

    public class LokiMessageWriterTest : IDisposable
    {
        private static readonly DateTimeOffset _date = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly ILokiExceptionFormatter _defaultExceptionFormatter = new DefaultLokiExceptionFormatter();

        private readonly MessageTemplateParser _messageTemplateParser = new();

        private readonly PooledByteBufferWriter _bufferWriter;
        private readonly Utf8JsonWriter _jsonWriter;

        public LokiMessageWriterTest()
        {
            _bufferWriter = new PooledByteBufferWriter();
            _jsonWriter = new Utf8JsonWriter(_bufferWriter, new() { Indented = true });
        }

        private string StringifyJsonPayload()
        {
            return Encoding.UTF8.GetString(_bufferWriter.WrittenMemory.Span.ToArray());
        }

        private LokiMessageWriter Create(LokiSinkConfigurations? configurations = null)
        {
            configurations ??= new LokiSinkConfigurations();

            var comparer = new LokiLogEventComparer(configurations);

            return new LokiMessageWriter(configurations, comparer, _defaultExceptionFormatter);
        }

        [Theory]
        [InlineData(LogEventLevel.Debug)]
        [InlineData(LogEventLevel.Information)]
        [InlineData(LogEventLevel.Verbose)]
        [InlineData(LogEventLevel.Warning)]
        [InlineData(LogEventLevel.Error)]
        [InlineData(LogEventLevel.Fatal)]
        public Task Should_write_simple_log_message_without_parameters(LogEventLevel level)
        {
            var logWriter = Create();

            var messageTemplate = _messageTemplateParser.Parse("log wihout parameters");

            var log = new LogEvent(_date, level, null, messageTemplate, []);

            logWriter.Write(_jsonWriter, [log]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload()).UseParameters(level);
        }

        [Fact]
        public Task Should_write_simple_log_message_with_global_lables()
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1"), new LokiLabel("label2", "value2")],
            });

            var messageTemplate = _messageTemplateParser.Parse("log wihout parameters");

            var log = new LogEvent(_date, LogEventLevel.Information, null, messageTemplate, []);

            logWriter.Write(_jsonWriter, [log]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload());
        }

        [Theory]
        [InlineData("value1")]
        [InlineData(42)]
#if NET
        [InlineData(42.43f)]
        [InlineData(42.44d)]
#endif
        [InlineData(45L)]
        [InlineData(46UL)]
        [InlineData(47U)]
        [InlineData('l')]
        [InlineData(null)]
        public Task Should_write_log_message_with_props_and_global_lables(object? scalarValue)
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1"), new LokiLabel("label2", "value2")],
            });

            var messageTemplate = _messageTemplateParser.Parse("log with parameters {Item}");

            LogEventProperty[] properties = [new LogEventProperty("Item", new ScalarValue(scalarValue))];

            var log = new LogEvent(_date, LogEventLevel.Information, null, messageTemplate, properties);

            logWriter.Write(_jsonWriter, [log]);

            _jsonWriter.Flush();

            var stringValue = scalarValue switch
            {
                null => "null",
                string => "string",
                uint => "uint",
                ulong => "ulong",
                long => "long",
                int => "int",
                char => "char",
                double => "double",
                float => "float",
                _ => throw new ArgumentOutOfRangeException(nameof(scalarValue))
            };
            return Verify(StringifyJsonPayload()).UseParameters(stringValue);
        }

        [Fact]
        public Task Should_write_logs_with_array_parameter()
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1"), new LokiLabel("label2", "value2")],
            });

            var messageTemplate = _messageTemplateParser.Parse("log with sequence parameters {Item}");

            var arrayParameter = new SequenceValue([new ScalarValue("value1"), new ScalarValue("value2")]);
            LogEventProperty[] properties = [new LogEventProperty("Item", arrayParameter)];

            var log = new LogEvent(_date, LogEventLevel.Information, null, messageTemplate, properties);

            logWriter.Write(_jsonWriter, [log]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("testType")]
        public Task Should_write_logs_with_object_parameter(string? typeTag)
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1"), new LokiLabel("label2", "value2")],
            });

            var messageTemplate = _messageTemplateParser.Parse("log with object parameters {Item}");

            var parameter = new StructureValue([
                new LogEventProperty("Property1", new ScalarValue("value1")),
                new LogEventProperty("Property2", new ScalarValue(42))
            ], typeTag);

            LogEventProperty[] properties = [new LogEventProperty("Item", parameter)];

            var log = new LogEvent(_date, LogEventLevel.Information, null, messageTemplate, properties);

            logWriter.Write(_jsonWriter, [log]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload()).UseParameters(typeTag ?? "empty");
        }

        [Fact]
        public Task Should_write_logs_with_dictionary_parameter()
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1"), new LokiLabel("label2", "value2")],
            });

            var messageTemplate = _messageTemplateParser.Parse("log with dictionary parameters {Item}");

            var parameter = new DictionaryValue(new Dictionary<ScalarValue, LogEventPropertyValue>()
            {
                { new ScalarValue("Key1"), new ScalarValue("value1") },
                { new ScalarValue("Key2"), new ScalarValue(42) }
            });
            LogEventProperty[] properties = [new LogEventProperty("Item", parameter)];

            var log = new LogEvent(_date, LogEventLevel.Information, null, messageTemplate, properties);

            logWriter.Write(_jsonWriter, [log]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload());
        }

        [Fact]
        public Task Should_write_logs_with_exception()
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1"), new LokiLabel("label2", "value2")],
            });

            var b = new MessageTemplateParser();

            var messageTemplate = b.Parse("log with error");

            var exception = new InvalidOperationException("Message from exception", new InvalidOperationException("Message from inner exception"));

            var log = new LogEvent(_date, LogEventLevel.Error, exception, messageTemplate, []);

            logWriter.Write(_jsonWriter, [log]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload());
        }

        [Fact]
        public Task Should_aggregate_logs_by_lables()
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1")],
            });

            var mtp = _messageTemplateParser;

            var log1_1 = new LogEvent(_date, LogEventLevel.Information, null, mtp.Parse("log #1.1"), []);
            var log1_2 = new LogEvent(_date, LogEventLevel.Information, null, mtp.Parse("log #1.2"), []);
            var log2_1 = new LogEvent(_date, LogEventLevel.Debug, null, mtp.Parse("log #2.1"), []);
            var log2_2 = new LogEvent(_date, LogEventLevel.Debug, null, mtp.Parse("log #2.2"), []);

            logWriter.Write(_jsonWriter, [log1_1, log1_2, log2_1, log2_2]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload());
        }

        [Fact]
        public Task Should_not_handle_log_level_as_lable()
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1")],
                HandleLogLevelAsLabel = false
            });

            var mtp = _messageTemplateParser;

            var log1 = new LogEvent(_date, LogEventLevel.Information, null, mtp.Parse("log #1.1"), []);
            var log2 = new LogEvent(_date, LogEventLevel.Debug, null, mtp.Parse("log #1.2"), []);

            logWriter.Write(_jsonWriter, [log1, log2]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload());
        }

        [Fact]
        public Task Should_add_property_as_lable()
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1")],
                HandleLogLevelAsLabel = false,
                PropertiesAsLabels = ["userId"]
            });

            var mtp = _messageTemplateParser;

            var userId = new LogEventProperty("userId", new ScalarValue(1));
            var log1 = new LogEvent(_date, LogEventLevel.Information, null, mtp.Parse("log #1.1"), [userId]);

            logWriter.Write(_jsonWriter, [log1]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload());
        }

        [Fact]
        public Task Should_add_property_out_of_message_template()
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                Labels = [new LokiLabel("label1", "value1")],
                HandleLogLevelAsLabel = false
            });

            var mtp = _messageTemplateParser;

            var userId = new LogEventProperty("userId", new ScalarValue(1));
            var log1 = new LogEvent(_date, LogEventLevel.Information, null, mtp.Parse("log #1.1"), [userId]);

            logWriter.Write(_jsonWriter, [log1]);

            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload());
        }

        [Fact]
        public Task Should_enrich_span_and_trace_ids()
        {
            var logWriter = Create(new LokiSinkConfigurations()
            {
                EnrichSpanId = true,
                EnrichTraceId = true
            });

            //hardcoded traceId and spanId
            var traceId = ActivityTraceId.CreateFromString("ed4ad863e4913c6183f28453ebac18e4".AsSpan());
            var activityId = ActivitySpanId.CreateFromString("effd9f82dde27762".AsSpan());

            var messageTemplate = _messageTemplateParser.Parse("log wihout parameters");

            var log = new LogEvent(_date, LogEventLevel.Debug, null, messageTemplate, [], traceId, activityId);

            logWriter.Write(_jsonWriter, [log]);
            _jsonWriter.Flush();

            return Verify(StringifyJsonPayload());
        }

        [Fact]
        public void Should_write_non_ascii_char_property_as_valid_utf8()
        {
            var logWriter = Create();

            var messageTemplate = _messageTemplateParser.Parse("char {c}");

            var log = new LogEvent(_date, LogEventLevel.Information, null, messageTemplate, [new LogEventProperty("c", new ScalarValue('é'))]);

            logWriter.Write(_jsonWriter, [log]);
            _jsonWriter.Flush();

            using var document = JsonDocument.Parse(_bufferWriter.WrittenMemory);
            var entry = document.RootElement.GetProperty("streams")[0].GetProperty("values")[0];

            Assert.Equal("char é", entry[1].GetString());
            Assert.Equal("é", entry[2].GetProperty("c").GetString());
        }

        [Fact]
        public void Should_flatten_ushort_and_null_dictionary_key()
        {
            var logWriter = Create();

            var messageTemplate = _messageTemplateParser.Parse("values {u} {d}");

            var dictionary = new DictionaryValue([
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue(null), new ScalarValue(1)),
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue(2.5), new ScalarValue("x")),
            ]);

            var log = new LogEvent(_date, LogEventLevel.Information, null, messageTemplate, [
                new LogEventProperty("u", new ScalarValue((ushort)42)),
                new LogEventProperty("d", dictionary),
            ]);

            logWriter.Write(_jsonWriter, [log]);
            _jsonWriter.Flush();

            using var document = JsonDocument.Parse(_bufferWriter.WrittenMemory);
            var metadata = document.RootElement.GetProperty("streams")[0].GetProperty("values")[0][2];

            Assert.Equal("42", metadata.GetProperty("u").GetString());
            Assert.Equal("1", metadata.GetProperty("d__<null>").GetString());
            Assert.Equal("x", metadata.GetProperty($"d__{2.5}").GetString());
        }

        [Fact]
        public void Should_flatten_nested_structures_into_prefixed_names()
        {
            var logWriter = Create();

            var messageTemplate = _messageTemplateParser.Parse("nested {@o}");

            var structure = new StructureValue([
                new LogEventProperty("a", new SequenceValue([new ScalarValue(1), new StructureValue([new LogEventProperty("b", new ScalarValue("deep"))])])),
                new LogEventProperty("c", new ScalarValue(true)),
            ]);

            var log = new LogEvent(_date, LogEventLevel.Information, null, messageTemplate, [new LogEventProperty("o", structure)]);

            logWriter.Write(_jsonWriter, [log, log]);
            _jsonWriter.Flush();

            using var document = JsonDocument.Parse(_bufferWriter.WrittenMemory);
            var values = document.RootElement.GetProperty("streams")[0].GetProperty("values");

            foreach (var entry in values.EnumerateArray())
            {
                var metadata = entry[2];
                Assert.Equal("1", metadata.GetProperty("o__a__0").GetString());
                Assert.Equal("deep", metadata.GetProperty("o__a__1__b").GetString());
                Assert.Equal("true", metadata.GetProperty("o__c").GetString());
                Assert.Equal(3, metadata.EnumerateObject().Count());
            }
        }

        public void Dispose()
        {
            _bufferWriter.Dispose();
            _jsonWriter.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}