using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SqlPhanos.CodeFormatting;

/// <summary>
/// Finds statically named SQL object references from ScriptDom semantic nodes. It does not
/// inspect or rewrite raw SQL, so comments, string contents, and dynamic SQL are never treated
/// as references.
/// </summary>
public sealed class SqlObjectReferenceAnalyzer
{
	public SqlObjectReferenceAnalysisResult Analyze(string? sql)
	{
		if (string.IsNullOrEmpty(sql))
		{
			return new SqlObjectReferenceAnalysisResult(
				new List<SqlObjectReference>(),
				new List<SqlObjectReferenceParseError>());
		}

		try
		{
			var parser = new TSql170Parser(initialQuotedIdentifiers: true);
			TSqlFragment fragment;
			IList<ParseError> errors;

			using (var reader = new StringReader(sql))
			{
				fragment = parser.Parse(reader, out errors);
			}

			var collector = new ReferenceCollector(sql);
			fragment.Accept(collector);
			return new SqlObjectReferenceAnalysisResult(
				collector.CreateReferences(),
				errors.Select(ToParseError).ToList());
		}
		catch (Exception exception)
		{
			return new SqlObjectReferenceAnalysisResult(
				new List<SqlObjectReference>(),
				new List<SqlObjectReferenceParseError>
				{
					new(0, 0, 0, 0, $"SQL parsing failed: {exception.Message}"),
				});
		}
	}

	private static SqlObjectReferenceParseError ToParseError(ParseError error)
		=> new(error.Number, error.Offset, error.Line, error.Column, error.Message);

	private sealed class ReferenceCollector : TSqlFragmentVisitor
	{
		private static readonly HashSet<string> BuiltInClrTypeNames =
			new(StringComparer.OrdinalIgnoreCase)
			{
				"geography",
				"geometry",
				"hierarchyid",
			};

		private readonly string _sql;
		private readonly List<Candidate> _candidates = new();
		private readonly List<AliasDeclaration> _aliases = new();
		private readonly List<ReferenceScope> _referenceScopes = new();
		private readonly List<CteScope> _cteScopes = new();
		private readonly List<ReferenceScope> _triggerScopes = new();

		public ReferenceCollector(string sql) => _sql = sql;

		public override void Visit(TSqlStatement node)
		{
			AddReferenceScope(node);

			if (node is StatementWithCtesAndXmlNamespaces statement &&
				statement.WithCtesAndXmlNamespaces is { } withClause)
			{
				var names = withClause.CommonTableExpressions
					.Select(cte => cte.ExpressionName.Value)
					.ToArray();

				if (names.Length > 0)
				{
					_cteScopes.Add(new CteScope(node.StartOffset, EndOffset(node), names));
				}
			}
		}

		public override void Visit(QuerySpecification node)
			=> AddReferenceScope(node);

		public override void Visit(DataModificationSpecification node)
			=> AddReferenceScope(node);

		public override void Visit(MergeSpecification node)
			=> AddReferenceScope(node);

		public override void Visit(TableReferenceWithAlias node)
			=> AddAlias(node.Alias);

		public override void Visit(SecurityStatement node)
			=> AddSecurityTarget(node.SecurityTargetObject);

		public override void Visit(AlterTableStatement node)
			=> AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.SchemaObjectName);

		public override void Visit(IndexStatement node)
			=> AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.OnName);

		public override void Visit(DropObjectsStatement node)
		{
			var kind = node switch
			{
				DropTableStatement => SqlObjectReferenceKind.TableOrView,
				DropViewStatement => SqlObjectReferenceKind.TableOrView,
				DropProcedureStatement => SqlObjectReferenceKind.Procedure,
				DropFunctionStatement => SqlObjectReferenceKind.Function,
				DropSequenceStatement => SqlObjectReferenceKind.Sequence,
				DropExternalTableStatement => SqlObjectReferenceKind.TableOrView,
				DropTriggerStatement trigger when trigger.TriggerScope == TriggerScope.Normal =>
					SqlObjectReferenceKind.Trigger,
				_ => (SqlObjectReferenceKind?)null,
			};

			if (kind is { } referenceKind)
			{
				AddSchemaObjects(referenceKind, node.Objects);
			}
		}

		public override void Visit(SignatureStatementBase node)
			=> AddSchemaObject(SqlObjectReferenceKind.Any, node.Element);

		public override void Visit(BulkInsertBase node)
			=> AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.To);

		public override void Visit(TextModificationStatement node)
			=> AddColumnOwner(node.Column);

		public override void ExplicitVisit(NamedTableReference node)
		{
			AddAlias(node.Alias);
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.SchemaObject, node.Alias is not null);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
		{
			AddAlias(node.Alias);
			AddSchemaObject(SqlObjectReferenceKind.Function, node.SchemaObject, node.Alias is not null);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(FullTextTableReference node)
		{
			AddAlias(node.Alias);
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.TableName, node.Alias is not null);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(ChangeTableChangesTableReference node)
		{
			AddAlias(node.Alias);
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.Target, node.Alias is not null);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(ChangeTableVersionTableReference node)
		{
			AddAlias(node.Alias);
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.Target, node.Alias is not null);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(SelectStatement node)
		{
			if (node.Into is not null)
			{
				AddSchemaObject(
					SqlObjectReferenceKind.TableOrView,
					node.Into,
					isActionTarget: true);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(InsertSpecification node)
		{
			AddActionTarget(node.Target);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(OutputIntoClause node)
		{
			AddActionTarget(node.IntoTable);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(TruncateTableStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.TableName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(UpdateStatisticsStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.SchemaObjectName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AlterTableSwitchStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.TargetTable);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CreateColumnStoreIndexStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.OnName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CreateSpatialIndexStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.Object);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CreateStatisticsStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.OnName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CreateFullTextIndexStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.OnName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AlterFullTextIndexStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.OnName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(DropFullTextIndexStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.TableName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(DropIndexClause node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.Object);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(BackwardsCompatibleDropIndexClause node)
		{
			AddIdentifiers(
				SqlObjectReferenceKind.TableOrView,
				node.Index.Identifiers.Take(node.Index.Identifiers.Count - 1).ToArray());
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(TableHintsOptimizerHint node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.ObjectName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(SetIdentityInsertStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.Table);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CreateTableStatement node)
		{
			if (node.CloneSource is not null)
			{
				AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.CloneSource);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(SystemVersioningTableOption node)
		{
			if (node.HistoryTable is not null)
			{
				AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.HistoryTable);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(GraphConnectionBetweenNodes node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.FromNode);
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.ToNode);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(SecurityPredicateAction node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.TargetObjectName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CreateSecurityPolicyStatement node)
		{
			AddSecurityPolicyActions(node);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AlterSecurityPolicyStatement node)
		{
			AddSecurityPolicyActions(node);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CopyStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.Into);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(OpenXmlTableReference node)
		{
			AddAlias(node.Alias);
			if (node.TableName is not null)
			{
				AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.TableName, node.Alias is not null);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(SemanticTableReference node)
		{
			AddAlias(node.Alias);
			AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.TableName, node.Alias is not null);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(OpenRowsetTableReference node)
		{
			AddAlias(node.Alias);
			if (node.Object is not null)
			{
				AddSchemaObject(
					SqlObjectReferenceKind.TableOrView,
					node.Object,
					declaresOwnAlias: node.Alias is not null,
					isRemoteDataSource: true);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AdHocTableReference node)
		{
			AddAlias(node.Alias);
			if (node.Object.SchemaObjectName is { } objectName)
			{
				AddSchemaObject(
					SqlObjectReferenceKind.TableOrView,
					objectName,
					declaresOwnAlias: node.Alias is not null,
					isRemoteDataSource: true);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(SchemaObjectResultSetDefinition node)
		{
			AddSchemaObject(
				node.ResultSetType == ResultSetType.Type
					? SqlObjectReferenceKind.Type
					: SqlObjectReferenceKind.Rowset,
				node.Name);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AlterAuthorizationStatement node)
		{
			AddSecurityTarget(node.SecurityTargetObject);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AuditActionSpecification node)
		{
			AddSecurityTarget(node.TargetObject);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(QueueProcedureOption node)
		{
			AddSchemaObject(SqlObjectReferenceKind.Procedure, node.OptionValue);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(DropStatisticsStatement node)
		{
			foreach (var childObject in node.Objects)
			{
				AddIdentifiers(
					SqlObjectReferenceKind.TableOrView,
					childObject.Identifiers.Take(childObject.Identifiers.Count - 1).ToArray());
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AlterSchemaStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.Any, node.ObjectName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(RenameEntityStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.Any, node.OldName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(PrivilegeSecurityElement80 node)
		{
			AddSchemaObject(SqlObjectReferenceKind.Any, node.SchemaObjectName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CreateSynonymStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.Any, node.ForName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AlterResourceGovernorStatement node)
		{
			if (node.ClassifierFunction is not null)
			{
				AddSchemaObject(SqlObjectReferenceKind.Function, node.ClassifierFunction);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AlterSequenceStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.Sequence, node.Name);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(DropTypeStatement node)
		{
			AddSchemaObject(SqlObjectReferenceKind.Type, node.Name);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(EnableDisableTriggerStatement node)
		{
			if (node.TriggerObject?.TriggerScope != TriggerScope.Normal)
			{
				base.ExplicitVisit(node);
				return;
			}

			var targetDatabase = node.TriggerObject?.Name?.DatabaseIdentifier?.Value;
			var targetSchema = node.TriggerObject?.Name?.SchemaIdentifier?.Value;
			foreach (var triggerName in node.TriggerNames)
			{
				if (triggerName.Identifiers.Count == 1 &&
					triggerName.SchemaIdentifier is null &&
					targetSchema is not null)
				{
					AddSingleIdentifier(
						SqlObjectReferenceKind.Trigger,
						triggerName.Identifiers[0],
						targetDatabase,
						targetSchema);
				}
				else
				{
					AddSchemaObject(SqlObjectReferenceKind.Trigger, triggerName);
				}
			}

			if (node.TriggerObject?.Name is { } target)
			{
				AddSchemaObject(SqlObjectReferenceKind.TableOrView, target);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AlterTableTriggerModificationStatement node)
		{
			var targetDatabase = node.SchemaObjectName.DatabaseIdentifier?.Value;
			var targetSchema = node.SchemaObjectName.SchemaIdentifier?.Value;
			foreach (var triggerName in node.TriggerNames)
			{
				AddSingleIdentifier(
					SqlObjectReferenceKind.Trigger,
					triggerName,
					targetDatabase,
					targetSchema);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(ReadTextStatement node)
		{
			AddColumnOwner(node.Column);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(UpdateTextStatement node)
		{
			if (node.SourceColumn is not null)
			{
				AddColumnOwner(node.SourceColumn);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(DbccStatement node)
		{
			var isCheckConstraints =
				node.Command == DbccCommand.CheckConstraints ||
				(node.Command == DbccCommand.Free &&
				 string.Equals(node.DllName, "CHECKCONSTRAINTS", StringComparison.OrdinalIgnoreCase));
			var literalIndex = isCheckConstraints
				? 0
				: node.Command switch
			{
				DbccCommand.CheckTable or
				DbccCommand.CheckIdent or
				DbccCommand.DBReindex or
				DbccCommand.ShowContig or
				DbccCommand.ShowStatistics => 0,
				DbccCommand.CleanTable or
				DbccCommand.IndexDefrag or
				DbccCommand.UpdateUsage => 1,
				_ => -1,
			};

			if (literalIndex >= 0 &&
				literalIndex < node.Literals.Count &&
				node.Literals[literalIndex].Value is Literal literal)
			{
				AddObjectNameLiteral(
					literal,
					isCheckConstraints);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(ExecutableProcedureReference node)
		{
			var name = node.ProcedureReference?.ProcedureReference?.Name;
			if (name is not null)
			{
				AddSchemaObject(SqlObjectReferenceKind.Executable, name);

				if (string.Equals(name.SchemaIdentifier?.Value, "sys", StringComparison.OrdinalIgnoreCase) &&
					node.Parameters.Count > 0 &&
					node.Parameters[0].ParameterValue is Literal literal)
				{
					var argumentKind = name.BaseIdentifier?.Value.ToUpperInvariant() switch
					{
						"SP_REFRESHVIEW" => SqlObjectReferenceKind.TableOrView,
						"SP_REFRESHSQLMODULE" or "SP_RECOMPILE" or "SP_RENAME" =>
							SqlObjectReferenceKind.SchemaObject,
						_ => (SqlObjectReferenceKind?)null,
					};
					if (argumentKind is { } referenceKind)
					{
						AddObjectNameLiteral(literal, requireQualifiedName: false, referenceKind);
					}
				}
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(FunctionCall node)
		{
			AddFunctionCall(node);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(UserDefinedTypePropertyAccess node)
		{
			if (node.CallTarget is UserDefinedTypeCallTarget target)
			{
				AddSchemaObject(SqlObjectReferenceKind.Type, target.SchemaObjectName);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(NextValueForExpression node)
		{
			AddSchemaObject(SqlObjectReferenceKind.Sequence, node.SequenceName);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(ForeignKeyConstraintDefinition node)
		{
			if (node.ReferenceTableName is not null)
			{
				AddSchemaObject(SqlObjectReferenceKind.TableOrView, node.ReferenceTableName);
			}

			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CreateTriggerStatement node)
		{
			AddTriggerTarget(node);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(AlterTriggerStatement node)
		{
			AddTriggerTarget(node);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(CreateOrAlterTriggerStatement node)
		{
			AddTriggerTarget(node);
			base.ExplicitVisit(node);
		}

		public override void ExplicitVisit(UserDataTypeReference node)
		{
			if (node.Name is not null)
			{
				AddSchemaObject(SqlObjectReferenceKind.Type, node.Name);
			}

			base.ExplicitVisit(node);
		}

		public List<SqlObjectReference> CreateReferences()
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var references = new List<SqlObjectReference>();

			foreach (var candidate in _candidates.OrderBy(candidate => candidate.Offset).ThenBy(candidate => candidate.Length))
			{
				if (ShouldExclude(candidate))
				{
					continue;
				}

				var key = $"{candidate.Offset}:{candidate.Length}:{(int)candidate.Kind}";
				if (!seen.Add(key))
				{
					continue;
				}

				references.Add(new SqlObjectReference(
					candidate.Kind,
					candidate.Offset,
					candidate.Length,
					_sql.Substring(candidate.Offset, candidate.Length),
					candidate.Server,
					candidate.Database,
					candidate.Schema,
					candidate.Object,
					candidate.PartCount,
					candidate.IsRemoteDataSource));
			}

			return references;
		}

		private void AddSchemaObject(
			SqlObjectReferenceKind kind,
			SchemaObjectName name,
			bool declaresOwnAlias = false,
			bool isActionTarget = false,
			bool isRemoteDataSource = false)
			=> AddIdentifiers(
				kind,
				name.Identifiers,
				declaresOwnAlias,
				isActionTarget,
				isRemoteDataSource);

		private void AddSchemaObjects(
			SqlObjectReferenceKind kind,
			IEnumerable<SchemaObjectName> names)
		{
			foreach (var name in names)
			{
				AddSchemaObject(kind, name);
			}
		}

		private void AddActionTarget(TableReference? target)
		{
			if (target is NamedTableReference namedTable)
			{
				AddSchemaObject(
					SqlObjectReferenceKind.TableOrView,
					namedTable.SchemaObject,
					isActionTarget: true);
			}
		}

		private void AddSecurityTarget(SecurityTargetObject? target)
		{
			if (target?.ObjectName?.MultiPartIdentifier is not { } identifier)
			{
				return;
			}

			var kind = target.ObjectKind switch
			{
				SecurityObjectKind.Type => SqlObjectReferenceKind.Type,
				SecurityObjectKind.Object => SqlObjectReferenceKind.SchemaObject,
				SecurityObjectKind.NotSpecified => SqlObjectReferenceKind.Any,
				_ => (SqlObjectReferenceKind?)null,
			};

			if (kind is { } referenceKind)
			{
				AddIdentifiers(referenceKind, identifier.Identifiers);
			}
		}

		private void AddSecurityPolicyActions(SecurityPolicyStatement statement)
		{
			foreach (var action in statement.SecurityPredicateActions)
			{
				AddSchemaObject(SqlObjectReferenceKind.TableOrView, action.TargetObjectName);
				AddFunctionCall(action.FunctionCall);
			}
		}

		private void AddFunctionCall(FunctionCall node)
		{
			var functionName = node.FunctionName;
			if (functionName is null)
			{
				return;
			}

			if (node.CallTarget is MultiPartIdentifierCallTarget multipartTarget)
			{
				var identifiers = multipartTarget.MultiPartIdentifier.Identifiers
					.Concat(new[] { functionName })
					.ToArray();
				AddIdentifiers(SqlObjectReferenceKind.Function, identifiers);
			}
			else if (node.CallTarget is UserDefinedTypeCallTarget userDefinedTypeTarget)
			{
				AddSchemaObject(SqlObjectReferenceKind.Type, userDefinedTypeTarget.SchemaObjectName);
			}
			else if (node.Parameters.Count > 0 &&
				node.Parameters[0] is Literal literal)
			{
				SqlObjectReferenceKind? literalKind;
				if (functionName.Value.Equals("HAS_PERMS_BY_NAME", StringComparison.OrdinalIgnoreCase) &&
					node.Parameters.Count > 1 &&
					node.Parameters[1] is Literal securableClass)
				{
					literalKind = securableClass.Value.ToUpperInvariant() switch
					{
						"OBJECT" => SqlObjectReferenceKind.SchemaObject,
						"TYPE" => SqlObjectReferenceKind.Type,
						_ => null,
					};
				}
				else
				{
					literalKind = functionName.Value.ToUpperInvariant() switch
					{
						"IDENT_CURRENT" or "IDENT_INCR" or "IDENT_SEED" or
							"COL_LENGTH" or "INDEX_COL" => SqlObjectReferenceKind.TableOrView,
						"OBJECT_ID" => SqlObjectReferenceKind.SchemaObject,
						"TYPE_ID" or "TYPEPROPERTY" => SqlObjectReferenceKind.Type,
						_ => null,
					};
				}

				if (literalKind is { } referenceKind)
				{
					AddObjectNameLiteral(literal, requireQualifiedName: false, referenceKind);
				}
			}
		}

		private void AddColumnOwner(ColumnReferenceExpression? column)
		{
			var identifiers = column?.MultiPartIdentifier?.Identifiers;
			if (identifiers is null || identifiers.Count < 2)
			{
				return;
			}

			AddIdentifiers(
				SqlObjectReferenceKind.TableOrView,
				identifiers.Take(identifiers.Count - 1).ToArray());
		}

		private void AddSingleIdentifier(
			SqlObjectReferenceKind kind,
			Identifier identifier,
			string? database,
			string? schema)
		{
			var tokens = identifier.ScriptTokenStream;
			if (tokens is null ||
				identifier.FirstTokenIndex < 0 ||
				identifier.LastTokenIndex >= tokens.Count)
			{
				return;
			}

			var firstToken = tokens[identifier.FirstTokenIndex];
			var lastToken = tokens[identifier.LastTokenIndex];
			var length = lastToken.Offset + lastToken.Text.Length - firstToken.Offset;
			if (length <= 0 || firstToken.Offset < 0 || firstToken.Offset + length > _sql.Length)
			{
				return;
			}

			_candidates.Add(new Candidate(
				kind,
				firstToken.Offset,
				length,
				null,
				database,
				schema,
				identifier.Value,
				1,
				false,
				false,
				false));
		}

		private void AddObjectNameLiteral(
			Literal literal,
			bool requireQualifiedName,
			SqlObjectReferenceKind kind = SqlObjectReferenceKind.TableOrView)
		{
			if (!TryParseObjectName(literal.Value, out var values) ||
				(requireQualifiedName && values.Length < 2) ||
				values[values.Length - 1].StartsWith("#", StringComparison.Ordinal))
			{
				return;
			}

			var tokens = literal.ScriptTokenStream;
			if (tokens is null ||
				literal.FirstTokenIndex < 0 ||
				literal.LastTokenIndex >= tokens.Count)
			{
				return;
			}

			var firstToken = tokens[literal.FirstTokenIndex];
			var lastToken = tokens[literal.LastTokenIndex];
			var length = lastToken.Offset + lastToken.Text.Length - firstToken.Offset;
			if (length <= 0 || firstToken.Offset < 0 || firstToken.Offset + length > _sql.Length)
			{
				return;
			}

			_candidates.Add(new Candidate(
				kind,
				firstToken.Offset,
				length,
				values.Length == 4 ? values[0] : null,
				values.Length >= 3 ? values[values.Length - 3] : null,
				values.Length >= 2 ? values[values.Length - 2] : null,
				values[values.Length - 1],
				values.Length,
				false,
				false,
				false));
		}

		private static bool TryParseObjectName(string value, out string[] values)
		{
			var parser = new TSql170Parser(initialQuotedIdentifiers: true);
			using var reader = new StringReader($"SELECT 1 FROM {value};");
			var fragment = parser.Parse(reader, out var errors);
			if (errors.Count > 0)
			{
				values = Array.Empty<string>();
				return false;
			}

			var visitor = new FirstNamedTableVisitor();
			fragment.Accept(visitor);
			values = visitor.Name?.Identifiers.Select(identifier => identifier.Value).ToArray()
				?? Array.Empty<string>();
			return values.Length is >= 1 and <= 4;
		}

		private void AddIdentifiers(
			SqlObjectReferenceKind kind,
			IList<Identifier> identifiers,
			bool declaresOwnAlias = false,
			bool isActionTarget = false,
			bool isRemoteDataSource = false)
		{
			if (identifiers.Count is < 1 or > 4)
			{
				return;
			}

			var first = identifiers[0];
			var last = identifiers[identifiers.Count - 1];
			var tokens = first.ScriptTokenStream;

			if (tokens is null ||
				first.FirstTokenIndex < 0 ||
				last.LastTokenIndex < first.FirstTokenIndex ||
				last.LastTokenIndex >= tokens.Count)
			{
				return;
			}

			var firstToken = tokens[first.FirstTokenIndex];
			var lastToken = tokens[last.LastTokenIndex];
			var offset = firstToken.Offset;
			var length = lastToken.Offset + lastToken.Text.Length - offset;

			if (offset < 0 || length <= 0 || offset + length > _sql.Length)
			{
				return;
			}

			var values = identifiers.Select(identifier => identifier.Value).ToArray();
			// ScriptDom retains an omitted multipart component as an empty Identifier. Keep that
			// slot when right-aligning parts so Database..Object is not mistaken for Schema.Object.
			var server = values.Length == 4 ? values[0] : null;
			var database = values.Length >= 3 ? values[values.Length - 3] : null;
			var schema = values.Length >= 2 ? values[values.Length - 2] : null;
			var objectName = values[values.Length - 1];

			if (string.IsNullOrEmpty(objectName) || objectName[0] == '#')
			{
				return;
			}

			_candidates.Add(new Candidate(
				kind,
				offset,
				length,
				server,
				database,
				schema,
				objectName,
				values.Length,
				declaresOwnAlias,
				isActionTarget,
				isRemoteDataSource));
		}

		private void AddAlias(Identifier? alias)
		{
			if (alias is not null)
			{
				_aliases.Add(new AliasDeclaration(
					alias.Value,
					alias.StartOffset,
					FindSmallestScope(alias.StartOffset, queryScope: true),
					FindSmallestScope(alias.StartOffset, queryScope: false)));
			}
		}

		private void AddTriggerTarget(TriggerStatementBody node)
		{
			if (node.FragmentLength > 0)
			{
				_triggerScopes.Add(new ReferenceScope(node.StartOffset, EndOffset(node)));
			}

			if (node.TriggerObject?.Name is { } triggerTarget)
			{
				AddSchemaObject(SqlObjectReferenceKind.TableOrView, triggerTarget);
			}
		}

		private void AddReferenceScope(TSqlFragment fragment)
		{
			if (fragment.FragmentLength > 0)
			{
				_referenceScopes.Add(new ReferenceScope(
					fragment.StartOffset,
					EndOffset(fragment),
					fragment is QuerySpecification));
			}
		}

		private bool ShouldExclude(Candidate candidate)
		{
			if (string.Equals(candidate.Schema, "sys", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(candidate.Schema, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			if (candidate.Kind == SqlObjectReferenceKind.Type &&
				candidate.Database is null &&
				candidate.Schema is null &&
				BuiltInClrTypeNames.Contains(candidate.Object))
			{
				return true;
			}

			if (candidate.Kind != SqlObjectReferenceKind.TableOrView)
			{
				return false;
			}

			if (candidate.Server is null &&
				candidate.Database is null &&
				candidate.Schema is null &&
				_triggerScopes.Any(scope => scope.Contains(candidate.Offset)) &&
				(candidate.Object.Equals("inserted", StringComparison.OrdinalIgnoreCase) ||
				 candidate.Object.Equals("deleted", StringComparison.OrdinalIgnoreCase)))
			{
				return true;
			}

			if (candidate.Server is not null ||
				candidate.Database is not null ||
				candidate.Schema is not null)
			{
				return false;
			}

			if (!candidate.IsActionTarget &&
				_cteScopes.Any(scope =>
				scope.Contains(candidate.Offset) &&
				scope.Names.Contains(candidate.Object, StringComparer.OrdinalIgnoreCase)))
			{
				return true;
			}

			if (candidate.DeclaresOwnAlias || candidate.IsActionTarget)
			{
				return false;
			}

			var candidateQueryScope = FindSmallestScope(candidate.Offset, queryScope: true);
			var candidateStatementScope = FindSmallestScope(candidate.Offset, queryScope: false);
			return _aliases.Any(alias =>
				string.Equals(alias.Name, candidate.Object, StringComparison.OrdinalIgnoreCase) &&
				(candidateQueryScope is not null
					? alias.QueryScope == candidateQueryScope
					: alias.StatementScope == candidateStatementScope));
		}

		private ReferenceScope? FindSmallestScope(int offset, bool? queryScope = null)
			=> _referenceScopes
				.Where(scope =>
					scope.Contains(offset) &&
					(queryScope is null || scope.IsQueryScope == queryScope))
				.OrderBy(scope => scope.End - scope.Start)
				.FirstOrDefault();

		private static int EndOffset(TSqlFragment fragment)
			=> fragment.StartOffset + fragment.FragmentLength;

		private sealed class Candidate
		{
			public Candidate(
				SqlObjectReferenceKind kind,
				int offset,
				int length,
				string? server,
				string? database,
				string? schema,
				string objectName,
				int partCount,
				bool declaresOwnAlias,
				bool isActionTarget,
				bool isRemoteDataSource)
			{
				Kind = kind;
				Offset = offset;
				Length = length;
				Server = server;
				Database = database;
				Schema = schema;
				Object = objectName;
				PartCount = partCount;
				DeclaresOwnAlias = declaresOwnAlias;
				IsActionTarget = isActionTarget;
				IsRemoteDataSource = isRemoteDataSource;
			}

			public SqlObjectReferenceKind Kind { get; }
			public int Offset { get; }
			public int Length { get; }
			public string? Server { get; }
			public string? Database { get; }
			public string? Schema { get; }
			public string Object { get; }
			public int PartCount { get; }
			public bool DeclaresOwnAlias { get; }
			public bool IsActionTarget { get; }
			public bool IsRemoteDataSource { get; }
		}

		private sealed class AliasDeclaration
		{
			public AliasDeclaration(
				string name,
				int offset,
				ReferenceScope? queryScope,
				ReferenceScope? statementScope)
			{
				Name = name;
				Offset = offset;
				QueryScope = queryScope;
				StatementScope = statementScope;
			}

			public string Name { get; }
			public int Offset { get; }
			public ReferenceScope? QueryScope { get; }
			public ReferenceScope? StatementScope { get; }
		}

		private sealed class ReferenceScope
		{
			public ReferenceScope(int start, int end, bool isQueryScope = false)
			{
				Start = start;
				End = end;
				IsQueryScope = isQueryScope;
			}

			public int Start { get; }
			public int End { get; }
			public bool IsQueryScope { get; }
			public bool Contains(int offset) => offset >= Start && offset < End;
		}

		private sealed class CteScope
		{
			public CteScope(int start, int end, string[] names)
			{
				Start = start;
				End = end;
				Names = names;
			}

			public int Start { get; }
			public int End { get; }
			public string[] Names { get; }
			public bool Contains(int offset) => offset >= Start && offset < End;
		}

		private sealed class FirstNamedTableVisitor : TSqlFragmentVisitor
		{
			public SchemaObjectName? Name { get; private set; }

			public override void ExplicitVisit(NamedTableReference node)
			{
				Name ??= node.SchemaObject;
			}
		}
	}
}
