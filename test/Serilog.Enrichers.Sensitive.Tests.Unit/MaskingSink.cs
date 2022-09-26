﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Serilog.Enrichers.Sensitive.Tests.Unit
{
    public class MaskingSink : ILogEventSink
    {
        private readonly MaskingMode _maskingMode;

        private static readonly MessageTemplateParser Parser = new MessageTemplateParser();
        private readonly List<IMaskingOperator> _maskingOperators;
        private readonly string _maskValue;
        private readonly List<string> _maskProperties;
        private readonly List<string> _excludeProperties;
        private readonly ILogEventSink _aggregateSink;

        public MaskingSink(
            ILogEventSink aggregateSink,
            Action<SensitiveDataEnricherOptions> options)
        {
            _aggregateSink = aggregateSink;

            var enricherOptions = new SensitiveDataEnricherOptions();

            if (options != null)
            {
                options(enricherOptions);
            }

            if (string.IsNullOrEmpty(enricherOptions.MaskValue))
            {
                throw new Exception("The mask must be a non-empty string");
            }

            _maskingMode = enricherOptions.Mode;
            _maskValue = enricherOptions.MaskValue;
            _maskProperties = enricherOptions.MaskProperties ?? new List<string>();
            _excludeProperties = enricherOptions.ExcludeProperties ?? new List<string>();

            _maskingOperators = enricherOptions.MaskingOperators.ToList();
        }

        public void Emit(LogEvent logEvent)
        {
            if (_maskingMode == MaskingMode.Globally || SensitiveArea.Instance != null)
            {
                _aggregateSink.Emit(Mask(logEvent));
            }
            else
            {
                _aggregateSink.Emit(logEvent);
            }
        }

        private Exception MaskException(Exception exception)
        {
            var exceptionType = exception.GetType();
            var serializationInfo = new SerializationInfo(exceptionType, new FormatterConverter());
            var context = new StreamingContext();
            var maskedSerializationInfo = new SerializationInfo(exceptionType, new FormatterConverter());

            exception.GetObjectData(serializationInfo, context);

            var enumerator = serializationInfo.GetEnumerator();

            while (enumerator.MoveNext())
            {
                var entry = enumerator.Current;

                if (entry.Value is string stringValue)
                {
                    var maskedValue = ReplaceSensitiveDataFromString(stringValue);

                    maskedSerializationInfo.AddValue(entry.Name, maskedValue, entry.ObjectType);
                }
                else if (entry.Value is IDictionary dictionary)
                {
                    var replacementDictionary = new Dictionary<object, object>();

                    foreach (var key in dictionary.Keys)
                    {
                        if (dictionary[key] is string dictionaryStringValue)
                        {
                            var maskedValue = ReplaceSensitiveDataFromString(dictionaryStringValue);
                            replacementDictionary.Add(key, maskedValue);
                        }
                        else
                        {
                            replacementDictionary.Add(key, dictionary[key]);
                        }
                    }

                    maskedSerializationInfo.AddValue(entry.Name, replacementDictionary, entry.ObjectType);
                }
                else if (entry.Value is Exception[] innerExceptions)
                {
                    var replacementInnerExceptions = new List<Exception>();

                    foreach (var ex in innerExceptions)
                    {
                        replacementInnerExceptions.Add(MaskException(ex));
                    }

                    maskedSerializationInfo.AddValue(entry.Name, replacementInnerExceptions.ToArray(), entry.ObjectType);
                }
                else if (!typeof(AggregateException).IsAssignableFrom(exceptionType) && entry.Value is Exception innerException)
                {
                    var serializedMaskedException = MaskException(innerException);

                    maskedSerializationInfo.AddValue(entry.Name, serializedMaskedException, entry.ObjectType);
                }
                else
                {
                    maskedSerializationInfo.AddValue(entry.Name, entry.Value, entry.ObjectType);
                }
            }

            var deserializingConstructor = exceptionType
                .GetConstructor(BindingFlags.CreateInstance | BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    CallingConventions.Any,
                    new[] { typeof(SerializationInfo), typeof(StreamingContext) },
                    Array.Empty<ParameterModifier>());

            return deserializingConstructor
                .Invoke(
                    new object[]
                    {
                        maskedSerializationInfo,
                        context
                    }) as Exception;
        }

        private LogEvent Mask(LogEvent logEvent)
        {
            var messageTemplateText = ReplaceSensitiveDataFromString(logEvent.MessageTemplate.Text);

            var maskedProperties = new List<LogEventProperty>();

            foreach (var property in logEvent.Properties.ToList())
            {
                if (_excludeProperties.Contains(property.Key, StringComparer.InvariantCultureIgnoreCase))
                {
                    maskedProperties.Add(new LogEventProperty(property.Key, property.Value));
                    continue;
                }

                if (_maskProperties.Contains(property.Key, StringComparer.InvariantCultureIgnoreCase))
                {
                    maskedProperties.Add(
                        new LogEventProperty(
                            property.Key,
                            new ScalarValue(_maskValue)));
                }
                else if (property.Value is ScalarValue { Value: string stringValue })
                {
                    maskedProperties.Add(
                        new LogEventProperty(
                            property.Key,
                            new ScalarValue(ReplaceSensitiveDataFromString(stringValue))));
                }
            }

            var exception = logEvent.Exception;

            if (exception != null)
            {
                exception = MaskException(exception);
            }

            return new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                exception,
                Parser.Parse(messageTemplateText),
                maskedProperties);
        }

        private string ReplaceSensitiveDataFromString(string input)
        {
            foreach (var maskingOperator in _maskingOperators)
            {
                var maskResult = maskingOperator.Mask(input, _maskValue);

                if (maskResult.Match)
                {
                    input = maskResult.Result;
                }
            }

            return input;
        }
    }
}