using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SsmsSqlPlugin.Formatting.Tokens
{
    /// <summary>
    /// SSMS 21 自带 ScriptDom 17.x，SSMS 22 自带 18.x。
    /// 两套构建引用各自版本的编译程序集，这里只保留解析器入口差异。
    /// </summary>
    internal static class ScriptDomCompat
    {
        public static TSqlParser CreateParser()
        {
#if SSMS21
            return new TSql170Parser(initialQuotedIdentifiers: true);
#else
            return new TSql180Parser(initialQuotedIdentifiers: true);
#endif
        }
    }
}
