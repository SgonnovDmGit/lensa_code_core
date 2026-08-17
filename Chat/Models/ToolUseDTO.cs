using System.Text.Json;
using System.Text.Json.Serialization;

namespace LenSa.Core.Chat.Models
{
    public class ToolUseDTO
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("input")]
        public JsonElement Input { get; set; }
    }
}
