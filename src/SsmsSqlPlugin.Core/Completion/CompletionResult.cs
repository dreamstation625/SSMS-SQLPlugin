using System.Collections.Generic;

namespace SsmsSqlPlugin.Core.Completion
{
    public sealed class CompletionResult
    {
        public readonly List<CompletionCandidate> Items = new List<CompletionCandidate>();
        public int PrefixStart;
        public int PrefixLength;
        public string Prefix = string.Empty;
        public string Slot = "default";
        public string Error;
    }
}
