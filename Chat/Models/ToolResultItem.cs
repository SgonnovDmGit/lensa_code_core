using System.Text.Json.Serialization;

namespace LenSa.Core.Chat.Models
{
    public static class ToolResultStatus
    {
        public const string Success = "success";
        public const string Failure = "failure";
    }

    public class ToolResultItem
    {
        [JsonPropertyName("toolUseId")]
        public string ToolUseId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        [JsonPropertyName("dataB64")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DataB64 { get; set; }

        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }

        [JsonPropertyName("contextB64")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ContextB64 { get; set; }

        [JsonPropertyName("failureType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FailureType { get; set; }
    }
}
