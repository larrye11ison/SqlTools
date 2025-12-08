using Caliburn.Micro;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTools.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;

namespace SqlTools.Scripting
{
    /// <summary>
    /// The script for one object from the database. Shown in a tabbed UI in the app interface.
    /// </summary>
    [Export]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class ScriptedObjectDocumentViewModel : Screen, IDocument //, IHandle<FontFamily>
    {
        private SearchResultsHighlight _currentResultsHighlight;
        private SearchResultsHighlight _generalHighlight;
        private string _originalSqlText;
        private TextEditor editor;

        [Import]
        private IEventAggregator eventagg = null;

        private TextBox findTextBox;

        private IDisposable findTextChangedSubscription;
        private FontFamily font = null;
        private IDisposable formatSqlSubscription;
        private IDisposable sqlTextPropChangedSub;

        public ScriptedObjectDocumentViewModel()
        {
            IsLoadingDefinition = true;
            FindText = new FindTextViewModel("");
        }

        private enum TextSearchDirection
        { Forwards, Backwards }

        public string CanonicalName { get; set; }

        public FindTextViewModel FindText { get; set; }

        public bool FormatSql { get; set; }

        public System.Uri IconUri
        {
            get
            {
                var iconPath = "/Media/AppIcon.ico";
                switch (TypeDescription)
                {
                    case "AGGREGATE_FUNCTION":
                    case "SQL_TABLE_VALUED_FUNCTION":
                    case "SQL_SCALAR_FUNCTION":
                    case "CLR_SCALAR_FUNCTION":
                    case "SQL_INLINE_TABLE_VALUED_FUNCTION":
                        iconPath = "/Media/func-scalar.png";
                        break;

                    case "CLR_STORED_PROCEDURE":
                    case "SQL_STORED_PROCEDURE":
                    case "EXTENDED_STORED_PROCEDURE":
                        iconPath = "/Media/storedprocedure.png";
                        break;

                    case "INTERNAL_TABLE":
                    case "SYSTEM_TABLE":
                    case "USER_TABLE":
                        iconPath = "/Media/table.png";
                        break;

                    case "SQL_TRIGGER":
                        iconPath = "/Media/trigger.png";
                        break;

                    case "VIEW":
                        iconPath = "/Media/view.png";
                        break;
                }
                return new System.Uri(iconPath, System.UriKind.Relative);
            }
        }

        public bool IsLoadingDefinition { get; set; }

        public string SqlText { get; set; }

        public string Title
        { get { return DisplayName; } }

        public string TypeDescription { get; set; }

        public void EditorLoaded()
        {
            var type = typeof(ScriptedObjectDocumentViewModel);
            var fullName = type.Namespace + ".tsql.xshd";

            IHighlightingDefinition hl = null;
            try
            {
                using (var stream = type.Assembly.GetManifestResourceStream(fullName))
                {
                    if (stream != null)
                        using (var reader = new XmlTextReader(stream))
                        {
                            hl = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                        }
                }
            }
            catch
            {
                // ignore highlighting errors
            }

            if (hl != null && editor != null)
                editor.SyntaxHighlighting = hl;

            // AvalonEdit cannot bind Text directly
            if (editor != null) editor.Text = SqlText;
            FindText = new FindTextViewModel(SqlText ?? "");

            _generalHighlight = new SearchResultsHighlight("general");
            _currentResultsHighlight = new SearchResultsHighlight("current")
            {
                ForegroundBrush = Brushes.Black,
                BackgroundBrush = Brushes.Orange
            };

            if (editor != null)
            {
                editor.TextArea.TextView.LineTransformers.Add(_generalHighlight);
                editor.TextArea.TextView.LineTransformers.Add(_currentResultsHighlight);
                editor.TextArea.TextView.Redraw();
            }
        }

        public void FindNext()
        {
            if (!FindText.HasResults()) return;
            FindText.NextResult();
            _currentResultsHighlight.FindResults = new FoundTextResult[] { FindText.ActiveResult };
            if (editor != null && FindText.ActiveResult != null)
                editor.ScrollToLine(editor.Document.GetLineByOffset(FindText.ActiveResult.StartOffset).LineNumber);
            if (editor != null) editor.TextArea.TextView.Redraw();
        }

        public void FindPrevious()
        {
            if (!FindText.HasResults()) return;
            FindText.PreviousResult();
            _currentResultsHighlight.FindResults = new FoundTextResult[] { FindText.ActiveResult };
            if (editor != null && FindText.ActiveResult != null)
                editor.ScrollToLine(editor.Document.GetLineByOffset(FindText.ActiveResult.StartOffset).LineNumber);
            if (editor != null) editor.TextArea.TextView.Redraw();
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

        public void Initialize(ScriptedObjectInfo info)
        {
            // Apply CREATE OR ALTER replacement to the original script from database
            string scriptWithCreateOrAlter = ApplyCreateOrAlterReplacement(info.ObjectDefinition);

            SqlText = scriptWithCreateOrAlter;
            _originalSqlText = scriptWithCreateOrAlter;
            TypeDescription = info.DbObject.type_desc;
            DisplayName = info.DbObject.full_name;
            CanonicalName = info.DbObject.LongDescription;
            NotifyOfPropertyChange(() => Title);
        }

        public void InitiateFindText()
        {
            findTextBox?.Focus();
            findTextBox?.SelectAll();
        }

        public void SetSqlFormat()
        {
            if (editor == null || SqlText == null) return;

            string textToUse;
            if (FormatSql)
            {
                try
                {
                    textToUse = CanonicalFormatSql(_originalSqlText);
                }
                catch (Exception ex)
                {
                    // fall back and log
                    textToUse = _originalSqlText;
                    Debug.WriteLine("SQL formatting error: " + ex.Message);
                }
            }
            else
            {
                textToUse = _originalSqlText;
            }

            SqlText = textToUse;
            if (editor != null) editor.Text = SqlText;
        }

        public void TextSearch(string c)
        {
            Debug.WriteLine("Searching for: " + c);

            FindText.SearchText = c;

            _generalHighlight.FindResults = FindText.Results;
            _currentResultsHighlight.FindResults = new FoundTextResult[] { FindText.ActiveResult };

            if (FindText.ActiveResult != null && editor != null)
                editor.ScrollToLine(editor.Document.GetLineByOffset(FindText.ActiveResult.StartOffset).LineNumber);

            if (editor != null) editor.TextArea.TextView.Redraw();
        }

        public void ToggleSqlFormat()
        {
            var searchy = FindText.SearchText;
            TextSearch("");
            FormatSql = !FormatSql;
            TextSearch(searchy);
        }

        public override string ToString()
        {
            return CanonicalName;
        }

        internal void SetFontFamily(FontFamily message)
        {
            font = message;
            if (this.editor != null) editor.FontFamily = message;
        }

        protected override Task OnDeactivateAsync(bool close, CancellationToken cancellationToken)
        {
            if (close)
            {
                findTextChangedSubscription?.Dispose();
                sqlTextPropChangedSub?.Dispose();
                formatSqlSubscription?.Dispose();
                eventagg?.Unsubscribe(this);
            }
            return base.OnDeactivateAsync(close, cancellationToken);
        }

        protected override void OnViewLoaded(object viewObject)
        {
            base.OnViewLoaded(viewObject);
            var view = viewObject as ScriptedObjectDocumentView;
            if (view is null) return;
            editor = view.editor;
            EditorLoaded();
            editor.FontFamily = font ?? new FontFamily("Consolas");
            findTextBox = view.findTextBox;

            findTextChangedSubscription = Observable
                .FromEventPattern<TextChangedEventArgs>(findTextBox, nameof(findTextBox.TextChanged))
                .Select(c => ((TextBox)c.Sender).Text)
                .DistinctUntilChanged()
                .Throttle(TimeSpan.FromMilliseconds(200))
                .Subscribe(c => Execute.OnUIThread(() => TextSearch(c)));

            formatSqlSubscription = Observable.FromEventPattern<PropertyChangedEventArgs>(this, nameof(PropertyChanged))
                .Where(pc => pc.EventArgs.PropertyName == nameof(FormatSql))
                .Subscribe(_ => Execute.OnUIThread(SetSqlFormat));
            sqlTextPropChangedSub = Observable.FromEventPattern<PropertyChangedEventArgs>(this, nameof(PropertyChanged))
                .Where(pc => pc.EventArgs.PropertyName == "SqlText")
                .Subscribe(_ => Execute.OnUIThread(() => FindText = new FindTextViewModel(SqlText)));

            eventagg?.SubscribeOnPublishedThread(this);
        }

        /// <summary>
        /// Applies CREATE OR ALTER replacement to SQL scripts using AST token analysis.
        /// Only replaces CREATE when it's followed by PROCEDURE, FUNCTION, VIEW, or TRIGGER.
        /// </summary>
        private string ApplyCreateOrAlterReplacement(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return sql;

            try
            {
                var parser = new TSql160Parser(false);
                IList<ParseError> errors;
                TSqlFragment fragment;

                using (var reader = new StringReader(sql))
                {
                    fragment = parser.Parse(reader, out errors);
                }

                // If there are parsing errors, return original SQL unchanged
                if (errors != null && errors.Count > 0)
                {
                    Debug.WriteLine($"SQL Parsing warning during CREATE OR ALTER replacement: {errors[0].Message}");
                    return sql;
                }

                var tokens = fragment.ScriptTokenStream;
                if (tokens == null || tokens.Count == 0)
                    return sql;

                var result = new StringBuilder();

                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];

                    // Check if this is a CREATE keyword followed by PROCEDURE/FUNCTION/VIEW/TRIGGER
                    if (token.TokenType == TSqlTokenType.Create)
                    {
                        // Find next significant token (skip whitespace/comments)
                        int nextSignificantIndex = i + 1;
                        while (nextSignificantIndex < tokens.Count &&
                               (tokens[nextSignificantIndex].TokenType == TSqlTokenType.WhiteSpace ||
                                tokens[nextSignificantIndex].TokenType == TSqlTokenType.MultilineComment ||
                                tokens[nextSignificantIndex].TokenType == TSqlTokenType.SingleLineComment))
                        {
                            nextSignificantIndex++;
                        }

                        if (nextSignificantIndex < tokens.Count)
                        {
                            var significantToken = tokens[nextSignificantIndex];

                            // Only replace CREATE with CREATE OR ALTER for these object types
                            // Note: TSqlTokenType uses Proc (abbreviated) in addition to Procedure
                            if (significantToken.TokenType == TSqlTokenType.Proc ||
                                significantToken.TokenType == TSqlTokenType.Procedure ||
                                significantToken.TokenType == TSqlTokenType.Function ||
                                significantToken.TokenType == TSqlTokenType.View ||
                                significantToken.TokenType == TSqlTokenType.Trigger)
                            {
                                // Output CREATE OR ALTER (preserving original casing style)
                                bool isUpperCase = token.Text == "CREATE";
                                result.Append(isUpperCase ? "CREATE OR ALTER" : "create or alter");

                                // Output all tokens between CREATE and the object type keyword (whitespace/comments)
                                for (int j = i + 1; j < nextSignificantIndex; j++)
                                {
                                    result.Append(tokens[j].Text);
                                }

                                // Now output the object type keyword itself (PROCEDURE/FUNCTION/VIEW/TRIGGER)
                                result.Append(tokens[nextSignificantIndex].Text);

                                // Skip past all the tokens we've already processed
                                i = nextSignificantIndex;
                                continue;
                            }
                        }
                    }

                    // For all other tokens, output as-is
                    result.Append(token.Text);
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying CREATE OR ALTER replacement: {ex.Message}");
                // On error, return original SQL unchanged
                return sql;
            }
        }

        private string CanonicalFormatSql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return sql;

            try
            {
                var parser = new TSql160Parser(false);
                IList<ParseError> errors;
                TSqlFragment fragment;

                using (var reader = new StringReader(sql))
                {
                    fragment = parser.Parse(reader, out errors);
                }

                // If there are parsing errors, inject them into the output
                if (errors != null && errors.Count > 0)
                {
                    return InjectParseErrorsIntoScript(sql, errors);
                }

                var tokens = fragment.ScriptTokenStream;
                if (tokens == null || tokens.Count == 0)
                    return sql;

                // Walk the token stream and apply formatting
                var result = new StringBuilder();
                int indentLevel = 0;
                bool lineStart = true;
                bool previousWasStatementEnd = false;
                bool inSelectColumnList = false;
                int selectStatementDepth = 0;
                int parenthesisDepth = 0;
                bool inFunctionCall = false;
                bool inInClause = false;
                int inClauseStartIndex = -1;

                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];

                    // Apply formatting rules based on token type
                    switch (token.TokenType)
                    {
                        case TSqlTokenType.As:
                            // AS should always be on its own line like GO
                            if (!lineStart)
                            {
                                result.Append(Environment.NewLine);
                                lineStart = true;
                            }

                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }

                            result.Append(token.Text.ToUpperInvariant());

                            // Force newline after AS
                            result.Append(Environment.NewLine);
                            lineStart = true;
                            break;

                        case TSqlTokenType.Begin:
                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }

                            result.Append(token.Text.ToUpperInvariant());
                            indentLevel++;
                            result.Append(Environment.NewLine);
                            lineStart = true;
                            break;

                        case TSqlTokenType.End:
                            indentLevel = Math.Max(0, indentLevel - 1);

                            if (!lineStart)
                            {
                                result.Append(Environment.NewLine);
                                lineStart = true;
                            }

                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }

                            result.Append(token.Text.ToUpperInvariant());
                            break;

                        case TSqlTokenType.Select:
                            if (!lineStart)
                            {
                                result.Append(Environment.NewLine);
                                lineStart = true;
                            }

                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }

                            result.Append(token.Text.ToUpperInvariant());
                            inSelectColumnList = true;
                            selectStatementDepth++;
                            result.Append(Environment.NewLine);
                            lineStart = true;
                            break;

                        case TSqlTokenType.From:
                        case TSqlTokenType.Where:
                        case TSqlTokenType.Order:
                        case TSqlTokenType.Group:
                        case TSqlTokenType.Having:
                        case TSqlTokenType.Union:
                            if (inSelectColumnList && selectStatementDepth > 0)
                            {
                                inSelectColumnList = false;
                                selectStatementDepth--;
                            }

                            if (!lineStart)
                            {
                                result.Append(Environment.NewLine);
                                lineStart = true;
                            }

                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }

                            result.Append(token.Text.ToUpperInvariant());
                            break;

                        case TSqlTokenType.In:
                            // Check if this is an IN clause (followed by parenthesis)
                            int nextInIdx = i + 1;
                            while (nextInIdx < tokens.Count && tokens[nextInIdx].TokenType == TSqlTokenType.WhiteSpace)
                            {
                                nextInIdx++;
                            }

                            if (nextInIdx < tokens.Count && tokens[nextInIdx].TokenType == TSqlTokenType.LeftParenthesis)
                            {
                                inInClause = true;
                                inClauseStartIndex = result.Length;
                            }

                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }
                            result.Append(token.Text.ToUpperInvariant());
                            break;

                        case TSqlTokenType.Left:
                        case TSqlTokenType.Right:
                        case TSqlTokenType.Inner:
                        case TSqlTokenType.Outer:
                        case TSqlTokenType.Cross:
                        case TSqlTokenType.Full:
                            int nextSigIdx = i + 1;
                            while (nextSigIdx < tokens.Count && tokens[nextSigIdx].TokenType == TSqlTokenType.WhiteSpace)
                            {
                                nextSigIdx++;
                            }

                            bool isJoinModifier = false;
                            if (nextSigIdx < tokens.Count)
                            {
                                var nextToken = tokens[nextSigIdx];
                                if (nextToken.TokenType == TSqlTokenType.Join ||
                                    nextToken.TokenType == TSqlTokenType.Outer ||
                                    nextToken.TokenType == TSqlTokenType.Inner)
                                {
                                    isJoinModifier = true;
                                }
                            }

                            if (isJoinModifier)
                            {
                                if (!lineStart)
                                {
                                    result.Append(Environment.NewLine);
                                    lineStart = true;
                                }

                                if (lineStart)
                                {
                                    result.Append(new string(' ', indentLevel * 4));
                                    lineStart = false;
                                }

                                result.Append(token.Text.ToUpperInvariant());
                            }
                            else
                            {
                                if (lineStart)
                                {
                                    result.Append(new string(' ', indentLevel * 4));
                                    lineStart = false;
                                }
                                result.Append(token.Text.ToUpperInvariant());
                            }
                            break;

                        case TSqlTokenType.Join:
                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }

                            result.Append(token.Text.ToUpperInvariant());
                            break;

                        case TSqlTokenType.Comma:
                            result.Append(token.Text);

                            // Determine if we should newline after comma
                            if (inSelectColumnList && selectStatementDepth > 0)
                            {
                                // In SELECT column list: newline after comma
                                result.Append(Environment.NewLine);
                                lineStart = true;
                            }
                            else if (inInClause && parenthesisDepth > 0)
                            {
                                // In IN clause: add space for now, we'll handle line breaking later
                                result.Append(" ");
                            }
                            else if (parenthesisDepth > 0 && !inFunctionCall)
                            {
                                // In parameter list (not function call): newline after comma
                                result.Append(Environment.NewLine);
                                lineStart = true;
                            }
                            else
                            {
                                // Normal comma (e.g., in function calls): just a space
                                result.Append(" ");
                            }
                            break;

                        case TSqlTokenType.LeftParenthesis:
                            parenthesisDepth++;

                            // Check if this is a function call by looking backwards
                            int prevIdx = i - 1;
                            while (prevIdx >= 0 && tokens[prevIdx].TokenType == TSqlTokenType.WhiteSpace)
                            {
                                prevIdx--;
                            }

                            bool isFunctionCall = false;
                            if (prevIdx >= 0)
                            {
                                var prevToken = tokens[prevIdx];
                                // If previous token is an identifier (not a keyword), it's likely a function call
                                if (prevToken.TokenType == TSqlTokenType.Identifier ||
                                    prevToken.TokenType == TSqlTokenType.QuotedIdentifier)
                                {
                                    isFunctionCall = true;
                                }
                                // Check for built-in functions
                                else if (IsBuiltInFunction(prevToken.TokenType))
                                {
                                    isFunctionCall = true;
                                }
                            }

                            if (isFunctionCall)
                            {
                                inFunctionCall = true;
                            }

                            if (lineStart)
                            {
                                int extraIndent = (inSelectColumnList && selectStatementDepth > 0) ? 1 : 0;
                                result.Append(new string(' ', (indentLevel + extraIndent) * 4));
                                lineStart = false;
                            }

                            result.Append(token.Text);

                            // Check if next significant token is SELECT (subquery)
                            if (!isFunctionCall)
                            {
                                int nextSelectIdx = i + 1;
                                while (nextSelectIdx < tokens.Count && tokens[nextSelectIdx].TokenType == TSqlTokenType.WhiteSpace)
                                {
                                    nextSelectIdx++;
                                }
                                if (nextSelectIdx < tokens.Count && tokens[nextSelectIdx].TokenType == TSqlTokenType.Select)
                                {
                                    selectStatementDepth++;
                                }
                            }
                            break;

                        case TSqlTokenType.RightParenthesis:
                            // Handle IN clause formatting before closing
                            if (inInClause && parenthesisDepth == 1)
                            {
                                // Check the length of the IN clause content
                                int inClauseLength = result.Length - inClauseStartIndex;

                                // If > 100 chars, reformat to multi-line
                                if (inClauseLength > 100)
                                {
                                    string inClauseContent = result.ToString(inClauseStartIndex, inClauseLength);
                                    result.Length = inClauseStartIndex; // Remove existing content
                                    FormatInClauseMultiline(result, inClauseContent, indentLevel);

                                    // Mark that we're done with IN clause and DON'T append the closing paren again
                                    inInClause = false;
                                    inClauseStartIndex = -1;
                                    parenthesisDepth = Math.Max(0, parenthesisDepth - 1);

                                    if (parenthesisDepth == 0)
                                    {
                                        inFunctionCall = false;
                                    }

                                    // Don't append the closing paren - it's already added by FormatInClauseMultiline
                                    break;
                                }

                                inInClause = false;
                                inClauseStartIndex = -1;
                            }

                            parenthesisDepth = Math.Max(0, parenthesisDepth - 1);

                            if (parenthesisDepth == 0)
                            {
                                inFunctionCall = false;
                            }

                            result.Append(token.Text);
                            break;

                        case TSqlTokenType.Semicolon:
                            result.Append(token.Text);
                            previousWasStatementEnd = true;
                            inSelectColumnList = false;
                            selectStatementDepth = 0;
                            break;

                        case TSqlTokenType.Go:
                            if (!lineStart)
                            {
                                result.Append(Environment.NewLine);
                                lineStart = true;
                            }

                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }

                            result.Append(token.Text.ToUpperInvariant());
                            result.Append(Environment.NewLine);
                            lineStart = true;

                            previousWasStatementEnd = true;
                            inSelectColumnList = false;
                            selectStatementDepth = 0;
                            break;

                        case TSqlTokenType.WhiteSpace:
                            if (token.Text.Contains("\n") || token.Text.Contains("\r"))
                            {
                                if (previousWasStatementEnd && !lineStart)
                                {
                                    result.Append(Environment.NewLine);
                                    result.Append(Environment.NewLine);
                                    lineStart = true;
                                    previousWasStatementEnd = false;
                                }
                            }
                            else if (!lineStart)
                            {
                                result.Append(" ");
                            }
                            break;

                        case TSqlTokenType.SingleLineComment:
                            if (lineStart)
                            {
                                int extraIndent = (inSelectColumnList && selectStatementDepth > 0) ? 1 : 0;
                                result.Append(new string(' ', (indentLevel + extraIndent) * 4));
                                lineStart = false;
                            }
                            result.Append(token.Text);
                            if (!token.Text.EndsWith("\n") && !token.Text.EndsWith("\r"))
                            {
                                result.Append(Environment.NewLine);
                            }
                            lineStart = true;
                            previousWasStatementEnd = false;
                            break;

                        case TSqlTokenType.MultilineComment:
                            if (lineStart)
                            {
                                int extraIndent = (inSelectColumnList && selectStatementDepth > 0) ? 1 : 0;
                                result.Append(new string(' ', (indentLevel + extraIndent) * 4));
                                lineStart = false;
                            }
                            result.Append(token.Text);
                            if (token.Text.EndsWith("\n") || token.Text.EndsWith("\r"))
                            {
                                lineStart = true;
                            }
                            previousWasStatementEnd = false;
                            break;

                        default:
                            if (lineStart)
                            {
                                int extraIndent = (inSelectColumnList && selectStatementDepth > 0) ? 1 : 0;
                                result.Append(new string(' ', (indentLevel + extraIndent) * 4));
                                lineStart = false;
                            }

                            if (IsKeyword(token.TokenType))
                            {
                                result.Append(token.Text.ToUpperInvariant());
                            }
                            else
                            {
                                result.Append(token.Text);
                            }

                            previousWasStatementEnd = false;
                            break;
                    }
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Formatting error: {ex.Message}");
                return sql;
            }
        }

        /// <summary>
        /// Formats an IN clause content into multiple lines, breaking at ~100 character boundaries.
        /// </summary>
        private void FormatInClauseMultiline(StringBuilder result, string inClauseContent, int indentLevel)
        {
            // Extract just the values between IN ( and )
            int startParen = inClauseContent.IndexOf('(');
            if (startParen < 0) return;

            string prefix = inClauseContent.Substring(0, startParen + 1).Trim();
            result.Append(prefix);
            result.Append(Environment.NewLine);

            // Get the values part
            string valuesPart = inClauseContent.Substring(startParen + 1).Trim();

            // Split by comma and rebuild with line breaks
            var values = valuesPart.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .ToList();

            var currentLine = new StringBuilder();
            string indent = new string(' ', (indentLevel + 1) * 4);

            for (int i = 0; i < values.Count; i++)
            {
                string value = values[i];

                // Check if adding this value would exceed 100 chars
                if (currentLine.Length > 0 && currentLine.Length + value.Length + 2 > 100)
                {
                    // Output current line
                    result.Append(indent);
                    result.Append(currentLine.ToString());
                    result.AppendLine();
                    currentLine.Clear();
                }

                if (currentLine.Length > 0)
                {
                    currentLine.Append(", ");
                }

                currentLine.Append(value);
            }

            // Output last line
            if (currentLine.Length > 0)
            {
                result.Append(indent);
                result.Append(currentLine.ToString());
                result.AppendLine();
            }

            // Close parenthesis
            result.Append(new string(' ', indentLevel * 4));
            result.Append(")");
        }

        /// <summary>
        /// Injects parsing error messages into the SQL script at the appropriate line positions.
        /// </summary>
        private string InjectParseErrorsIntoScript(string sql, IList<ParseError> errors)
        {
            if (string.IsNullOrWhiteSpace(sql) || errors == null || errors.Count == 0)
                return sql;

            var scriptLines = sql.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var result = new StringBuilder();

            // Group errors by line number for efficient lookup
            var errorsByLine = errors
                .GroupBy(e => e.Line)
                .ToDictionary(g => g.Key, g => g.ToList());

            for (int lineIndex = 0; lineIndex < scriptLines.Length; lineIndex++)
            {
                int lineNumber = lineIndex + 1; // Lines are 1-based
                result.AppendLine(scriptLines[lineIndex]);

                // Check if there are errors for this line
                if (errorsByLine.TryGetValue(lineNumber, out var lineErrors))
                {
                    foreach (var error in lineErrors)
                    {
                        // Add pointer to show approximate column position FIRST
                        if (error.Column > 0 && error.Column <= scriptLines[lineIndex].Length + 1)
                        {
                            result.Append("    ");
                            result.Append(new string(' ', Math.Max(0, error.Column - 1)));
                            result.AppendLine("^");
                        }

                        // Then inject error message with visual indicator
                        result.Append("    ⚠️  "); // Warning emoji with spaces
                        result.Append($"ERROR at line {error.Line}, column {error.Column}: ");
                        result.AppendLine(error.Message);
                    }
                    result.AppendLine(); // Add blank line after errors for readability
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Checks if a token type represents a built-in SQL function.
        /// </summary>
        private bool IsBuiltInFunction(TSqlTokenType tokenType)
        {
            // Only include token types that actually exist in TSqlTokenType enum
            // Most built-in functions are parsed as Identifiers, not specific token types
            switch (tokenType)
            {
                // These are the few aggregate functions that have dedicated tokens
                case TSqlTokenType.Coalesce:
                case TSqlTokenType.NullIf:
                    return true;

                default:
                    return false;
            }
        }

        private bool IsKeyword(TSqlTokenType tokenType)
        {
            // Return true for SQL keywords that should be uppercased
            switch (tokenType)
            {
                case TSqlTokenType.Select:
                case TSqlTokenType.From:
                case TSqlTokenType.Where:
                case TSqlTokenType.Insert:
                case TSqlTokenType.Update:
                case TSqlTokenType.Delete:
                case TSqlTokenType.Create:
                case TSqlTokenType.Alter:
                case TSqlTokenType.Drop:
                case TSqlTokenType.Proc:
                case TSqlTokenType.Procedure:
                case TSqlTokenType.Function:
                case TSqlTokenType.View:
                case TSqlTokenType.Trigger:
                case TSqlTokenType.Begin:
                case TSqlTokenType.End:
                case TSqlTokenType.If:
                case TSqlTokenType.Else:
                case TSqlTokenType.While:
                case TSqlTokenType.Return:
                case TSqlTokenType.Declare:
                case TSqlTokenType.Set:
                case TSqlTokenType.As:
                case TSqlTokenType.Join:
                case TSqlTokenType.Left:
                case TSqlTokenType.Right:
                case TSqlTokenType.Inner:
                case TSqlTokenType.Outer:
                case TSqlTokenType.Cross:
                case TSqlTokenType.Full:
                case TSqlTokenType.On:
                case TSqlTokenType.And:
                case TSqlTokenType.Or:
                case TSqlTokenType.Not:
                case TSqlTokenType.Null:
                case TSqlTokenType.Is:
                case TSqlTokenType.In:
                case TSqlTokenType.Between:
                case TSqlTokenType.Like:
                case TSqlTokenType.Exists:
                case TSqlTokenType.Case:
                case TSqlTokenType.When:
                case TSqlTokenType.Then:
                case TSqlTokenType.Order:
                case TSqlTokenType.By:
                case TSqlTokenType.Group:
                case TSqlTokenType.Having:
                case TSqlTokenType.Distinct:
                case TSqlTokenType.Top:
                case TSqlTokenType.With:
                case TSqlTokenType.Union:
                case TSqlTokenType.All:
                case TSqlTokenType.Into:
                case TSqlTokenType.Values:
                case TSqlTokenType.Table:
                case TSqlTokenType.Execute:
                case TSqlTokenType.Exec:
                    return true;

                default:
                    return false;
            }
        }
    }
}