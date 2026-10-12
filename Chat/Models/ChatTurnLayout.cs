using System;
using System.Collections.Generic;

namespace LenSa.Core.Chat.Models
{
    public enum ChatTurnPiece
    {
        None,
        Single,
        First,
        Middle,
        Last
    }

    /// <summary>Assigns connected assistant-turn layout and text-availability state for each turn.</summary>
    public static class ChatTurnLayout
    {
        public static void Assign(IList<ChatMessage> messages, bool turnInFlight, bool hasOlderMessages)
        {
            if (messages is null) throw new ArgumentNullException(nameof(messages));

            var count = messages.Count;
            var pieces = new ChatTurnPiece[count];
            var showFooters = new bool[count];
            var missingStarts = new bool[count];
            var canCollectText = new bool[count];

            var firstHistoryRow = 0;
            while (firstHistoryRow < count &&
                   messages[firstHistoryRow].Role == MessageRoles.Notice &&
                   messages[firstHistoryRow].IsHistoryPageError)
            {
                firstHistoryRow++;
            }

            var openTurnStart = hasOlderMessages && firstHistoryRow < count &&
                                messages[firstHistoryRow].Role == MessageRoles.Assistant
                ? firstHistoryRow
                : -1;

            var runStart = 0;
            while (runStart < count)
            {
                if (messages[runStart].Role != MessageRoles.Assistant)
                {
                    runStart++;
                    continue;
                }

                var runEnd = runStart;
                while (runEnd + 1 < count && messages[runEnd + 1].Role == MessageRoles.Assistant)
                    runEnd++;

                var runIsOpen = runStart == openTurnStart;
                var hasText = false;
                for (var index = runStart; index <= runEnd; index++)
                {
                    var message = messages[index];
                    if (!message.IsThinking && !string.IsNullOrWhiteSpace(message.Content))
                        hasText = true;
                }

                var runLength = runEnd - runStart + 1;
                for (var index = runStart; index <= runEnd; index++)
                {
                    var piece = runLength == 1
                        ? ChatTurnPiece.Single
                        : index == runStart
                            ? ChatTurnPiece.First
                            : index == runEnd
                                ? ChatTurnPiece.Last
                                : ChatTurnPiece.Middle;

                    if (runIsOpen && index == runStart)
                        piece = piece == ChatTurnPiece.Single ? ChatTurnPiece.Last : ChatTurnPiece.Middle;

                    pieces[index] = piece;
                    missingStarts[index] = runIsOpen;
                    canCollectText[index] = !runIsOpen && hasText;
                }

                var last = messages[runEnd];
                showFooters[runEnd] = !(runEnd == count - 1 && turnInFlight) && !last.IsThinking;
                runStart = runEnd + 1;
            }

            // Publish each final value once. Repeated Assign calls with unchanged inputs stay silent.
            for (var index = 0; index < count; index++)
            {
                var message = messages[index];
                message.TurnPiece = pieces[index];
                message.ShowTurnFooter = showFooters[index];
                message.IsTurnStartMissing = missingStarts[index];
                message.CanCollectTurnText = canCollectText[index];
            }
        }

        /// <summary>
        /// Collect visible text from the complete contiguous assistant series containing member.
        /// An open history start must never be silently reduced to its visible tail.
        /// </summary>
        public static string CollectText(IList<ChatMessage> messages, ChatMessage member)
        {
            if (messages is null) throw new ArgumentNullException(nameof(messages));
            if (member is null) throw new ArgumentNullException(nameof(member));
            if (member.Role != MessageRoles.Assistant ||
                member.IsTurnStartMissing ||
                !member.CanCollectTurnText)
                return string.Empty;

            var memberIndex = -1;
            for (var index = 0; index < messages.Count; index++)
            {
                if (!ReferenceEquals(messages[index], member)) continue;
                memberIndex = index;
                break;
            }
            if (memberIndex < 0) return string.Empty;

            var start = memberIndex;
            while (start > 0 && messages[start - 1].Role == MessageRoles.Assistant)
                start--;
            var end = memberIndex;
            while (end + 1 < messages.Count && messages[end + 1].Role == MessageRoles.Assistant)
                end++;

            if (messages[start].IsTurnStartMissing) return string.Empty;
            var parts = new List<string>();
            for (var index = start; index <= end; index++)
            {
                var message = messages[index];
                if (message.IsThinking || string.IsNullOrWhiteSpace(message.Content)) continue;
                parts.Add(message.Content);
            }
            return string.Join("\n\n", parts);
        }
    }
}
