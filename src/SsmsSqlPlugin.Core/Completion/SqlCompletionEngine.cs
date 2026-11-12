using System;
using System.Collections.Generic;
using SsmsSqlPlugin.Formatting.Tokens;
using SsmsSqlPlugin.Core.Metadata;

namespace SsmsSqlPlugin.Core.Completion
{
    /// <summary>
    /// 纯逻辑补全引擎：SQL 文本 + 光标 + 元数据 → 候选列表。
    /// 不碰 UI、不碰数据库，可独立测试。
    /// </summary>
    public sealed class SqlCompletionEngine
    {
        private enum Clause
        {
            None, Select, From, Join, Where, On, Group, Order, Having, Set,
            Exec, Insert, Update, Delete, Merge,
        }

        public CompletionResult GetCompletions(
            string sql,
            int caretOffset,
            MetadataSnapshot metadata,
            CompletionOptions options = null)
        {
            options = options ?? CompletionOptions.Default;
            var result = new CompletionResult();

            try
            {
                sql = sql ?? string.Empty;
                caretOffset = Math.Max(0, Math.Min(caretOffset, sql.Length));

                var window = SqlTextWindow.Around(sql, caretOffset);
                int localCaret = caretOffset - window.Start;
                int localPrefixStart = FindPrefixStart(window.Text, localCaret);
                string prefix = window.Text.Substring(localPrefixStart, localCaret - localPrefixStart);

                result.PrefixStart = window.Start + localPrefixStart;
                result.PrefixLength = prefix.Length;
                result.Prefix = prefix;

                var raw = SqlTokenizer.Tokenize(window.Text);
                var tokens = new List<SqlToken>();
                foreach (var t in raw.Tokens)
                {
                    if (t.Kind != SqlTokenKind.Comment) tokens.Add(t);
                }

                if (tokens.Count == 0)
                {
                    AddBuiltIns(result, prefix, options, 0, 0);
                    return result;
                }

                var lineStarts = BuildLineStarts(window.Text);
                int prevIndex = FindPreviousToken(tokens, lineStarts, localPrefixStart);
                int stmtStart = FindStatementStart(tokens, prevIndex);
                var depth = BuildDepth(tokens);

                int selectIndex = -1, fromIndex = -1, execIndex = -1;
                int insertIndex = -1, updateIndex = -1, deleteIndex = -1, mergeIndex = -1;

                for (int i = stmtStart; i < tokens.Count; i++)
                {
                    if (depth[i] != 0) continue;
                    var t = tokens[i];

                    if (selectIndex < 0 && t.Is("SELECT")) selectIndex = i;
                    if (fromIndex < 0 && t.Is("FROM")) fromIndex = i;
                    if (execIndex < 0 && t.IsAny("EXEC", "EXECUTE")) execIndex = i;
                    if (insertIndex < 0 && t.Is("INSERT")) insertIndex = i;
                    if (updateIndex < 0 && t.Is("UPDATE")) updateIndex = i;
                    if (deleteIndex < 0 && t.Is("DELETE")) deleteIndex = i;
                    if (mergeIndex < 0 && t.Is("MERGE")) mergeIndex = i;
                }

                var aliases = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
                var ctes = new List<TableMetadata>();
                var temps = new List<TableMetadata>();

                if (fromIndex >= 0)
                    BuildFromAliases(tokens, depth, fromIndex, metadata, aliases);

                ParseCtes(tokens, depth, stmtStart, selectIndex, aliases, ctes);
                ParseTempTables(tokens, aliases, temps);
                MergeScriptTemps(sql, aliases, temps);

                TableMetadata targetTable = null;
                if (updateIndex >= 0)
                    targetTable = ParseTarget(tokens, updateIndex + 1, metadata, aliases);
                else if (insertIndex >= 0)
                {
                    int into = FindKeyword(tokens, insertIndex + 1, "INTO");
                    if (into >= 0) targetTable = ParseTarget(tokens, into + 1, metadata, aliases);
                }
                else if (mergeIndex >= 0)
                {
                    int into = FindKeyword(tokens, mergeIndex + 1, "INTO");
                    if (into >= 0) targetTable = ParseTarget(tokens, into + 1, metadata, aliases);
                }

                string slot = "default";
                string schemaFilter = null;
                TableMetadata contextTable = null;
                bool afterDot = false;

                SqlToken previous = prevIndex >= 0 ? tokens[prevIndex] : null;

                if (previous != null && previous.IsText("."))
                {
                    afterDot = true;
                    int aliasIndex = prevIndex - 1;
                    string alias = aliasIndex >= 0 && IsNameToken(tokens[aliasIndex])
                        ? CleanIdentifier(tokens[aliasIndex].Text)
                        : null;

                    if (execIndex >= 0)
                    {
                        slot = "procedure";
                        schemaFilter = alias;
                    }
                    else
                    {
                        TableMetadata mapped;
                        if (alias != null && aliases.TryGetValue(alias, out mapped) &&
                            mapped.Columns.Count > 0)
                        {
                            contextTable = mapped;
                            slot = "column";
                        }
                        else if (alias != null && HasSchema(metadata, alias))
                        {
                            // FROM dbo. / FROM dbo.Us：dbo 是 schema，不是表别名
                            contextTable = null;
                            schemaFilter = alias;
                            slot = "table";
                        }
                        else if (alias != null && aliases.TryGetValue(alias, out mapped))
                        {
                            contextTable = mapped;
                            slot = "column";
                        }
                        else if (alias != null && metadata != null && metadata.Find(alias) != null)
                        {
                            contextTable = metadata.Find(alias);
                            slot = "column";
                        }
                        else
                        {
                            slot = "column";
                        }
                    }
                }
                else if (previous != null)
                {
                    if (IsTablePositionKeyword(previous))
                    {
                        slot = "table";
                    }
                    else if (previous.IsAny("EXEC", "EXECUTE"))
                    {
                        slot = "procedure";
                    }
                    else if (previous.IsText(","))
                    {
                        var clause = NearestClause(tokens, depth, stmtStart, prevIndex - 1);
                        slot = clause == Clause.From || clause == Clause.Join ? "table" : "column";
                    }
                    else if (IsTypeContext(tokens, depth, stmtStart, prevIndex))
                    {
                        slot = "type";
                    }
                    else
                    {
                        var clause = NearestClause(tokens, depth, stmtStart, prevIndex);

                        if (clause == Clause.Select || clause == Clause.Where ||
                            clause == Clause.On || clause == Clause.Order ||
                            clause == Clause.Group || clause == Clause.Having ||
                            clause == Clause.Set)
                        {
                            slot = "column";
                        }
                        else if (clause == Clause.From || clause == Clause.Join)
                        {
                            slot = "table";
                        }
                        else if (execIndex >= 0)
                        {
                            slot = "procedure";
                        }
                        else if (insertIndex >= 0)
                        {
                            slot = "table";
                        }
                        else
                        {
                            slot = "default";
                        }
                    }
                }
                else if (execIndex >= 0)
                {
                    slot = "procedure";
                }

                result.Slot = slot;

                var built = new List<CompletionCandidate>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (slot == "table")
                {
                    AddTables(built, seen, metadata, ctes, temps, schemaFilter, prefix);
                }
                else if (slot == "procedure")
                {
                    AddProcedures(built, seen, metadata, schemaFilter, prefix);
                }
                else if (slot == "type")
                {
                    AddEntries(built, seen, BuiltInCatalog.DataTypeEntries, prefix, options, 2000, true);
                }
                else if (slot == "column")
                {
                    AddColumns(built, seen, contextTable, aliases, targetTable, prefix);

                    if (!afterDot)
                    {
                        AddEntries(built, seen, BuiltInCatalog.FunctionEntries, prefix, options, 30, false);
                        AddEntries(built, seen, BuiltInCatalog.KeywordEntries, prefix, options, 0, false);
                    }
                }
                else
                {
                    AddEntries(built, seen, BuiltInCatalog.FunctionEntries, prefix, options, 30, false);
                    AddEntries(built, seen, BuiltInCatalog.KeywordEntries, prefix, options, 0, false);
                }

                if (built.Count == 0 && slot != "type")
                {
                    AddEntries(built, seen, BuiltInCatalog.FunctionEntries, prefix, options, 0, false);
                    AddEntries(built, seen, BuiltInCatalog.KeywordEntries, prefix, options, 0, false);
                }

                built.Sort(CompareCandidates);

                int max = options.MaxItems <= 0 ? 200 : options.MaxItems;
                for (int i = 0; i < built.Count && i < max; i++)
                    result.Items.Add(built[i]);

                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Slot = "error";
                AddBuiltIns(result, result.Prefix ?? string.Empty, options, 0, 0);
                return result;
            }
        }

        // ── 候选生成 ──

        private static void AddTables(
            List<CompletionCandidate> list,
            HashSet<string> seen,
            MetadataSnapshot metadata,
            List<TableMetadata> ctes,
            List<TableMetadata> temps,
            string schemaFilter,
            string prefix)
        {
            if (metadata != null)
            {
                foreach (var obj in metadata.Objects)
                {
                    if (!obj.IsTable) continue;
                    if (schemaFilter != null &&
                        !string.Equals(obj.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int score = Math.Max(
                        CompletionScorer.Score(obj.Name, prefix),
                        CompletionScorer.Score(obj.FullName, prefix));

                    if (prefix.Length > 0 && score == 0) continue;

                    AddCandidate(list, seen,
                        obj.Name,
                        QualifyForInsert(obj),
                        obj.FullName + " " + obj.Name,
                        obj.Schema,
                        KindText(obj) + " · " + obj.FullName,
                        obj.IsView ? CompletionKind.View : CompletionKind.Table,
                        score + 1200);
                }
            }

            AddTableObjectList(list, seen, ctes, prefix, "CTE", CompletionKind.Cte);
            AddTableObjectList(list, seen, temps, prefix, "临时表", CompletionKind.TempTable);
        }

        private static void AddTableObjectList(
            List<CompletionCandidate> list,
            HashSet<string> seen,
            List<TableMetadata> objects,
            string prefix,
            string label,
            CompletionKind kind)
        {
            if (objects == null) return;

            foreach (var obj in objects)
            {
                int score = Math.Max(
                    CompletionScorer.Score(obj.Name, prefix),
                    CompletionScorer.Score(obj.FullName, prefix));
                if (prefix.Length > 0 && score == 0) continue;

                AddCandidate(list, seen,
                    obj.Name,
                    QualifyForInsert(obj),
                    obj.FullName,
                    label,
                    label + " · " + obj.FullName,
                    kind,
                    score + 1200);
            }
        }

        private static void AddProcedures(
            List<CompletionCandidate> list,
            HashSet<string> seen,
            MetadataSnapshot metadata,
            string schemaFilter,
            string prefix)
        {
            if (metadata == null) return;

            foreach (var obj in metadata.Objects)
            {
                if (!obj.IsProcedure) continue;
                if (schemaFilter != null &&
                    !string.Equals(obj.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                int score = Math.Max(
                    CompletionScorer.Score(obj.Name, prefix),
                    CompletionScorer.Score(obj.FullName, prefix));
                if (prefix.Length > 0 && score == 0) continue;

                AddCandidate(list, seen,
                    obj.Name,
                    QualifyForInsert(obj),
                    obj.FullName + " " + obj.Name,
                    obj.Schema,
                    "存储过程 · " + obj.FullName,
                    CompletionKind.Procedure,
                    score + 1200);
            }
        }

        private static void AddColumns(
            List<CompletionCandidate> list,
            HashSet<string> seen,
            TableMetadata contextTable,
            Dictionary<string, TableMetadata> aliases,
            TableMetadata targetTable,
            string prefix)
        {
            var tables = new List<TableMetadata>();

            if (contextTable != null)
            {
                tables.Add(contextTable);
            }
            else if (targetTable != null)
            {
                tables.Add(targetTable);
            }
            else
            {
                foreach (var t in aliases.Values)
                {
                    if (t == null) continue;
                    bool exists = false;
                    for (int i = 0; i < tables.Count; i++)
                    {
                        if (ReferenceEquals(tables[i], t)) { exists = true; break; }
                    }
                    if (!exists) tables.Add(t);
                }
            }

            foreach (var table in tables)
            {
                foreach (var column in table.Columns)
                {
                    int score = CompletionScorer.Score(column.Name, prefix);
                    if (prefix.Length > 0 && score == 0) continue;

                    string desc = table.FullName + "." + column.Name +
                                  (string.IsNullOrEmpty(column.DataType) ? "" : " · " + column.DataType);

                    AddCandidate(list, seen,
                        column.Name,
                        QuoteIfNeeded(column.Name),
                        column.Name,
                        column.DataType,
                        desc,
                        CompletionKind.Column,
                        score + 1200);
                }
            }
        }

        private static void AddBuiltIns(
            CompletionResult result,
            string prefix,
            CompletionOptions options,
            int functionBonus,
            int keywordBonus)
        {
            var list = new List<CompletionCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddEntries(list, seen, BuiltInCatalog.FunctionEntries, prefix, options, functionBonus, false);
            AddEntries(list, seen, BuiltInCatalog.KeywordEntries, prefix, options, keywordBonus, false);
            list.Sort(CompareCandidates);

            int max = options.MaxItems <= 0 ? 200 : options.MaxItems;
            for (int i = 0; i < list.Count && i < max; i++) result.Items.Add(list[i]);
        }

        private static void AddEntries(
            List<CompletionCandidate> list,
            HashSet<string> seen,
            IReadOnlyList<BuiltInCatalog.Entry> entries,
            string prefix,
            CompletionOptions options,
            int bonus,
            bool forceUpper)
        {
            foreach (var entry in entries)
            {
                int score = CompletionScorer.Score(entry.Text, prefix);
                if (prefix.Length > 0 && score == 0) continue;

                string text = forceUpper || options.UppercaseKeywords
                    ? entry.Text
                    : entry.Text.ToLowerInvariant();

                AddCandidate(list, seen,
                    text,
                    text,
                    entry.Text,
                    null,
                    entry.Description,
                    entry.Kind,
                    score + bonus);
            }
        }

        private static void AddCandidate(
            List<CompletionCandidate> list,
            HashSet<string> seen,
            string display,
            string insert,
            string filter,
            string suffix,
            string description,
            CompletionKind kind,
            int rank)
        {
            if (!seen.Add(display)) return;

            list.Add(new CompletionCandidate
            {
                DisplayText = display,
                InsertText = insert,
                FilterText = filter,
                Suffix = suffix,
                Description = description,
                Kind = kind,
                Rank = rank,
            });
        }

        private static int CompareCandidates(CompletionCandidate a, CompletionCandidate b)
        {
            int c = b.Rank.CompareTo(a.Rank);
            if (c != 0) return c;
            return string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase);
        }

        private static string KindText(TableMetadata obj)
        {
            switch ((obj.Kind ?? string.Empty).Trim())
            {
                case "V": return "视图";
                case "TF":
                case "IF": return "表值函数";
                default: return "表";
            }
        }

        private static string QualifyForInsert(TableMetadata obj)
        {
            if (string.IsNullOrEmpty(obj.Schema) ||
                string.Equals(obj.Schema, "dbo", StringComparison.OrdinalIgnoreCase))
                return QuoteIfNeeded(obj.Name);

            return QuoteIfNeeded(obj.Schema) + "." + QuoteIfNeeded(obj.Name);
        }

        private static string QuoteIfNeeded(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$') continue;
                return "[" + name.Replace("]", "]]") + "]";
            }
            return name;
        }

        // ── FROM / JOIN 别名 ──

        private static void BuildFromAliases(
            List<SqlToken> tokens,
            int[] depth,
            int fromIndex,
            MetadataSnapshot metadata,
            Dictionary<string, TableMetadata> aliases)
        {
            bool expectTable = true;
            bool inOn = false;

            for (int i = fromIndex + 1; i < tokens.Count; i++)
            {
                if (depth[i] != 0) continue;
                var t = tokens[i];

                if (inOn)
                {
                    if (IsJoinKeyword(t) || t.Is("JOIN"))
                    {
                        inOn = false;
                        expectTable = true;
                        continue;
                    }
                    if (t.IsText(","))
                    {
                        inOn = false;
                        expectTable = true;
                        continue;
                    }
                    if (IsFromEnd(t)) break;
                    continue;
                }

                if (expectTable)
                {
                    if (t.IsText("("))
                    {
                        i = SkipParentheses(tokens, i);
                        string derivedAlias = ParseAlias(tokens, ref i);
                        if (!string.IsNullOrEmpty(derivedAlias))
                        {
                            var derived = new TableMetadata { Name = derivedAlias, Kind = "DERIVED" };
                            aliases[derivedAlias] = derived;
                        }
                        expectTable = false;
                        continue;
                    }

                    if (IsJoinKeyword(t) || t.Is("JOIN")) continue;
                    if (t.Is("ON")) { expectTable = false; inOn = true; continue; }
                    if (t.IsText(",")) continue;
                    if (IsFromEnd(t)) break;

                    string schema, name;
                    if (ParseQualifiedName(tokens, ref i, out schema, out name))
                    {
                        var table = FindTable(metadata, aliases, schema, name);
                        string alias = ParseAlias(tokens, ref i);
                        AddAlias(aliases, table, alias);
                        i--;
                        expectTable = false;
                        continue;
                    }

                    continue;
                }

                if (t.IsText(","))
                {
                    expectTable = true;
                    continue;
                }
                if (IsJoinKeyword(t) || t.Is("JOIN"))
                {
                    expectTable = true;
                    continue;
                }
                if (t.Is("ON"))
                {
                    inOn = true;
                    continue;
                }
                if (t.Is("WITH"))
                {
                    int next = i + 1;
                    if (next < tokens.Count && tokens[next].IsText("("))
                        i = SkipParentheses(tokens, next);
                    continue;
                }
                if (IsFromEnd(t)) break;
            }
        }

        private static bool IsFromEnd(SqlToken t)
        {
            return t.IsAny("WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT",
                           "INTERSECT", "OPTION", "FOR", "OUTPUT", "SET", "VALUES");
        }

        private static TableMetadata FindTable(
            MetadataSnapshot metadata,
            Dictionary<string, TableMetadata> aliases,
            string schema,
            string name)
        {
            TableMetadata table;
            if (!string.IsNullOrEmpty(schema) &&
                aliases.TryGetValue(schema + "." + name, out table))
                return table;

            if (aliases.TryGetValue(name, out table)) return table;

            if (metadata != null)
            {
                table = metadata.Find(schema, name);
                if (table == null && !string.IsNullOrEmpty(schema)) table = metadata.Find(name);
                if (table != null) return table;
            }

            return new TableMetadata { Schema = schema, Name = name, Kind = "UNKNOWN" };
        }

        private static void AddAlias(
            Dictionary<string, TableMetadata> aliases,
            TableMetadata table,
            string alias)
        {
            if (table == null) return;

            if (!string.IsNullOrEmpty(alias)) aliases[alias] = table;
            aliases[table.Name] = table;
            if (!string.IsNullOrEmpty(table.Schema))
                aliases[table.Schema + "." + table.Name] = table;
        }

        private static string ParseAlias(List<SqlToken> tokens, ref int i)
        {
            if (i < tokens.Count && tokens[i].Is("AS")) i++;
            if (i < tokens.Count && IsAliasToken(tokens[i]))
            {
                string alias = CleanIdentifier(tokens[i].Text);
                i++;
                return alias;
            }
            return null;
        }

        private static bool ParseQualifiedName(
            List<SqlToken> tokens,
            ref int i,
            out string schema,
            out string name)
        {
            schema = null;
            name = null;

            if (i >= tokens.Count || !IsNameToken(tokens[i])) return false;

            var parts = new List<string>(3);
            parts.Add(CleanIdentifier(tokens[i].Text));
            i++;

            while (i + 1 < tokens.Count && tokens[i].IsText(".") && IsNameToken(tokens[i + 1]))
            {
                parts.Add(CleanIdentifier(tokens[i + 1].Text));
                i += 2;
            }

            if (parts.Count >= 3)
            {
                schema = parts[parts.Count - 2];
                name = parts[parts.Count - 1];
            }
            else if (parts.Count == 2)
            {
                schema = parts[0];
                name = parts[1];
            }
            else
            {
                name = parts[0];
            }

            return !string.IsNullOrEmpty(name);
        }

        private static int SkipParentheses(List<SqlToken> tokens, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < tokens.Count; i++)
            {
                if (tokens[i].IsText("(")) depth++;
                else if (tokens[i].IsText(")"))
                {
                    depth--;
                    if (depth == 0) return i + 1;
                }
            }
            return tokens.Count;
        }

        // ── CTE / 临时表 ──

        private static void ParseCtes(
            List<SqlToken> tokens,
            int[] depth,
            int stmtStart,
            int selectIndex,
            Dictionary<string, TableMetadata> aliases,
            List<TableMetadata> ctes)
        {
            int limit = selectIndex >= 0 ? selectIndex : tokens.Count;
            if (stmtStart >= tokens.Count || !tokens[stmtStart].Is("WITH")) return;

            int i = stmtStart + 1;
            while (i < limit)
            {
                if (depth[i] != 0 || !IsNameToken(tokens[i])) break;

                string name = CleanIdentifier(tokens[i].Text);
                i++;

                if (i < limit && tokens[i].Is("AS")) i++;
                if (i >= limit || !tokens[i].IsText("(")) break;

                var cte = new TableMetadata { Name = name, Kind = "CTE" };
                ctes.Add(cte);
                aliases[name] = cte;

                i = SkipParentheses(tokens, i);
                if (i < limit && tokens[i].IsText(",")) { i++; continue; }
                break;
            }
        }

        private static void ParseTempTables(
            List<SqlToken> tokens,
            Dictionary<string, TableMetadata> aliases,
            List<TableMetadata> temps)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                bool isCreate = t.Is("CREATE");
                bool isDeclare = t.Is("DECLARE");

                if (!isCreate && !isDeclare) continue;
                if (i + 2 >= tokens.Count) continue;

                int nameIndex;
                if (isCreate)
                {
                    if (!tokens[i + 1].Is("TABLE")) continue;
                    nameIndex = i + 2;
                }
                else
                {
                    if (!IsNameToken(tokens[i + 1])) continue;
                    if (!tokens[i + 1].Text.StartsWith("@", StringComparison.Ordinal)) continue;
                    if (!tokens[i + 2].Is("TABLE")) continue;
                    nameIndex = i + 1;
                }

                if (!IsNameToken(tokens[nameIndex])) continue;
                string rawName = tokens[nameIndex].Text;
                if (!rawName.StartsWith("#", StringComparison.Ordinal) &&
                    !rawName.StartsWith("@", StringComparison.Ordinal)) continue;

                int open = nameIndex + 1;
                if (open >= tokens.Count || !tokens[open].IsText("(")) continue;

                var table = new TableMetadata { Name = CleanIdentifier(rawName), Kind = "TEMP" };
                ParseTempColumns(tokens, open, table);

                temps.Add(table);
                aliases[table.Name] = table;
            }
        }

        private static void MergeScriptTemps(
            string sql,
            Dictionary<string, TableMetadata> aliases,
            List<TableMetadata> temps)
        {
            foreach (var temp in ScriptTempScanner.Scan(sql))
            {
                bool exists = false;
                for (int i = 0; i < temps.Count; i++)
                {
                    if (string.Equals(temps[i].Name, temp.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }

                if (exists) continue;
                temps.Add(temp);
                aliases[temp.Name] = temp;
            }
        }

        private static void ParseTempColumns(List<SqlToken> tokens, int openIndex, TableMetadata table)
        {
            int depth = 0;
            string column = null;
            string dataType = null;

            for (int i = openIndex; i < tokens.Count; i++)
            {
                var t = tokens[i];

                if (t.IsText("(")) { depth++; continue; }
                if (t.IsText(")"))
                {
                    depth--;
                    if (depth == 0) break;
                    continue;
                }
                if (depth != 1) continue;

                if (t.IsText(","))
                {
                    if (column != null)
                        table.Columns.Add(new ColumnMetadata { Name = column, DataType = dataType });
                    column = null;
                    dataType = null;
                    continue;
                }

                if (column == null)
                {
                    if (IsNameToken(t) && !IsConstraintKeyword(t))
                        column = CleanIdentifier(t.Text);
                }
                else if (dataType == null)
                {
                    if (IsNameToken(t)) dataType = CleanIdentifier(t.Text);
                }
            }

            if (column != null)
                table.Columns.Add(new ColumnMetadata { Name = column, DataType = dataType });
        }

        private static bool IsConstraintKeyword(SqlToken t)
            => t.IsAny("PRIMARY", "FOREIGN", "CONSTRAINT", "UNIQUE", "CHECK", "INDEX",
                       "DEFAULT", "NOT", "NULL", "IDENTITY");

        // ── 目标表（UPDATE/INSERT/MERGE） ──

        private static TableMetadata ParseTarget(
            List<SqlToken> tokens,
            int start,
            MetadataSnapshot metadata,
            Dictionary<string, TableMetadata> aliases)
        {
            int i = start;
            if (i < tokens.Count && tokens[i].Is("INTO")) i++;

            string schema, name;
            if (!ParseQualifiedName(tokens, ref i, out schema, out name)) return null;

            var table = FindTable(metadata, aliases, schema, name);
            AddAlias(aliases, table, null);
            return table;
        }

        private static int FindKeyword(List<SqlToken> tokens, int start, string keyword)
        {
            for (int i = start; i < tokens.Count; i++)
            {
                if (tokens[i].Is(keyword)) return i;
            }
            return -1;
        }

        // ── 上下文判定 ──

        private static bool IsTypeContext(
            List<SqlToken> tokens,
            int[] depth,
            int stmtStart,
            int prevIndex)
        {
            var previous = tokens[prevIndex];

            if (previous.Is("AS"))
            {
                for (int i = prevIndex - 1; i >= stmtStart && i >= prevIndex - 12; i--)
                {
                    // CAST/CONVERT 在 ScriptDom 里可能是 Identifier，不能只看 KeywordUpper
                    if (IsWord(tokens[i], "CAST", "TRY_CAST", "CONVERT", "TRY_CONVERT"))
                        return true;
                }
            }

            if (tokens[stmtStart].Is("DECLARE") &&
                previous.Kind == SqlTokenKind.Literal &&
                previous.Text.StartsWith("@", StringComparison.Ordinal))
                return true;

            if ((tokens[stmtStart].Is("CREATE") || tokens[stmtStart].Is("ALTER")) &&
                stmtStart + 1 < tokens.Count && tokens[stmtStart + 1].Is("TABLE") &&
                depth[prevIndex] > 0)
                return true;

            if (previous.IsText("(") &&
                tokens[stmtStart].Is("CREATE") &&
                stmtStart + 1 < tokens.Count && tokens[stmtStart + 1].Is("TABLE"))
                return true;

            return false;
        }

        private static Clause NearestClause(
            List<SqlToken> tokens,
            int[] depth,
            int stmtStart,
            int beforeIndex)
        {
            for (int i = beforeIndex; i >= stmtStart && i >= 0; i--)
            {
                if (depth[i] != 0) continue;
                var t = tokens[i];

                if (t.Is("FROM")) return Clause.From;
                if (IsJoinKeyword(t) || t.Is("JOIN")) return Clause.Join;
                if (t.Is("WHERE")) return Clause.Where;
                if (t.Is("ON")) return Clause.On;
                if (t.Is("GROUP")) return Clause.Group;
                if (t.Is("ORDER")) return Clause.Order;
                if (t.Is("HAVING")) return Clause.Having;
                if (t.Is("SET")) return Clause.Set;
                if (t.Is("SELECT")) return Clause.Select;
                if (t.IsAny("EXEC", "EXECUTE")) return Clause.Exec;
                if (t.Is("INSERT")) return Clause.Insert;
                if (t.Is("UPDATE")) return Clause.Update;
                if (t.Is("DELETE")) return Clause.Delete;
                if (t.Is("MERGE")) return Clause.Merge;
            }
            return Clause.None;
        }

        private static bool IsTablePositionKeyword(SqlToken t)
        {
            return t.IsAny("FROM", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS",
                           "OUTER", "APPLY", "INTO", "UPDATE", "USING");
        }

        private static bool IsJoinKeyword(SqlToken t)
        {
            return t.IsAny("INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "APPLY");
        }

        private static bool HasSchema(MetadataSnapshot metadata, string schema)
        {
            if (metadata == null || string.IsNullOrEmpty(schema)) return false;

            for (int i = 0; i < metadata.Objects.Count; i++)
            {
                if (string.Equals(metadata.Objects[i].Schema, schema, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ── token 基础 ──

        private static int FindPrefixStart(string sql, int caret)
        {
            int i = caret;
            while (i > 0)
            {
                char c = sql[i - 1];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$') i--;
                else break;
            }
            return i;
        }

        private static int[] BuildLineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') starts.Add(i + 1);
            }
            return starts.ToArray();
        }

        private static int TokenStart(SqlToken token, int[] lineStarts)
        {
            int line = token.Line - 1;
            if (line < 0) line = 0;
            if (line >= lineStarts.Length) line = lineStarts.Length - 1;
            return lineStarts[line] + Math.Max(0, token.Column - 1);
        }

        private static int FindPreviousToken(List<SqlToken> tokens, int[] lineStarts, int prefixStart)
        {
            int index = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                int start = TokenStart(tokens[i], lineStarts);
                int end = start + tokens[i].Text.Length;
                if (end <= prefixStart) index = i;
                else break;
            }
            return index;
        }

        private static int FindStatementStart(List<SqlToken> tokens, int previousIndex)
        {
            for (int i = previousIndex; i >= 0; i--)
            {
                if (tokens[i].Kind == SqlTokenKind.Semicolon) return i + 1;
            }
            return 0;
        }

        private static int[] BuildDepth(List<SqlToken> tokens)
        {
            var depth = new int[tokens.Count];
            int current = 0;

            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].IsText(")")) current = Math.Max(0, current - 1);
                depth[i] = current;
                if (tokens[i].IsText("(")) current++;
            }

            return depth;
        }

        private static bool IsWord(SqlToken t, params string[] words)
        {
            for (int i = 0; i < words.Length; i++)
            {
                if (string.Equals(t.Text, words[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsNameToken(SqlToken t)
        {
            return t.Kind == SqlTokenKind.Identifier || t.Kind == SqlTokenKind.Literal;
        }

        private static bool IsAliasToken(SqlToken t)
        {
            return t.Kind == SqlTokenKind.Identifier || t.Kind == SqlTokenKind.Literal;
        }

        private static string CleanIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            if (text.Length >= 2 && text[0] == '[' && text[text.Length - 1] == ']')
                return text.Substring(1, text.Length - 2).Replace("]]", "]");

            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                return text.Substring(1, text.Length - 2).Replace("\"\"", "\"");

            return text;
        }
    }
}
