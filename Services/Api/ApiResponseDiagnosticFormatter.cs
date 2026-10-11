using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace LenSa.Core.Services.Api;

internal static class ApiResponseDiagnosticFormatter
{
    public static string Format(string operation, int httpStatus, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return $"{operation} HTTP {httpStatus} bodyLength={body.Length}";
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return $"{operation} HTTP {httpStatus} bodyLength={body.Length}";

            var payload = root;
            if (TryGetObject(root, "result", out var result))
                payload = result;

            var status = ReadString(root, "status");
            var errorCode = ReadString(root, "errorCode");
            if (errorCode == null && TryGetObject(root, "serviceInfo", out var serviceInfo))
                errorCode = ReadString(serviceInfo, "errorCode");
            if (errorCode == null)
            {
                errorCode = ReadString(payload, "errorCode");
                if (errorCode == null && TryGetObject(payload, "serviceInfo", out var payloadServiceInfo))
                    errorCode = ReadString(payloadServiceInfo, "errorCode");
            }

            var contentLength = ReadString(payload, "content")?.Length ?? 0;
            var tools = ReadTools(payload);

            return $"{operation} HTTP {httpStatus} status={SafeField(status)} errorCode={SafeField(errorCode)} " +
                   $"contentLength={contentLength} toolCount={tools.Count} tools=[{string.Join(",", tools)}]";
        }
    }

    private static List<string> ReadTools(JsonElement payload)
    {
        var names = new List<string>();
        if (!TryGetArray(payload, "toolUses", out var tools))
            return names;

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.ValueKind == JsonValueKind.Object ? ReadString(tool, "name") : null;
            names.Add(SafeField(name));
        }

        return names;
    }

    private static string SafeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "none";

        const int maxLength = 80;
        var safe = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            if (safe.Length == maxLength)
                break;

            safe.Append(char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_');
        }

        return safe.Length == 0 ? "none" : safe.ToString();
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out value) &&
               value.ValueKind == JsonValueKind.Array;
    }
}
