using Microsoft.Data.Sqlite;
using System.Globalization;

namespace SqlPhanos.DependencyIndex;

public sealed class DependencyGraphQueryService
{
    private readonly DependencyIndexStore _store;

    public DependencyGraphQueryService(DependencyIndexStore store) => _store = store;

    public async Task<IReadOnlyList<ObjectSearchDto>> SearchObjectsAsync(
        string? searchText = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await _store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.object_key,s.server_id,s.display_name,d.database_id,d.name,o.schema_name,
                   o.object_name,o.type_desc,o.parent_object_key,p.object_name,o.is_encrypted,
                   o.analysis_status,o.has_dynamic_sql,o.is_deleted
            FROM schema_objects o
            JOIN databases d ON d.database_id=o.database_id
            JOIN server_endpoints s ON s.server_id=d.server_id
            LEFT JOIN schema_objects p ON p.object_key=o.parent_object_key
            WHERE o.is_deleted=0 AND
              ($search IS NULL OR o.object_name LIKE $pattern ESCAPE '\' COLLATE NOCASE
               OR o.schema_name LIKE $pattern ESCAPE '\' COLLATE NOCASE
               OR d.name LIKE $pattern ESCAPE '\' COLLATE NOCASE)
            ORDER BY s.display_name,d.name,o.schema_name,o.object_name,o.object_key LIMIT $limit;
            """;
        Add(command, "$search", string.IsNullOrWhiteSpace(searchText) ? null : searchText);
        Add(command, "$pattern", string.IsNullOrWhiteSpace(searchText) ? null : $"%{EscapeLike(searchText)}%");
        Add(command, "$limit", limit);
        var result = new List<ObjectSearchDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadObject(reader));
        return result;
    }

    public async Task<IReadOnlyList<ObjectSearchDto>> FindObjectsExactAsync(
        long serverId, string database, string schema, string name, string? type = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await _store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.object_key,s.server_id,s.display_name,d.database_id,d.name,o.schema_name,
                   o.object_name,o.type_desc,o.parent_object_key,p.object_name,o.is_encrypted,
                   o.analysis_status,o.has_dynamic_sql,o.is_deleted
            FROM schema_objects o
            JOIN databases d ON d.database_id=o.database_id
            JOIN server_endpoints s ON s.server_id=d.server_id
            LEFT JOIN schema_objects p ON p.object_key=o.parent_object_key
            WHERE s.server_id=$server AND d.name=$database COLLATE NOCASE
              AND o.schema_name=$schema COLLATE NOCASE
              AND o.object_name=$name COLLATE NOCASE AND o.is_deleted=0
            ORDER BY o.type_desc,o.object_key;
            """;
        Add(command, "$server", serverId);
        Add(command, "$database", database);
        Add(command, "$schema", schema);
        Add(command, "$name", name);
        var result = new List<ObjectSearchDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = ReadObject(reader);
            if (IsTypeCompatible(type, item.Type))
                result.Add(item);
        }
        return result;
    }

    public async Task<ObjectSearchDto?> FindObjectAsync(
        long serverId, string database, string schema, string name, string? type = null,
        CancellationToken cancellationToken = default)
    {
        var matches = await FindObjectsExactAsync(
            serverId, database, schema, name, type, cancellationToken);
        return matches.Count == 1 ? matches[0] : null;
    }

    public Task<GraphResult> GetDirectAsync(long objectKey, GraphDirection direction,
        GraphFilter? filter = null, int nodeLimit = 250, CancellationToken cancellationToken = default)
        => TraverseAsync(objectKey, direction, 1, nodeLimit, filter, cancellationToken);

    public Task<GraphResult> ExpandNodeAsync(long objectKey, GraphDirection direction,
        GraphFilter? filter = null, int nodeLimit = 250, CancellationToken cancellationToken = default)
        => GetDirectAsync(objectKey, direction, filter, nodeLimit, cancellationToken);

    // Bounded, level-by-level traversal: each depth level issues one indexed query per direction
    // (WHERE source_object_key/target_object_key IN (current frontier)) instead of loading every
    // object and edge in the index up front. This keeps the SQLite work proportional to the
    // requested depth/node limit rather than to total index size, and only fetches occurrences
    // for edges that actually end up in the result instead of every edge in the store.
    public async Task<GraphResult> TraverseAsync(
        long rootObjectKey, GraphDirection direction, int maxDepth = 3, int nodeLimit = 500,
        GraphFilter? filter = null, CancellationToken cancellationToken = default)
    {
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (nodeLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(nodeLimit));
        filter ??= new GraphFilter();

        await using var connection = await _store.OpenConnectionAsync(cancellationToken);
        var rootObject = await LoadObjectByKeyAsync(connection, rootObjectKey, cancellationToken);
        if (rootObject is null)
            return new GraphResult([], [], false, EmptyCoverage(), await GetFreshnessAsync(cancellationToken));

        var objects = new Dictionary<long, ObjectSearchDto> { [rootObjectKey] = rootObject };
        var depths = new Dictionary<long, int> { [rootObjectKey] = 0 };
        var selectedEdges = new Dictionary<long, DependencyEdgeRecord>();
        var frontier = new List<long> { rootObjectKey };
        var truncated = false;

        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layer = await LoadAdjacentEdgesAsync(connection, frontier, direction, cancellationToken);
            var nextFrontier = new List<long>();
            foreach (var adjacency in layer)
            {
                if (!filter.IncludeDriDependencies && IsDriEvidence(adjacency.Edge.EvidenceKind))
                    continue;
                if (adjacency.OtherKey is not { } otherKey)
                {
                    if (filter.IncludeUnresolved)
                        selectedEdges[adjacency.Edge.Id] = adjacency.Edge;
                    continue;
                }
                selectedEdges[adjacency.Edge.Id] = adjacency.Edge;
                if (depths.ContainsKey(otherKey))
                    continue;
                if (adjacency.OtherObject is not { } candidate || !Matches(candidate, filter))
                    continue;
                if (depths.Count >= nodeLimit)
                {
                    truncated = true;
                    continue;
                }
                depths[otherKey] = depth + 1;
                objects[otherKey] = candidate;
                nextFrontier.Add(otherKey);
            }
            frontier = nextFrontier;
        }

        var nodes = depths.OrderBy(pair => pair.Value)
            .ThenBy(pair => objects[pair.Key].Database, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => objects[pair.Key].Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => objects[pair.Key].Name, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new GraphNode(objects[pair.Key], pair.Value)).ToArray();
        var nodeKeys = depths.Keys.ToHashSet();
        var finalEdges = selectedEdges.Values
            .Where(edge => nodeKeys.Contains(edge.SourceObjectKey) &&
                           (edge.TargetObjectKey is null || nodeKeys.Contains(edge.TargetObjectKey.Value)))
            .OrderBy(edge => edge.SourceObjectKey).ThenBy(edge => edge.TargetObjectKey).ThenBy(edge => edge.Id)
            .ToArray();
        var occurrencesByEdge = await LoadOccurrencesAsync(
            connection, finalEdges.Select(edge => edge.Id).ToArray(), cancellationToken);
        var edges = finalEdges
            .Select(edge => new GraphEdge(
                edge with { Occurrences = occurrencesByEdge.GetValueOrDefault(edge.Id, []) },
                edge.SourceObjectKey, edge.TargetObjectKey))
            .ToArray();
        return new GraphResult(nodes, edges, truncated, Coverage(nodes.Select(n => n.Object), edges.Select(e => e.Edge)),
            await GetFreshnessAsync(cancellationToken));
    }

    private const int InClauseChunkSize = 400;

    private readonly record struct AdjacentEdge(
        long CurrentKey, DependencyEdgeRecord Edge, long? OtherKey, ObjectSearchDto? OtherObject);

    private static async Task<ObjectSearchDto?> LoadObjectByKeyAsync(
        SqliteConnection connection, long objectKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.object_key,s.server_id,s.display_name,d.database_id,d.name,o.schema_name,
                   o.object_name,o.type_desc,o.parent_object_key,p.object_name,o.is_encrypted,
                   o.analysis_status,o.has_dynamic_sql,o.is_deleted
            FROM schema_objects o
            JOIN databases d ON d.database_id=o.database_id
            JOIN server_endpoints s ON s.server_id=d.server_id
            LEFT JOIN schema_objects p ON p.object_key=o.parent_object_key
            WHERE o.object_key=$key AND o.is_deleted=0;
            """;
        Add(command, "$key", objectKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadObject(reader) : null;
    }

    private static async Task<List<AdjacentEdge>> LoadAdjacentEdgesAsync(
        SqliteConnection connection, IReadOnlyList<long> frontier, GraphDirection direction,
        CancellationToken cancellationToken)
    {
        var result = new List<AdjacentEdge>();
        foreach (var chunk in frontier.Chunk(InClauseChunkSize))
        {
            if (direction is GraphDirection.Uses or GraphDirection.Both)
                await LoadLayerAsync(connection, chunk, forward: true, result, cancellationToken);
            if (direction is GraphDirection.UsedBy or GraphDirection.Both)
                await LoadLayerAsync(connection, chunk, forward: false, result, cancellationToken);
        }
        return result;
    }

    private static async Task LoadLayerAsync(
        SqliteConnection connection, IReadOnlyList<long> chunk, bool forward,
        List<AdjacentEdge> result, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var filterColumn = forward ? "source_object_key" : "target_object_key";
        var joinColumn = forward ? "e.target_object_key" : "e.source_object_key";
        var placeholders = AddInClauseParams(command, "$k", chunk);
        command.CommandText = $"""
            SELECT e.edge_id,e.source_object_key,e.target_object_key,e.reference_kind,e.classification,
              e.resolution_status,e.target_server_id,e.target_linked_server_id,e.target_database_name,
              e.target_schema_name,e.target_object_name,e.raw_server_part,e.raw_database_part,
              e.raw_schema_part,e.raw_object_part,e.part_count,e.evidence_kind,e.first_seen_scan_id,
              e.last_seen_scan_id,
              o.object_key,s.server_id,s.display_name,o.database_id,d.name,o.schema_name,o.object_name,
              o.type_desc,o.parent_object_key,p.object_name,o.is_encrypted,o.analysis_status,
              o.has_dynamic_sql,o.is_deleted
            FROM dependency_edges e
            LEFT JOIN schema_objects o ON o.object_key={joinColumn} AND o.is_deleted=0
            LEFT JOIN databases d ON d.database_id=o.database_id
            LEFT JOIN server_endpoints s ON s.server_id=d.server_id
            LEFT JOIN schema_objects p ON p.object_key=o.parent_object_key
            WHERE e.{filterColumn} IN ({placeholders});
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var edge = ReadEdge(reader);
            var currentKey = forward ? edge.SourceObjectKey : edge.TargetObjectKey!.Value;
            var otherKey = forward ? edge.TargetObjectKey : edge.SourceObjectKey;
            var otherObject = ReadOptionalObject(reader, 19);
            result.Add(new AdjacentEdge(currentKey, edge, otherKey, otherObject));
        }
    }

    private static async Task<Dictionary<long, List<DependencyOccurrence>>> LoadOccurrencesAsync(
        SqliteConnection connection, IReadOnlyList<long> edgeIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, List<DependencyOccurrence>>();
        foreach (var chunk in edgeIds.Chunk(InClauseChunkSize))
        {
            await using var command = connection.CreateCommand();
            var placeholders = AddInClauseParams(command, "$e", chunk);
            command.CommandText = $"""
                SELECT edge_id,start_offset,length,start_line,start_column,reference_text
                FROM dependency_occurrences WHERE edge_id IN ({placeholders}) ORDER BY edge_id,start_offset;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var edgeId = reader.GetInt64(0);
                var occurrence = new DependencyOccurrence(reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5));
                if (!result.TryGetValue(edgeId, out var list))
                    result[edgeId] = list = [];
                list.Add(occurrence);
            }
        }
        return result;
    }

    private static string AddInClauseParams(SqliteCommand command, string prefix, IReadOnlyList<long> values)
    {
        var placeholders = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"{prefix}{i}";
            placeholders[i] = name;
            Add(command, name, values[i]);
        }
        return string.Join(',', placeholders);
    }

    private static DependencyEdgeRecord ReadEdge(SqliteDataReader reader)
        => new(reader.GetInt64(0), reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3),
            Enum.Parse<ReferenceClassification>(reader.GetString(4)),
            Enum.Parse<ResolutionStatus>(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            Text(reader, 8), Text(reader, 9), reader.GetString(10), Text(reader, 11),
            Text(reader, 12), Text(reader, 13), reader.GetString(14), reader.GetInt32(15),
            Enum.Parse<EvidenceKind>(reader.GetString(16)), reader.GetInt64(17), reader.GetInt64(18), []);

    private static ObjectSearchDto? ReadOptionalObject(SqliteDataReader reader, int baseOrdinal)
    {
        if (reader.IsDBNull(baseOrdinal))
            return null;
        return new ObjectSearchDto(
            reader.GetInt64(baseOrdinal), reader.GetInt64(baseOrdinal + 1), reader.GetString(baseOrdinal + 2),
            reader.GetInt64(baseOrdinal + 3), reader.GetString(baseOrdinal + 4), reader.GetString(baseOrdinal + 5),
            reader.GetString(baseOrdinal + 6), reader.GetString(baseOrdinal + 7),
            reader.IsDBNull(baseOrdinal + 8) ? null : reader.GetInt64(baseOrdinal + 8),
            Text(reader, baseOrdinal + 9), reader.GetBoolean(baseOrdinal + 10),
            Enum.Parse<AnalysisStatus>(reader.GetString(baseOrdinal + 11)), reader.GetBoolean(baseOrdinal + 12),
            reader.GetBoolean(baseOrdinal + 13));
    }

    public async Task<ShortestPathResult?> FindShortestPathAsync(
        long startObjectKey, long endObjectKey, GraphDirection direction = GraphDirection.Uses,
        int maxDepth = 12, int nodeLimit = 5000, CancellationToken cancellationToken = default)
    {
        var graph = await LoadGraphAsync(cancellationToken);
        if (!graph.Objects.ContainsKey(startObjectKey) || !graph.Objects.ContainsKey(endObjectKey))
            return null;
        var queue = new Queue<(long Key, int Depth)>();
        var visited = new HashSet<long> { startObjectKey };
        var previous = new Dictionary<long, (long Parent, DependencyEdgeRecord Edge)>();
        queue.Enqueue((startObjectKey, 0));

        while (queue.Count > 0 && visited.Count <= nodeLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (key, depth) = queue.Dequeue();
            if (key == endObjectKey)
                break;
            if (depth >= maxDepth)
                continue;
            foreach (var edge in Adjacent(graph, key, direction))
            {
                var next = AdjacentObject(edge, key, direction);
                if (next is null || visited.Contains(next.Value))
                    continue;
                if (visited.Count >= nodeLimit)
                    return null;
                visited.Add(next.Value);
                previous[next.Value] = (key, edge);
                queue.Enqueue((next.Value, depth + 1));
            }
        }
        if (!visited.Contains(endObjectKey))
            return null;

        var nodeKeys = new List<long> { endObjectKey };
        var edges = new List<DependencyEdgeRecord>();
        var current = endObjectKey;
        while (current != startObjectKey)
        {
            var step = previous[current];
            edges.Add(step.Edge);
            current = step.Parent;
            nodeKeys.Add(current);
        }
        nodeKeys.Reverse();
        edges.Reverse();
        return new ShortestPathResult(nodeKeys.Select(key => graph.Objects[key]).ToArray(), edges);
    }

    public async Task<IReadOnlyList<IReadOnlyList<ObjectSearchDto>>> FindCyclesAsync(
        int nodeLimit = 10000, CancellationToken cancellationToken = default)
    {
        var graph = await LoadGraphAsync(cancellationToken);
        var adjacency = graph.Edges.Where(edge => edge.TargetObjectKey is not null)
            .GroupBy(edge => edge.SourceObjectKey)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.TargetObjectKey!.Value).Distinct().ToArray());
        var index = 0;
        var stack = new Stack<long>();
        var onStack = new HashSet<long>();
        var indices = new Dictionary<long, int>();
        var low = new Dictionary<long, int>();
        var result = new List<IReadOnlyList<ObjectSearchDto>>();

        void Visit(long node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            indices[node] = low[node] = index++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var next in adjacency.GetValueOrDefault(node, []))
            {
                if (!indices.ContainsKey(next))
                {
                    Visit(next);
                    low[node] = Math.Min(low[node], low[next]);
                }
                else if (onStack.Contains(next))
                    low[node] = Math.Min(low[node], indices[next]);
            }
            if (low[node] != indices[node])
                return;
            var component = new List<long>();
            long current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            } while (current != node);
            var selfLoop = component.Count == 1 &&
                           adjacency.GetValueOrDefault(component[0], []).Contains(component[0]);
            if (component.Count > 1 || selfLoop)
                result.Add(component.Select(key => graph.Objects[key])
                    .OrderBy(item => item.Database).ThenBy(item => item.Schema).ThenBy(item => item.Name).ToArray());
        }

        foreach (var node in graph.Objects.Keys.Take(nodeLimit))
            if (!indices.ContainsKey(node))
                Visit(node);
        return result;
    }

    public async Task<FreshnessSummary> GetFreshnessAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH latest AS (SELECT outcome,inaccessible_database_count FROM scan_runs ORDER BY scan_id DESC LIMIT 1)
            SELECT (SELECT completed_utc FROM scan_runs WHERE outcome='Succeeded'
                    ORDER BY scan_id DESC LIMIT 1),
                   CASE WHEN latest.outcome='Failed' THEN 1 ELSE 0 END,
                   latest.inaccessible_database_count
            FROM latest;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new FreshnessSummary(null, true, 0, 0);
        var last = reader.IsDBNull(0) ? (DateTimeOffset?)null :
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
        return new FreshnessSummary(last, last is null || DateTimeOffset.UtcNow - last > TimeSpan.FromDays(1),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.GetInt32(2));
    }

    private async Task<PersistedGraph> LoadGraphAsync(CancellationToken token)
    {
        var objects = (await SearchObjectsAsync(limit: int.MaxValue, cancellationToken: token))
            .ToDictionary(item => item.ObjectKey);
        var edges = new List<DependencyEdgeRecord>();
        await using var connection = await _store.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT edge_id,source_object_key,target_object_key,reference_kind,classification,
              resolution_status,target_server_id,target_linked_server_id,target_database_name,
              target_schema_name,target_object_name,raw_server_part,raw_database_part,raw_schema_part,
              raw_object_part,part_count,evidence_kind,first_seen_scan_id,last_seen_scan_id
            FROM dependency_edges ORDER BY edge_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            edges.Add(new DependencyEdgeRecord(reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3),
                Enum.Parse<ReferenceClassification>(reader.GetString(4)),
                Enum.Parse<ResolutionStatus>(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Text(reader, 8), Text(reader, 9), reader.GetString(10), Text(reader, 11),
                Text(reader, 12), Text(reader, 13), reader.GetString(14), reader.GetInt32(15),
                Enum.Parse<EvidenceKind>(reader.GetString(16)), reader.GetInt64(17), reader.GetInt64(18), []));
        await reader.CloseAsync();

        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            await using var occurrences = connection.CreateCommand();
            occurrences.CommandText = """
                SELECT start_offset,length,start_line,start_column,reference_text
                FROM dependency_occurrences WHERE edge_id=$edge ORDER BY start_offset;
                """;
            Add(occurrences, "$edge", edge.Id);
            var values = new List<DependencyOccurrence>();
            await using var occurrenceReader = await occurrences.ExecuteReaderAsync(token);
            while (await occurrenceReader.ReadAsync(token))
                values.Add(new DependencyOccurrence(occurrenceReader.GetInt32(0), occurrenceReader.GetInt32(1),
                    occurrenceReader.GetInt32(2), occurrenceReader.GetInt32(3), occurrenceReader.GetString(4)));
            edges[index] = edge with { Occurrences = values };
        }
        return new PersistedGraph(objects, edges);
    }

    private static IEnumerable<DependencyEdgeRecord> Adjacent(
        PersistedGraph graph, long key, GraphDirection direction)
    {
        if (direction is GraphDirection.Uses or GraphDirection.Both)
            foreach (var edge in graph.Edges.Where(edge => edge.SourceObjectKey == key))
                yield return edge;
        if (direction is GraphDirection.UsedBy or GraphDirection.Both)
            foreach (var edge in graph.Edges.Where(edge => edge.TargetObjectKey == key))
                yield return edge;
    }

    private static long? AdjacentObject(DependencyEdgeRecord edge, long current, GraphDirection direction)
    {
        if (edge.SourceObjectKey == current && direction is GraphDirection.Uses or GraphDirection.Both)
            return edge.TargetObjectKey;
        if (edge.TargetObjectKey == current && direction is GraphDirection.UsedBy or GraphDirection.Both)
            return edge.SourceObjectKey;
        return null;
    }

    private static bool Matches(ObjectSearchDto item, GraphFilter filter)
        => (filter.DatabaseNames is null || Contains(filter.DatabaseNames, item.Database)) &&
           (filter.SchemaNames is null || Contains(filter.SchemaNames, item.Schema)) &&
           (filter.TypeDescriptions is null || Contains(filter.TypeDescriptions, item.Type)) &&
           (filter.IncludeDriDependencies || item.Type is not ("CHECK_CONSTRAINT" or "DEFAULT_CONSTRAINT"));

    private static bool IsDriEvidence(EvidenceKind kind) =>
        kind is EvidenceKind.ForeignKey or EvidenceKind.Constraint;

    private static bool Contains(IReadOnlySet<string> values, string value)
        => values.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

    private static bool IsTypeCompatible(string? requestedType, string actualType)
    {
        if (string.IsNullOrWhiteSpace(requestedType))
            return true;
        var requested = NormalizeType(requestedType);
        var actual = NormalizeType(actualType);
        if (requested == actual)
            return true;
        return requested switch
        {
            "TABLE" => actual.Contains("TABLE", StringComparison.Ordinal) &&
                       !actual.Contains("TABLETYPE", StringComparison.Ordinal),
            "VIEW" => actual.Contains("VIEW", StringComparison.Ordinal),
            "PROCEDURE" or "STOREDPROCEDURE" =>
                actual.Contains("PROCEDURE", StringComparison.Ordinal),
            "FUNCTION" => actual.Contains("FUNCTION", StringComparison.Ordinal),
            "TRIGGER" => actual.Contains("TRIGGER", StringComparison.Ordinal),
            "SYNONYM" => actual.Contains("SYNONYM", StringComparison.Ordinal),
            "TYPE" or "USERTYPE" => actual.Contains("TYPE", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string NormalizeType(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static CoverageSummary Coverage(
        IEnumerable<ObjectSearchDto> objects, IEnumerable<DependencyEdgeRecord> edges)
    {
        var array = objects.ToArray();
        var edgeArray = edges.ToArray();
        return new CoverageSummary(
            array.Count(item => item.AnalysisStatus == AnalysisStatus.Complete),
            array.Count(item => item.AnalysisStatus == AnalysisStatus.NoDefinition),
            array.Count(item => item.Encrypted),
            array.Count(item => item.AnalysisStatus == AnalysisStatus.ParseError),
            array.Count(item => item.HasDynamicSql),
            array.Count(item => item.AnalysisStatus == AnalysisStatus.Inaccessible),
            edgeArray.Count(item => item.ResolutionStatus is ResolutionStatus.NotFound or ResolutionStatus.ExternalUnmapped),
            edgeArray.Count(item => item.ResolutionStatus == ResolutionStatus.Ambiguous));
    }

    private static CoverageSummary EmptyCoverage() => new(0, 0, 0, 0, 0, 0, 0, 0);

    private static ObjectSearchDto ReadObject(SqliteDataReader reader)
        => new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8), Text(reader, 9), reader.GetBoolean(10),
            Enum.Parse<AnalysisStatus>(reader.GetString(11)), reader.GetBoolean(12), reader.GetBoolean(13));

    private static string? Text(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string EscapeLike(string value)
        => value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    private sealed record PersistedGraph(
        IReadOnlyDictionary<long, ObjectSearchDto> Objects, IReadOnlyList<DependencyEdgeRecord> Edges);
}
