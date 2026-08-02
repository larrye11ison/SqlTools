using System.Linq;
using Xunit;

namespace SqlPhanos.CodeFormatting.Tests;

public sealed class SqlObjectReferenceAnalyzerTests
{
	private static readonly SqlObjectReferenceAnalyzer analyzer = new();

	[Fact]
	public void MapsOneThroughFourPartNamesAndClassifiesLinkedServer()
	{
		var sql = """
			SELECT *
			FROM ObjectA AS a
			JOIN SchemaB.ObjectB AS b ON b.Id = a.Id
			JOIN DatabaseC.SchemaC.ObjectC AS c ON c.Id = b.Id
			JOIN ServerD.DatabaseD.SchemaD.ObjectD AS d ON d.Id = c.Id;
			""";

		var result = analyzer.Analyze(sql);

		Assert.True(result.ParseSucceeded);
		Assert.Collection(
			result.References,
			reference => AssertName(reference, "ObjectA", null, null, null, "ObjectA"),
			reference => AssertName(reference, "SchemaB.ObjectB", null, null, "SchemaB", "ObjectB"),
			reference => AssertName(reference, "DatabaseC.SchemaC.ObjectC", null, "DatabaseC", "SchemaC", "ObjectC"),
			reference =>
			{
				AssertName(reference, "ServerD.DatabaseD.SchemaD.ObjectD", "ServerD", "DatabaseD", "SchemaD", "ObjectD");
				Assert.Equal(4, reference.PartCount);
				Assert.Equal(SqlObjectReferenceClassification.LinkedServer, reference.Classification);
			});
	}

	[Fact]
	public void PreservesQuotedSourceSpanAndUnescapesNormalizedParts()
	{
		var sql = """SELECT * FROM [Database]]A] . "Schema""B" . [Object C];""";

		var reference = Assert.Single(analyzer.Analyze(sql).References);

		Assert.Equal("""[Database]]A] . "Schema""B" . [Object C]""", reference.Text);
		Assert.Equal(reference.Text, sql.Substring(reference.Offset, reference.Length));
		Assert.Equal("Database]A", reference.Database);
		Assert.Equal("Schema\"B", reference.Schema);
		Assert.Equal("Object C", reference.Object);
	}

	[Fact]
	public void PreservesOmittedSchemaInProcedureNames()
	{
		var sql = """
			EXEC ProcedureA;
			EXEC DatabaseA..ProcedureA;
			EXEC [Database A]..[Procedure A];
			EXEC ServerA.DatabaseA..ProcedureA;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Collection(
			references,
			reference =>
			{
				AssertName(reference, "ProcedureA", null, null, null, "ProcedureA");
				Assert.Equal(1, reference.PartCount);
				Assert.Equal(SqlObjectReferenceClassification.Local, reference.Classification);
			},
			reference =>
			{
				AssertName(reference, "DatabaseA..ProcedureA", null, "DatabaseA", null, "ProcedureA");
				Assert.Equal(3, reference.PartCount);
				Assert.Equal(SqlObjectReferenceClassification.Local, reference.Classification);
			},
			reference =>
			{
				AssertName(reference, "[Database A]..[Procedure A]", null, "Database A", null, "Procedure A");
				Assert.Equal(3, reference.PartCount);
				Assert.Equal(SqlObjectReferenceClassification.Local, reference.Classification);
			},
			reference =>
			{
				AssertName(reference, "ServerA.DatabaseA..ProcedureA", "ServerA", "DatabaseA", null, "ProcedureA");
				Assert.Equal(4, reference.PartCount);
				Assert.Equal(SqlObjectReferenceClassification.LinkedServer, reference.Classification);
			});

		Assert.All(
			references,
			reference => Assert.Equal(reference.Text, sql.Substring(reference.Offset, reference.Length)));
	}

	[Fact]
	public void ReturnsEveryRepeatedOccurrenceWithItsOwnOffset()
	{
		var sql = "SELECT * FROM dbo.ObjectA; SELECT * FROM dbo.ObjectA;";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(2, references.Count);
		Assert.Equal("dbo.ObjectA", references[0].Text);
		Assert.Equal("dbo.ObjectA", references[1].Text);
		Assert.NotEqual(references[0].Offset, references[1].Offset);
		Assert.All(references, reference => Assert.Equal(reference.Text, sql.Substring(reference.Offset, reference.Length)));
	}

	[Fact]
	public void FindsSelectJoinAndDmlTargets()
	{
		var sql = """
			SELECT * INTO dbo.ObjectOutput FROM dbo.ObjectSource s JOIN dbo.ObjectJoin j ON j.Id = s.Id;
			INSERT dbo.ObjectInsert DEFAULT VALUES;
			UPDATE dbo.ObjectUpdate SET ValueA = 1;
			DELETE FROM dbo.ObjectDelete;
			MERGE dbo.ObjectMerge AS target
			USING dbo.ObjectUsing AS source ON source.Id = target.Id
			WHEN MATCHED THEN UPDATE SET ValueA = source.ValueA;
			""";

		var names = analyzer.Analyze(sql).References
			.Where(reference => reference.Kind == SqlObjectReferenceKind.TableOrView)
			.Select(reference => reference.Text)
			.ToArray();

		Assert.Equal(
			new[]
			{
				"dbo.ObjectOutput", "dbo.ObjectSource", "dbo.ObjectJoin", "dbo.ObjectInsert",
				"dbo.ObjectUpdate", "dbo.ObjectDelete", "dbo.ObjectMerge", "dbo.ObjectUsing",
			},
			names);
	}

	[Fact]
	public void FindsViewReferencesInStoredProcedureSelects()
	{
		var sql = """
			CREATE OR ALTER PROCEDURE dbo.ProcedureA
			AS
			SELECT a.Id
			FROM dbo.ViewA AS a
			INNER JOIN ReportingDatabase.reporting.ViewB AS b ON b.Id = a.Id;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Collection(
			references,
			reference => AssertName(reference, "dbo.ViewA", null, null, "dbo", "ViewA"),
			reference => AssertName(
				reference,
				"ReportingDatabase.reporting.ViewB",
				null,
				"ReportingDatabase",
				"reporting",
				"ViewB"));
	}

	[Fact]
	public void FindsMaintenanceAndBulkStatementTargets()
	{
		var sql = """
			TRUNCATE TABLE DatabaseA..TableA;
			BULK INSERT SchemaB.TableB FROM 'C:\Import\FileB.csv';
			INSERT BULK SchemaB.TableC ([Id] int);
			UPDATE STATISTICS [Database C].[Schema C].[Table C];
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Collection(
			references,
			reference => AssertName(reference, "DatabaseA..TableA", null, "DatabaseA", null, "TableA"),
			reference => AssertName(reference, "SchemaB.TableB", null, null, "SchemaB", "TableB"),
			reference => AssertName(reference, "SchemaB.TableC", null, null, "SchemaB", "TableC"),
			reference => AssertName(reference, "[Database C].[Schema C].[Table C]", null, "Database C", "Schema C", "Table C"));
	}

	[Fact]
	public void FindsAlterTableAndIndexTargets()
	{
		var sql = """
			ALTER TABLE dbo.TableA ADD ColumnA int NULL;
			ALTER TABLE dbo.TableB SWITCH TO archive.TableB;
			CREATE INDEX IX_ViewA ON dbo.ViewA(Id);
			ALTER INDEX ALL ON dbo.TableC REBUILD;
			DROP INDEX IX_TableD ON dbo.TableD;
			SET IDENTITY_INSERT dbo.TableE ON;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[]
			{
				"dbo.TableA",
				"dbo.TableB",
				"archive.TableB",
				"dbo.ViewA",
				"dbo.TableC",
				"dbo.TableD",
				"dbo.TableE",
			},
			references.Select(reference => reference.Text).ToArray());
	}

	[Fact]
	public void FindsDropTargetsForEveryScriptableObjectKind()
	{
		var sql = """
			DROP TABLE dbo.TableA;
			DROP VIEW dbo.ViewA;
			DROP PROCEDURE dbo.ProcedureA;
			DROP FUNCTION dbo.FunctionA;
			DROP SEQUENCE dbo.SequenceA;
			DROP TRIGGER dbo.TriggerA;
			DROP TYPE dbo.TypeA;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Collection(
			references,
			reference => Assert.Equal(SqlObjectReferenceKind.TableOrView, reference.Kind),
			reference => Assert.Equal(SqlObjectReferenceKind.TableOrView, reference.Kind),
			reference => Assert.Equal(SqlObjectReferenceKind.Procedure, reference.Kind),
			reference => Assert.Equal(SqlObjectReferenceKind.Function, reference.Kind),
			reference => Assert.Equal(SqlObjectReferenceKind.Sequence, reference.Kind),
			reference => Assert.Equal(SqlObjectReferenceKind.Trigger, reference.Kind),
			reference => Assert.Equal(SqlObjectReferenceKind.Type, reference.Kind));
	}

	[Fact]
	public void FindsTriggerControlTargets()
	{
		var sql = """
			ENABLE TRIGGER TriggerA ON dbo.TableA;
			DISABLE TRIGGER dbo.TriggerB ON dbo.TableB;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "TriggerA", "dbo.TableA", "dbo.TriggerB", "dbo.TableB" },
			references.Select(reference => reference.Text).ToArray());
		Assert.Equal("dbo", references[0].Schema);
		Assert.Equal(
			new[]
			{
				SqlObjectReferenceKind.Trigger,
				SqlObjectReferenceKind.TableOrView,
				SqlObjectReferenceKind.Trigger,
				SqlObjectReferenceKind.TableOrView,
			},
			references.Select(reference => reference.Kind).ToArray());
	}

	[Fact]
	public void FindsSchemaTransferAndSynonymBaseTargets()
	{
		var sql = """
			ALTER SCHEMA archive TRANSFER dbo.TableA;
			CREATE SYNONYM dbo.SynonymA FOR DatabaseA.reporting.ViewA;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "dbo.TableA", "DatabaseA.reporting.ViewA" },
			references.Select(reference => reference.Text).ToArray());
		Assert.All(references, reference => Assert.Equal(SqlObjectReferenceKind.Any, reference.Kind));
	}

	[Fact]
	public void DerivedTableAliasesAreNeverTreatedAsObjects()
	{
		var result = analyzer.Analyze(
			"UPDATE q SET c = 1 FROM (SELECT 1 AS c) AS q;");

		Assert.True(result.ParseSucceeded);
		Assert.Empty(result.References);
	}

	[Fact]
	public void IntoTargetIsNotSuppressedByCollidingSourceAlias()
	{
		var result = analyzer.Analyze(
			"SELECT * INTO TargetA FROM dbo.SourceA AS TargetA;");

		Assert.Equal(
			new[] { "TargetA", "dbo.SourceA" },
			result.References.Select(reference => reference.Text).ToArray());
	}

	[Fact]
	public void FindsSecurityAndAuthorizationTargets()
	{
		var sql = """
			GRANT SELECT ON OBJECT::dbo.ViewA TO public;
			REVOKE REFERENCES ON TYPE::dbo.TypeA FROM public;
			ALTER AUTHORIZATION ON OBJECT::dbo.TableA TO dbo;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "dbo.ViewA", "dbo.TypeA", "dbo.TableA" },
			references.Select(reference => reference.Text).ToArray());
		Assert.Equal(
			new[]
			{
				SqlObjectReferenceKind.SchemaObject,
				SqlObjectReferenceKind.Type,
				SqlObjectReferenceKind.SchemaObject,
			},
			references.Select(reference => reference.Kind).ToArray());
	}

	[Fact]
	public void FindsQueueActivationProcedureAndResultSetType()
	{
		var sql = """
			CREATE QUEUE dbo.QueueA
			WITH ACTIVATION
			(
				STATUS = ON,
				PROCEDURE_NAME = dbo.ProcedureA,
				MAX_QUEUE_READERS = 1,
				EXECUTE AS OWNER
			);
			EXEC dbo.ProcedureB WITH RESULT SETS (AS TYPE dbo.TableTypeA);
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "dbo.ProcedureA", "dbo.ProcedureB", "dbo.TableTypeA" },
			references.Select(reference => reference.Text).ToArray());
		Assert.Equal(
			new[]
			{
				SqlObjectReferenceKind.Procedure,
				SqlObjectReferenceKind.Executable,
				SqlObjectReferenceKind.Type,
			},
			references.Select(reference => reference.Kind).ToArray());
	}

	[Fact]
	public void FindsExecutableScalarFunctionAndResultSetTableFunction()
	{
		var sql = """
			EXEC @result = dbo.ScalarFunctionA @value = 1;
			EXEC dbo.ProcedureA WITH RESULT SETS (AS OBJECT dbo.TableFunctionA);
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[]
			{
				SqlObjectReferenceKind.Executable,
				SqlObjectReferenceKind.Executable,
				SqlObjectReferenceKind.Rowset,
			},
			references.Select(reference => reference.Kind).ToArray());
		Assert.Equal(
			new[] { "dbo.ScalarFunctionA", "dbo.ProcedureA", "dbo.TableFunctionA" },
			references.Select(reference => reference.Text).ToArray());
	}

	[Fact]
	public void FindsDropStatisticsAndAlterTableTriggerTargets()
	{
		var sql = """
			DROP STATISTICS DatabaseA.dbo.TableA.StatsA;
			ALTER TABLE dbo.TableB DISABLE TRIGGER TriggerB;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "DatabaseA.dbo.TableA", "dbo.TableB", "TriggerB" },
			references.Select(reference => reference.Text).ToArray());
		Assert.Equal("dbo", references[2].Schema);
		Assert.Equal(SqlObjectReferenceKind.Trigger, references[2].Kind);
	}

	[Fact]
	public void FindsStaticDbccTableArguments()
	{
		var sql = """
			DBCC CHECKTABLE ('dbo.TableA');
			DBCC INDEXDEFRAG (DatabaseA, 'reporting.TableB');
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "'dbo.TableA'", "'reporting.TableB'" },
			references.Select(reference => reference.Text).ToArray());
		Assert.Equal("dbo", references[0].Schema);
		Assert.Equal("TableA", references[0].Object);
		Assert.Equal("reporting", references[1].Schema);
		Assert.Equal("TableB", references[1].Object);
	}

	[Fact]
	public void FindsSecurityPolicyFunctionAndTableTargets()
	{
		var sql = """
			CREATE SECURITY POLICY security.PolicyA
			ADD FILTER PREDICATE security.FunctionA(UserId) ON dbo.TableA
			WITH (STATE = ON);
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "security.FunctionA", "dbo.TableA" },
			references.Select(reference => reference.Text).ToArray());
		Assert.Equal(SqlObjectReferenceKind.Function, references[0].Kind);
		Assert.Equal(SqlObjectReferenceKind.TableOrView, references[1].Kind);
	}

	[Fact]
	public void FindsClrTypeStaticPropertyTarget()
	{
		var reference = Assert.Single(
			analyzer.Analyze("SELECT dbo.TypeA::StaticProperty;").References);

		Assert.Equal("dbo.TypeA", reference.Text);
		Assert.Equal(SqlObjectReferenceKind.Type, reference.Kind);
	}

	[Fact]
	public void OrdinaryInsertedTableIsNotExcludedOutsideTriggerBody()
	{
		var reference = Assert.Single(
			analyzer.Analyze("SELECT * FROM inserted;").References);

		Assert.Equal("inserted", reference.Text);
	}

	[Fact]
	public void FindsLegacyDropIndexAndOptimizerTableHintOccurrences()
	{
		var sql = """
			DROP INDEX dbo.TableA.IX_A;
			SELECT *
			FROM dbo.TableB
			OPTION (TABLE HINT (dbo.TableB, FORCESEEK));
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "dbo.TableA", "dbo.TableB", "dbo.TableB" },
			references.Select(reference => reference.Text).ToArray());
	}

	[Fact]
	public void OptimizerTableHintAliasIsNotAnObjectReference()
	{
		var sql = """
			SELECT *
			FROM dbo.TableB AS b
			OPTION (TABLE HINT (b, FORCESEEK));
			""";

		var reference = Assert.Single(analyzer.Analyze(sql).References);

		Assert.Equal("dbo.TableB", reference.Text);
	}

	[Fact]
	public void NestedAliasDoesNotSuppressOuterTableWithSameName()
	{
		var sql = """
			SELECT *
			FROM NavOuter
			WHERE EXISTS
			(
				SELECT 1
				FROM dbo.NavInner AS NavOuter
			);
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "NavOuter", "dbo.NavInner" },
			references.Select(reference => reference.Text).ToArray());
	}

	[Fact]
	public void InsertAndOutputIntoTargetsSurviveAliasNameCollisions()
	{
		var sql = """
			INSERT NavTarget SELECT * FROM dbo.NavSource AS NavTarget;
			UPDATE dbo.NavTarget
			SET Id = 1
			OUTPUT inserted.Id INTO NavAudit
			FROM dbo.NavSource AS NavAudit;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Contains(references, reference => reference.Text == "NavTarget");
		Assert.Contains(references, reference => reference.Text == "NavAudit");
	}

	[Fact]
	public void InferredTriggerInheritsCrossDatabaseTableContext()
	{
		var sql = "ALTER TABLE DatabaseA.SchemaA.TableA DISABLE TRIGGER TriggerA;";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(new[] { "DatabaseA.SchemaA.TableA", "TriggerA" },
			references.Select(reference => reference.Text).ToArray());
		Assert.Equal("DatabaseA", references[1].Database);
		Assert.Equal("SchemaA", references[1].Schema);
		Assert.Equal(SqlObjectReferenceKind.Trigger, references[1].Kind);
	}

	[Fact]
	public void FindsReadWriteAndUpdateTextTableTargets()
	{
		var sql = """
			READTEXT dbo.Documents.Body @pointer 0 32;
			WRITETEXT dbo.Documents.Body @pointer 'A';
			UPDATETEXT dbo.Documents.Body @pointer 0 1 'B';
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "dbo.Documents", "dbo.Documents", "dbo.Documents" },
			references.Select(reference => reference.Text).ToArray());
	}

	[Fact]
	public void FindsIdentityMetadataFunctionTableArguments()
	{
		var sql = """
			SELECT IDENT_CURRENT('dbo.TableA'),
				IDENT_INCR('dbo.TableB'),
				IDENT_SEED('dbo.TableC');
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "'dbo.TableA'", "'dbo.TableB'", "'dbo.TableC'" },
			references.Select(reference => reference.Text).ToArray());
	}

	[Fact]
	public void FindsStaticObjectAndTypeMetadataFunctionArguments()
	{
		var sql = """
			SELECT OBJECT_ID('dbo.TableA'),
				COL_LENGTH('dbo.TableB', 'Id'),
				INDEX_COL('DatabaseA.dbo.ViewA', 1, 1),
				TYPE_ID('dbo.TypeA'),
				TYPEPROPERTY('dbo.TypeB', 'OwnerId');
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[]
			{
				SqlObjectReferenceKind.SchemaObject,
				SqlObjectReferenceKind.TableOrView,
				SqlObjectReferenceKind.TableOrView,
				SqlObjectReferenceKind.Type,
				SqlObjectReferenceKind.Type,
			},
			references.Select(reference => reference.Kind).ToArray());
		Assert.Equal(
			new[]
			{
				"'dbo.TableA'",
				"'dbo.TableB'",
				"'DatabaseA.dbo.ViewA'",
				"'dbo.TypeA'",
				"'dbo.TypeB'",
			},
			references.Select(reference => reference.Text).ToArray());
		Assert.Equal(SqlObjectReferenceKind.SchemaObject, references[0].Kind);
	}

	[Fact]
	public void IgnoresTemporaryObjectsInLiteralMetadataAndDbccArguments()
	{
		var sql = """
			SELECT OBJECT_ID('tempdb..#TempA'), COL_LENGTH('#TempA', 'Id');
			DBCC CHECKTABLE ('#TempA');
			""";

		Assert.Empty(analyzer.Analyze(sql).References);
	}

	[Fact]
	public void ExcludesBuiltInClrTypesButKeepsSchemaQualifiedUserTypes()
	{
		var sql = """
			SELECT geometry::STGeomFromText('POINT (1 1)', 0);
			SELECT hierarchyid::Parse('/1/');
			SELECT dbo.TypeA::StaticProperty;
			""";

		var reference = Assert.Single(analyzer.Analyze(sql).References);

		Assert.Equal("dbo.TypeA", reference.Text);
	}

	[Fact]
	public void FindsHasPermsByNameObjectAndTypeArguments()
	{
		var sql = """
			SELECT HAS_PERMS_BY_NAME('dbo.NavTable', 'OBJECT', 'SELECT');
			SELECT HAS_PERMS_BY_NAME('dbo.NavType', 'TYPE', 'REFERENCES');
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { SqlObjectReferenceKind.SchemaObject, SqlObjectReferenceKind.Type },
			references.Select(reference => reference.Kind).ToArray());
	}

	[Fact]
	public void FindsStaticObjectArgumentsToSupportedSystemProcedures()
	{
		var sql = """
			EXEC sys.sp_refreshview N'dbo.NavView';
			EXEC sys.sp_refreshsqlmodule N'dbo.NavProcedure';
			EXEC sys.sp_recompile N'dbo.NavTable';
			EXEC sys.sp_rename N'dbo.NavOldName', N'NavNewName';
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "N'dbo.NavView'", "N'dbo.NavProcedure'", "N'dbo.NavTable'", "N'dbo.NavOldName'" },
			references.Select(reference => reference.Text).ToArray());
		Assert.Equal(
			new[]
			{
				SqlObjectReferenceKind.TableOrView,
				SqlObjectReferenceKind.SchemaObject,
				SqlObjectReferenceKind.SchemaObject,
				SqlObjectReferenceKind.SchemaObject,
			},
			references.Select(reference => reference.Kind).ToArray());
	}

	[Fact]
	public void FindsDbccCheckConstraintsTableWhenScriptDomReportsFreeCommand()
	{
		var reference = Assert.Single(
			analyzer.Analyze("DBCC CHECKCONSTRAINTS ('dbo.NavTable');").References);

		Assert.Equal("'dbo.NavTable'", reference.Text);
	}

	[Fact]
	public void MarksStaticRemoteDataSourceObjectsAsNonLocal()
	{
		var sql = """
			SELECT * FROM OPENROWSET(
				'SQLNCLI',
				'Server=RemoteServer;Trusted_Connection=yes;',
				tempdb.dbo.NavTable) AS remoteA;
			SELECT * FROM OPENDATASOURCE(
				'SQLNCLI',
				'Data Source=RemoteServer;Integrated Security=SSPI;'
			).tempdb.dbo.NavView AS remoteB;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(2, references.Count);
		Assert.All(
			references,
			reference => Assert.Equal(
				SqlObjectReferenceClassification.RemoteDataSource,
				reference.Classification));
	}

	[Fact]
	public void FindsExternalTableDropButExcludesDatabaseDdlTriggers()
	{
		var sql = """
			DROP EXTERNAL TABLE dbo.NavExternal;
			DROP TRIGGER NavDdlTrigger ON DATABASE;
			ENABLE TRIGGER NavDdlTrigger ON DATABASE;
			""";

		var reference = Assert.Single(analyzer.Analyze(sql).References);

		Assert.Equal("dbo.NavExternal", reference.Text);
		Assert.Equal(SqlObjectReferenceKind.TableOrView, reference.Kind);
	}

	[Fact]
	public void ExcludesExplicitSystemProceduresAndFunctions()
	{
		var sql = """
			EXEC sys.sp_executesql N'SELECT 1';
			SELECT * FROM sys.fn_builtin_permissions(DEFAULT);
			""";

		Assert.Empty(analyzer.Analyze(sql).References);
	}

	[Fact]
	public void FindsForeignKeyReferenceTargetsInTableScripts()
	{
		var sql = """
			CREATE TABLE dbo.ChildTable
			(
				ParentId int REFERENCES dbo.ParentTable(Id),
				CONSTRAINT FK_Child_Other FOREIGN KEY (ParentId)
					REFERENCES OtherDatabase..OtherParentTable(Id)
			);
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Collection(
			references,
			reference => AssertName(reference, "dbo.ParentTable", null, null, "dbo", "ParentTable"),
			reference => AssertName(reference, "OtherDatabase..OtherParentTable", null, "OtherDatabase", null, "OtherParentTable"));
	}

	[Fact]
	public void FindsTriggerTargetWithoutTreatingTriggerNameAsAReference()
	{
		var sql = """
			CREATE OR ALTER TRIGGER dbo.TriggerA
			ON DatabaseA..TableA
			AFTER INSERT
			AS
			SELECT 1;
			""";

		var reference = Assert.Single(analyzer.Analyze(sql).References);

		AssertName(reference, "DatabaseA..TableA", null, "DatabaseA", null, "TableA");
	}

	[Fact]
	public void ExcludesTriggerPseudoTablesAndSystemCatalogViews()
	{
		var sql = """
			CREATE OR ALTER TRIGGER dbo.TriggerA
			ON dbo.TableA
			AFTER UPDATE
			AS
			SELECT * FROM inserted;
			SELECT * FROM deleted;
			SELECT * FROM sys.objects;
			SELECT * FROM INFORMATION_SCHEMA.TABLES;
			""";

		var reference = Assert.Single(analyzer.Analyze(sql).References);

		Assert.Equal("dbo.TableA", reference.Text);
	}

	[Fact]
	public void FindsProceduresScalarAndTableFunctionsSequencesAndTypes()
	{
		var sql = """
			EXEC DatabaseA.SchemaA.ProcedureA;
			SELECT SchemaA.FunctionA(1), SchemaA.GETDATE(), DATALENGTH('A'), NEXT VALUE FOR SchemaA.SequenceA
			FROM SchemaA.TableFunctionA(1);
			DECLARE @ValueA SchemaA.TypeA;
			DECLARE @RowsA SchemaA.TableTypeA;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Contains(references, reference => reference.Kind == SqlObjectReferenceKind.Executable && reference.Text == "DatabaseA.SchemaA.ProcedureA");
		Assert.Contains(references, reference => reference.Kind == SqlObjectReferenceKind.Function && reference.Text == "SchemaA.FunctionA");
		Assert.Contains(references, reference => reference.Kind == SqlObjectReferenceKind.Function && reference.Text == "SchemaA.GETDATE");
		Assert.DoesNotContain(references, reference => reference.Text == "DATALENGTH");
		Assert.Contains(references, reference => reference.Kind == SqlObjectReferenceKind.Function && reference.Text == "SchemaA.TableFunctionA");
		Assert.Contains(references, reference => reference.Kind == SqlObjectReferenceKind.Sequence && reference.Text == "SchemaA.SequenceA");
		Assert.Contains(references, reference => reference.Kind == SqlObjectReferenceKind.Type && reference.Text == "SchemaA.TypeA");
		Assert.Contains(references, reference => reference.Kind == SqlObjectReferenceKind.Type && reference.Text == "SchemaA.TableTypeA");
	}

	[Fact]
	public void ExcludesDeclarationsCtesAliasesVariablesTempsAndBuiltIns()
	{
		var sql = """
			CREATE OR ALTER PROCEDURE dbo.ProcedureDeclaration
			AS
			BEGIN
				WITH CteA AS (SELECT * FROM dbo.ObjectA)
				SELECT GETDATE(), COALESCE(aliasA.ValueA, 0)
				FROM CteA AS c
				JOIN dbo.ObjectB AS aliasA ON aliasA.Id = c.Id;

				UPDATE aliasB SET ValueA = 1 FROM dbo.ObjectC AS aliasB;
				DECLARE @Rows TABLE (Id int);
				SELECT * FROM @Rows;
				CREATE TABLE #TempA (Id int);
				SELECT * FROM #TempA;
				SELECT * INTO ##TempB FROM dbo.ObjectD;
			END;
			""";

		var references = analyzer.Analyze(sql).References;

		Assert.Equal(
			new[] { "dbo.ObjectA", "dbo.ObjectB", "dbo.ObjectC", "dbo.ObjectD" },
			references.Select(reference => reference.Text).ToArray());
	}

	[Fact]
	public void IgnoresCommentsStringsAndDynamicSql()
	{
		var sql = """
			-- SELECT * FROM dbo.CommentObject;
			SELECT 'dbo.StringObject', *
			FROM dbo.RealObject;
			EXEC(N'SELECT * FROM dbo.DynamicObject');
			""";

		var reference = Assert.Single(analyzer.Analyze(sql).References);

		Assert.Equal("dbo.RealObject", reference.Text);
	}

	[Fact]
	public void MalformedSqlReturnsParseErrorsAndKeepsValidPartialAstReferences()
	{
		var result = analyzer.Analyze("SELECT * FROM dbo.ObjectA;\nGO\nSELECT * FROM");

		Assert.False(result.ParseSucceeded);
		Assert.NotEmpty(result.ParseErrors);
		var reference = Assert.Single(result.References);
		Assert.Equal("dbo.ObjectA", reference.Text);
	}

	private static void AssertName(
		SqlObjectReference reference,
		string text,
		string? server,
		string? database,
		string? schema,
		string objectName)
	{
		Assert.Equal(text, reference.Text);
		Assert.Equal(server, reference.Server);
		Assert.Equal(database, reference.Database);
		Assert.Equal(schema, reference.Schema);
		Assert.Equal(objectName, reference.Object);
	}
}
