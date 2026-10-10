using System;
using System.Collections.Generic;
using System.Linq;
using LenSa.Core.Services.Api;

namespace LenSa.Core.Helpers
{
    public sealed record ConversationModelDecision(ModelDto? Model, string? MissingModelId);

    public static class ConversationModelDecisionPolicy
    {
        public static ConversationModelDecision Decide(string? historicalModelId, IReadOnlyList<ModelDto> catalog)
        {
            if (string.IsNullOrWhiteSpace(historicalModelId))
                return new ConversationModelDecision(null, null);

            var exact = catalog.FirstOrDefault(m => string.Equals(m.Id, historicalModelId, StringComparison.Ordinal));
            if (exact != null)
                return new ConversationModelDecision(exact, null);

            return new ConversationModelDecision(catalog.FirstOrDefault(), historicalModelId);
        }
    }
}
