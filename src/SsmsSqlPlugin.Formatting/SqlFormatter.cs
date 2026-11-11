using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SsmsSqlPlugin.Formatting
{
    /// <summary>
    /// T-SQL 格式化入口。
    ///
    /// 设计边界（S1.1）：
    /// - 按 GO 分批解析，单批失败只影响该批，其余批照常格式化；
    /// - 含注释的批**暂时跳过**，因为当前用的是 ScriptDom 的生成器，它会丢注释。
    ///   宁可这一批不格式化，也不能把用户的注释吃掉。等 S1.2 的自研 printer 上线再开。
    /// - 任何异常都不往外抛，最坏情况是原样返回。
    /// </summary>
    public sealed class SqlFormatter
    {
        private static readonly Regex GoLine =
            new Regex(@"^[ \t]*GO[ \t]*(\d+)?[ \t]*(--.*)?$",
                      RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        public FormatResult Format(string sql, FormatOptions options = null)
        {
            options = options ?? FormatOptions.Default;

            if (string.IsNullOrWhiteSpace(sql))
            {
                return FormatResult.Empty(sql ?? string.Empty);
            }

            var batches = SplitBatches(sql);
            var output = new StringBuilder();
            var errors = new List<FormatError>();
            int formatted = 0, skipped = 0;

            for (int i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];

                if (!batch.IsCode)
                {
                    output.Append(batch.Text);
                    continue;
                }

                var outcome = FormatBatch(batch.Text, options);
                if (outcome.Error != null)
                {
                    errors.Add(outcome.Error);
                }

                if (outcome.Formatted != null)
                {
                    // 生成器会吃掉批前后的空白，这里把原批的换行边界补回来，
                    // 否则会和相邻的 GO 行粘在一起（"GOUPDATE"）。
                    output.Append(LeadingWhitespace(batch.Text));
                    output.Append(outcome.Formatted.TrimEnd());
                    if (batch.Text.EndsWith("\n", StringComparison.Ordinal))
                    {
                        output.Append('\n');
                    }

                    formatted++;
                }
                else
                {
                    output.Append(batch.Text);
                    skipped++;
                }
            }

            return new FormatResult(output.ToString(), errors, formatted, skipped);
        }

        private static (string Formatted, FormatError Error) FormatBatch(string text, FormatOptions options)
        {
            if (ContainsComment(text))
            {
                // 注释会被生成器丢掉，这一批不动。
                return (null, null);
            }

            IList<ParseError> parseErrors;
            TSqlFragment tree;
            try
            {
                var parser = new TSql180Parser(initialQuotedIdentifiers: true);
                using (var reader = new StringReader(text))
                {
                    tree = parser.Parse(reader, out parseErrors);
                }

                if (parseErrors != null && parseErrors.Count > 0)
                {
                    var first = parseErrors[0];
                    return (null, new FormatError(first.Line, first.Column, first.Message));
                }

                var gen = new Sql180ScriptGenerator(new SqlScriptGeneratorOptions
                {
                    KeywordCasing = options.UppercaseKeywords ? KeywordCasing.Uppercase : KeywordCasing.Lowercase,
                    IndentationSize = options.IndentSize,
                    IncludeSemicolons = options.IncludeSemicolons,
                    AlignClauseBodies = true,
                    MultilineSelectElementsList = true,
                    MultilineWherePredicatesList = true,
                    MultilineSetClauseItems = true,
                    NewLineBeforeFromClause = true,
                    NewLineBeforeWhereClause = true,
                    NewLineBeforeGroupByClause = true,
                    NewLineBeforeOrderByClause = true,
                    NewLineBeforeJoinClause = true,
                });

                gen.GenerateScript(tree, out string formatted);
                return (formatted, null);
            }
            catch (Exception ex)
            {
                return (null, new FormatError(0, 0, ex.Message));
            }
        }

        private static bool ContainsComment(string text)
        {
            try
            {
                var parser = new TSql180Parser(initialQuotedIdentifiers: true);
                using (var reader = new StringReader(text))
                {
                    IList<ParseError> errors;
                    var tokens = parser.GetTokenStream(reader, out errors);
                    return tokens.Any(t =>
                        t.TokenType == TSqlTokenType.SingleLineComment ||
                        t.TokenType == TSqlTokenType.MultilineComment);
                }
            }
            catch
            {
                // 判断不了就当有注释，保守跳过。
                return true;
            }
        }

        /// <summary>按 GO 切批，GO 行本身保留原样。</summary>
        private static List<Batch> SplitBatches(string sql)
        {
            var result = new List<Batch>();
            int last = 0;

            foreach (Match m in GoLine.Matches(sql))
            {
                if (m.Index > last)
                {
                    result.Add(new Batch(sql.Substring(last, m.Index - last), true));
                }

                result.Add(new Batch(m.Value, false));
                last = m.Index + m.Length;
            }

            if (last < sql.Length)
            {
                result.Add(new Batch(sql.Substring(last), true));
            }

            return result;
        }

        private readonly struct Batch
        {
            public Batch(string text, bool isCode)
            {
                Text = text;
                IsCode = isCode;
            }

            public string Text { get; }
            public bool IsCode { get; }
        }
    }
}
