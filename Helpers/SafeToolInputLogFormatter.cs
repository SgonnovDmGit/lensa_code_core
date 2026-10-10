using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LenSa.Core.Helpers
{
    /// <summary>Builds a bounded diagnostic summary without exposing tool payload values.</summary>
    public static class SafeToolInputLogFormatter
    {
        private const int MaxRecordLength = 512;
        private const int MaxSelectorLength = 120;

        public static string Format(string? toolName, JsonElement input)
        {
            var name = SafeText(toolName, 80);
            var selectors = GetSelectors(name);
            var fields = new List<string>();

            if (input.ValueKind == JsonValueKind.Object)
            {
                foreach (var selector in selectors)
                {
                    if (!input.TryGetProperty(selector, out var value)) continue;
                    if (value.ValueKind == JsonValueKind.String)
                        fields.Add($"{selector}=\"{SafeSelector(value.GetString())}\"");
                    else if (value.ValueKind == JsonValueKind.Number)
                        fields.Add($"{selector}={SafeNumber(value.GetRawText())}");
                }

                var metadata = new List<string>();
                foreach (var property in input.EnumerateObject())
                {
                    if (selectors.Contains(property.Name, StringComparer.Ordinal) || IsSensitiveKey(property.Name)) continue;
                    metadata.Add($"{SafeKey(property.Name)}:{Kind(property.Value)}");
                }
                metadata.Sort(StringComparer.Ordinal);
                if (metadata.Count > 0)
                    fields.Add("fields=[" + string.Join(',', metadata.Take(24)) + (metadata.Count > 24 ? ",…" : string.Empty) + "]");
            }
            else if (input.ValueKind != JsonValueKind.Undefined && input.ValueKind != JsonValueKind.Null)
            {
                fields.Add("shape=" + Kind(input));
            }

            var record = $"tool={name} input={{{string.Join(',', fields)}}}";
            return record.Length <= MaxRecordLength ? record : record[..(MaxRecordLength - 1)] + "…";
        }

        private static string[] GetSelectors(string name) => name switch
        {
            "read_oneC_procedure" => ["name", "target"],
            "list_oneC_procedures" => ["target"],
            "activate_oneC_window" => ["windowId"],
            "apply_oneC_edit" => ["name", "target", "windowId"],
            _ => []
        };

        private static string SafeSelector(string? value)
        {
            if (value == null) return "";
            if (value.Any(char.IsControl)) return "<omitted-control-text>";
            var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            return escaped.Length <= MaxSelectorLength ? escaped : escaped[..MaxSelectorLength] + "…";
        }

        private static string SafeNumber(string value) => value.Length <= 32 ? value : "<omitted-number>";

        private static string SafeText(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "<unknown>";
            var safe = new string(value.Select(character => char.IsControl(character) ? '_' : character).ToArray());
            return safe.Length <= maxLength ? safe : safe[..maxLength] + "…";
        }

        private static string SafeKey(string key)
        {
            var safe = new string(key.Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
            return safe.Length <= 48 ? safe : safe[..48] + "…";
        }

        private static bool IsSensitiveKey(string key)
        {
            var normalized = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return normalized.Contains("secret", StringComparison.Ordinal)
                || normalized.Contains("password", StringComparison.Ordinal)
                || normalized.Contains("token", StringComparison.Ordinal)
                || normalized.Contains("apikey", StringComparison.Ordinal)
                || normalized.Contains("authorization", StringComparison.Ordinal)
                || normalized.Contains("cookie", StringComparison.Ordinal)
                || normalized.Contains("credential", StringComparison.Ordinal);
        }

        private static string Kind(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => $"array({value.GetArrayLength()})",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => "undefined"
        };
    }
}
