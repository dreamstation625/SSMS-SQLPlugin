namespace SsmsSqlPlugin.Core.Completion
{
    public sealed class CompletionCandidate
    {
        public string DisplayText;
        public string InsertText;
        public string FilterText;
        public string Suffix;
        public string Description;
        public CompletionKind Kind;
        public int Rank;
    }
}
