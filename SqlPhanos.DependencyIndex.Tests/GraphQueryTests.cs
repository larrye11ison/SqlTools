namespace SqlPhanos.DependencyIndex.Tests;

public sealed class GraphQueryTests
{
    [Fact]
    public async Task DriDependenciesAreExcludedByDefaultButTriggersAlwaysShow()
    {
        await using var index = await TestIndex.CreateAsync();
        var database = new CatalogDatabase(5, "MainDb", null, "ONLINE", true);
        var objects = new[]
        {
            TestIndex.Object(1, "dbo", "TableA", "USER_TABLE", null),
            TestIndex.Object(2, "dbo", "TableB", "USER_TABLE", null),
            TestIndex.Object(3, "dbo", "TR_TableA", "SQL_TRIGGER", null, parentId: 1),
            TestIndex.Object(4, "dbo", "CK_TableA", "CHECK_CONSTRAINT", null, parentId: 1),
            TestIndex.Object(5, "dbo", "DF_TableA", "DEFAULT_CONSTRAINT", null, parentId: 1),
        };
        var databaseSnapshot = new DatabaseCatalogSnapshot(database, objects,
            [new CatalogForeignKey(1, 2, "FK_TableA_TableB")], [], [], ScanOutcome.Succeeded);
        await index.Builder.BuildAsync(TestIndex.Snapshot(databases: [databaseSnapshot]));
        var table = (await index.Graph.SearchObjectsAsync("TableA", 10)).Single(item => item.Name == "TableA");

        var defaultGraph = await index.Graph.GetDirectAsync(table.ObjectKey, GraphDirection.Both);
        var withDri = await index.Graph.GetDirectAsync(table.ObjectKey, GraphDirection.Both,
            new GraphFilter(IncludeDriDependencies: true));

        Assert.DoesNotContain(defaultGraph.Edges, edge => edge.Edge.EvidenceKind == EvidenceKind.ForeignKey);
        Assert.DoesNotContain(defaultGraph.Edges, edge => edge.Edge.EvidenceKind == EvidenceKind.Constraint);
        Assert.Contains(defaultGraph.Edges, edge => edge.Edge.EvidenceKind == EvidenceKind.TriggerParent);
        Assert.Contains(defaultGraph.Nodes, node => node.Object.Name == "TR_TableA");
        Assert.DoesNotContain(defaultGraph.Nodes, node => node.Object.Name is "CK_TableA" or "DF_TableA" or "TableB");

        Assert.Contains(withDri.Edges, edge => edge.Edge.EvidenceKind == EvidenceKind.ForeignKey);
        Assert.Contains(withDri.Edges, edge =>
            edge.Edge.EvidenceKind == EvidenceKind.Constraint && edge.Edge.ReferenceKind == "CheckConstraint");
        Assert.Contains(withDri.Edges, edge =>
            edge.Edge.EvidenceKind == EvidenceKind.Constraint && edge.Edge.ReferenceKind == "DefaultConstraint");
        Assert.Contains(withDri.Nodes, node => node.Object.Name is "CK_TableA" or "DF_TableA" or "TableB");
    }

    [Fact]
    public async Task ReverseTraversalIsCycleSafeAndBounded()
    {
        await using var index = await TestIndex.CreateAsync();
        await index.Builder.BuildAsync(TestIndex.Snapshot());
        var root = Assert.Single(await index.Graph.SearchObjectsAsync("ViewA"));

        var graph = await index.Graph.TraverseAsync(root.ObjectKey, GraphDirection.UsedBy, 10, 20);

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(3, graph.Edges.Count);
        Assert.False(graph.Truncated);
        Assert.Equal(new[] { "ViewA", "ViewC", "ViewB" },
            graph.Nodes.OrderBy(node => node.Depth).Select(node => node.Object.Name));
    }

    [Fact]
    public async Task FindsShortestPathAndStronglyConnectedCycle()
    {
        await using var index = await TestIndex.CreateAsync();
        await index.Builder.BuildAsync(TestIndex.Snapshot());
        var objects = await index.Graph.SearchObjectsAsync("View", 10);
        var a = objects.Single(item => item.Name == "ViewA");
        var c = objects.Single(item => item.Name == "ViewC");

        var path = await index.Graph.FindShortestPathAsync(a.ObjectKey, c.ObjectKey);
        var cycles = await index.Graph.FindCyclesAsync();

        Assert.NotNull(path);
        Assert.Equal(new[] { "ViewA", "ViewB", "ViewC" }, path.Nodes.Select(node => node.Name));
        Assert.Equal(2, path.Edges.Count);
        Assert.Single(cycles);
        Assert.Equal(new[] { "ViewA", "ViewB", "ViewC" },
            cycles[0].Select(node => node.Name).OrderBy(name => name));
    }

    [Fact]
    public async Task DiamondDependencyIsVisitedOnceWithBothConvergingEdgesRetained()
    {
        await using var index = await TestIndex.CreateAsync();
        var objects = new[]
        {
            TestIndex.Object(1, "dbo", "A", "VIEW",
                "CREATE VIEW dbo.A AS SELECT * FROM dbo.B UNION ALL SELECT * FROM dbo.C;"),
            TestIndex.Object(2, "dbo", "B", "VIEW", "CREATE VIEW dbo.B AS SELECT * FROM dbo.D;"),
            TestIndex.Object(3, "dbo", "C", "VIEW", "CREATE VIEW dbo.C AS SELECT * FROM dbo.D;"),
            TestIndex.Object(4, "dbo", "D", "VIEW", null),
        };
        await index.Builder.BuildAsync(TestIndex.Snapshot(objects));
        var root = (await index.Graph.SearchObjectsAsync("A", 10)).Single(item => item.Name == "A");

        var graph = await index.Graph.TraverseAsync(root.ObjectKey, GraphDirection.Uses, 2, 20);

        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(4, graph.Edges.Count);
        var d = Assert.Single(graph.Nodes, node => node.Object.Name == "D");
        Assert.Equal(2, d.Depth);
        Assert.Equal(2, graph.Edges.Count(edge => edge.TargetObjectKey == d.Object.ObjectKey));
    }

    [Fact]
    public async Task ExcludingUnresolvedReferencesOmitsExternalEdgesFromTraversal()
    {
        await using var index = await TestIndex.CreateAsync();
        var source = TestIndex.Object(1, "dbo", "SourceView", "VIEW",
            "CREATE VIEW dbo.SourceView AS SELECT * FROM [Link One].[Remote Db].[sales].[Order Item];");
        var snapshot = TestIndex.Snapshot([source],
            [new("Link One", "SQL Server", "MSOLEDBSQL", "remote-host", null, "Remote Db", true, false)]);
        await index.Builder.BuildAsync(snapshot);
        var sourceDto = Assert.Single(await index.Graph.SearchObjectsAsync("SourceView"));

        var graph = await index.Graph.GetDirectAsync(sourceDto.ObjectKey, GraphDirection.Uses,
            new GraphFilter(IncludeUnresolved: false));

        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task LazyExpansionHonorsFiltersAndNodeLimits()
    {
        await using var index = await TestIndex.CreateAsync();
        var objects = new[]
        {
            TestIndex.Object(1, "dbo", "RootView", "VIEW",
                "CREATE VIEW dbo.RootView AS SELECT * FROM dbo.ChildA UNION ALL SELECT * FROM other.ChildB;"),
            TestIndex.Object(2, "dbo", "ChildA", "VIEW", null),
            TestIndex.Object(3, "other", "ChildB", "VIEW", null),
        };
        await index.Builder.BuildAsync(TestIndex.Snapshot(objects));
        var root = Assert.Single(await index.Graph.SearchObjectsAsync("RootView"));

        var filtered = await index.Graph.ExpandNodeAsync(root.ObjectKey, GraphDirection.Uses,
            new GraphFilter(SchemaNames: new HashSet<string> { "dbo" }));
        var limited = await index.Graph.TraverseAsync(root.ObjectKey, GraphDirection.Uses, 2, 1);

        Assert.Equal(new[] { "RootView", "ChildA" }, filtered.Nodes.Select(node => node.Object.Name));
        Assert.True(limited.Truncated);
        Assert.Single(limited.Nodes);
    }
}
