namespace SsmsSqlPlugin.Core.Completion
{
    public sealed class CompletionOptions
    {
        public bool UppercaseKeywords = true;
        public int MaxItems = 200;

        public static readonly CompletionOptions Default = new CompletionOptions();
    }
}
