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
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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

        // TODO: get rid of the disposables when closing the document
        private IDisposable findTextChangedSubscription;

        private FontFamily font = null;
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
            SqlText = info.ObjectDefinition;
            _originalSqlText = info.ObjectDefinition;
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
            }
            return base.OnDeactivateAsync(close, cancellationToken);
        }

        protected override void OnViewLoaded(object viewObject)
        {
            base.OnViewLoaded(viewObject);
            var view = viewObject as ScriptedObjectDocumentView;
            if (view == null) return;
            editor = view.editor;
            EditorLoaded();
            editor.FontFamily = font ?? new FontFamily("Consolas");
            findTextBox = view.findTextBox;
            findTextChangedSubscription = Observable
                .FromEventPattern<TextChangedEventArgs>(findTextBox, "TextChanged")
                .Select(c => ((TextBox)c.Sender).Text)
                .DistinctUntilChanged()
                .Throttle(TimeSpan.FromMilliseconds(200))
                .ObserveOn(System.Reactive.Concurrency.Scheduler.CurrentThread)
                .Subscribe(c => Dispatcher.CurrentDispatcher.BeginInvoke(new System.Action(() => TextSearch(c))));

            Observable.FromEventPattern<PropertyChangedEventArgs>(this, "PropertyChanged")
                .Where(pc => pc.EventArgs.PropertyName == "FormatSql")
                .ObserveOn(System.Reactive.Concurrency.Scheduler.CurrentThread)
                .Subscribe(_ => Dispatcher.CurrentDispatcher.BeginInvoke(new System.Action(SetSqlFormat)));

            sqlTextPropChangedSub = Observable.FromEventPattern<PropertyChangedEventArgs>(this, "PropertyChanged")
                .Where(pc => pc.EventArgs.PropertyName == "SqlText")
                .ObserveOn(System.Reactive.Concurrency.Scheduler.CurrentThread)
                .Subscribe(_ => Dispatcher.CurrentDispatcher.BeginInvoke(new System.Action(() => FindText = new FindTextViewModel(SqlText))));

            eventagg?.SubscribeOnPublishedThread(this);
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

                if (errors != null && errors.Count > 0)
                    throw new Exception($"SQL Parsing Error: {errors[0].Message}");

                var tokens = fragment.ScriptTokenStream;
                if (tokens == null || tokens.Count == 0)
                    return sql;

                // Walk the token stream and apply formatting + OR ALTER injection
                var result = new StringBuilder();
                int indentLevel = 0;
                bool lineStart = true;

                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];

                    // Check if this is a CREATE keyword followed by PROCEDURE/FUNCTION/VIEW/TRIGGER
                    if (token.TokenType == TSqlTokenType.Create)
                    {
                        // Find next significant token (skip whitespace/comments) to CHECK what type
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
                            if (significantToken.TokenType == TSqlTokenType.Procedure ||
                                significantToken.TokenType == TSqlTokenType.Function ||
                                significantToken.TokenType == TSqlTokenType.View ||
                                significantToken.TokenType == TSqlTokenType.Trigger)
                            {
                                // Output CREATE OR ALTER (replacing CREATE)
                                if (lineStart)
                                {
                                    result.Append(new string(' ', indentLevel * 4));
                                    lineStart = false;
                                }
                                result.Append("CREATE OR ALTER");

                                // Now output all the tokens between CREATE and the object type
                                // (this includes any comments or whitespace that appeared there)
                                for (int j = i + 1; j < nextSignificantIndex; j++)
                                {
                                    var interveningToken = tokens[j];
                                    if (interveningToken.TokenType == TSqlTokenType.WhiteSpace)
                                    {
                                        if (interveningToken.Text.Contains("\n") || interveningToken.Text.Contains("\r"))
                                        {
                                            result.Append(Environment.NewLine);
                                            lineStart = true;
                                        }
                                        else if (!lineStart)
                                        {
                                            result.Append(" ");
                                        }
                                    }
                                    else // Comment
                                    {
                                        if (lineStart)
                                        {
                                            result.Append(new string(' ', indentLevel * 4));
                                            lineStart = false;
                                        }
                                        result.Append(interveningToken.Text);
                                    }
                                }

                                // Skip to just before the object type keyword (we'll process it normally next iteration)
                                i = nextSignificantIndex - 1;
                                continue;
                            }
                        }
                    }

                    // Apply formatting rules based on token type
                    switch (token.TokenType)
                    {
                        case TSqlTokenType.WhiteSpace:
                            // Normalize whitespace - preserve line breaks, standardize spaces
                            if (token.Text.Contains("\n") || token.Text.Contains("\r"))
                            {
                                result.Append(Environment.NewLine);
                                lineStart = true;
                            }
                            else if (!lineStart)
                            {
                                result.Append(" ");
                            }
                            break;

                        case TSqlTokenType.SingleLineComment:
                        case TSqlTokenType.MultilineComment:
                            // Preserve comments as-is
                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }
                            result.Append(token.Text);
                            break;

                        default:
                            // Apply indentation if at start of line
                            if (lineStart)
                            {
                                result.Append(new string(' ', indentLevel * 4));
                                lineStart = false;
                            }

                            // Apply keyword casing
                            if (IsKeyword(token.TokenType))
                            {
                                result.Append(token.Text.ToUpperInvariant());
                            }
                            else
                            {
                                result.Append(token.Text);
                            }

                            // Track indentation for BEGIN/END blocks
                            if (token.TokenType == TSqlTokenType.Begin)
                            {
                                indentLevel++;
                            }
                            else if (token.TokenType == TSqlTokenType.End)
                            {
                                indentLevel = Math.Max(0, indentLevel - 1);
                            }
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