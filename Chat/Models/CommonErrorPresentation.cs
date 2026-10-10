namespace LenSa.Core.Chat.Models
{
    public sealed record CommonErrorPresentation(
        string Message,
        string? ServerDetail = null,
        bool IsVerbatimServerText = false);
}
