using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Gazetteer.Core.DTOs;
using Gazetteer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Gazetteer.Infrastructure.Services;

public class ElasticsearchService : IElasticsearchService
{
    private const string IndexName = "locations";
    private const string BoundariesIndexName = "boundaries";
    private readonly ElasticsearchClient _client;
    private readonly ILogger<ElasticsearchService> _logger;

    public ElasticsearchService(ElasticsearchClient client, ILogger<ElasticsearchService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task CreateIndexAsync(CancellationToken ct = default)
    {
        var existsResponse = await _client.Indices.ExistsAsync(IndexName, ct);
        if (existsResponse.Exists)
        {
            _logger.LogInformation("Index '{Index}' already exists, skipping creation", IndexName);
            return;
        }

        var createResponse = await _client.Indices.CreateAsync(IndexName, c => c
            .Settings(s => s
                .Analysis(a => a
                    .Analyzers(an => an
                        .Custom("gazetteer_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(new[] { "lowercase", "asciifolding", "gazetteer_edge_ngram" })
                        )
                        .Custom("gazetteer_search_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(new[] { "lowercase", "asciifolding" })
                        )
                    )
                    .TokenFilters(tf => tf
                        .EdgeNGram("gazetteer_edge_ngram", eng => eng
                            .MinGram(2)
                            .MaxGram(15)
                        )
                    )
                )
            )
            .Mappings(m => m
                .Properties(p => p
                    .LongNumber(d => d.Id)
                    .Text(d => d.Name, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                        .Fields(f => f
                            .Keyword(k => k.Name!.Suffix("raw"))
                        )
                    )
                    .Text(d => d.NameEn, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Text(d => d.AlternateNames, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Keyword(d => d.LocationType)
                    .Keyword(d => d.CountryCode)
                    .Keyword(d => d.PostalCode)
                    .FloatNumber(d => d.Latitude)
                    .FloatNumber(d => d.Longitude)
                    .GeoPoint(d => d.GeoPoint)
                    .LongNumber(d => d.Population)
                    .Text(d => d.ParentChain)
                    .Boolean(d => d.HasGeometry)
                )
            ),
            ct
        );

        if (!createResponse.IsValidResponse)
        {
            _logger.LogError("Failed to create index: {Error}", createResponse.DebugInformation);
            throw new InvalidOperationException($"Failed to create Elasticsearch index: {createResponse.DebugInformation}");
        }

        _logger.LogInformation("Created Elasticsearch index '{Index}'", IndexName);
    }

    public async Task IndexLocationAsync(LocationIndexDocument document, CancellationToken ct = default)
    {
        var response = await _client.IndexAsync(document, idx => idx
            .Index(IndexName)
            .Id(document.Id.ToString()),
            ct
        );

        if (!response.IsValidResponse)
            _logger.LogWarning("Failed to index document {Id}: {Error}", document.Id, response.DebugInformation);
    }

    public async Task BulkIndexAsync(IEnumerable<LocationIndexDocument> documents, CancellationToken ct = default)
    {
        var batch = documents.ToList();
        if (batch.Count == 0) return;

        var response = await _client.BulkAsync(b => b
            .Index(IndexName)
            .IndexMany(batch),
            ct
        );

        if (response.Errors)
        {
            var errorCount = response.ItemsWithErrors.Count();
            _logger.LogWarning("Bulk index had {ErrorCount} errors out of {Total}", errorCount, batch.Count);
        }
        else
        {
            _logger.LogDebug("Bulk indexed {Count} documents", batch.Count);
        }
    }

    public async Task<List<LocationSearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var response = await _client.SearchAsync<LocationIndexDocument>(s =>
        {
            s.Index(IndexName)
             .Size(request.Limit)
             .Query(q => BuildQuery(q, request));

            return s;
        }, ct);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Search failed: {Error}", response.DebugInformation);
            return new List<LocationSearchHit>();
        }

        return response.Hits
            .Where(h => h.Source != null)
            .Select(h => new LocationSearchHit
            {
                Id = h.Source!.Id,
                Score = h.Score ?? 0
            })
            .ToList();
    }

    public async Task DeleteIndexAsync(CancellationToken ct = default)
    {
        await _client.Indices.DeleteAsync(IndexName, ct);
        _logger.LogInformation("Deleted Elasticsearch index '{Index}'", IndexName);
    }

    // ---- Boundaries index ----

    public async Task CreateBoundariesIndexAsync(CancellationToken ct = default)
    {
        var existsResponse = await _client.Indices.ExistsAsync(BoundariesIndexName, ct);
        if (existsResponse.Exists)
        {
            _logger.LogInformation("Index '{Index}' already exists, skipping creation", BoundariesIndexName);
            return;
        }

        var createResponse = await _client.Indices.CreateAsync(BoundariesIndexName, c => c
            .Settings(s => s
                .Analysis(a => a
                    .Analyzers(an => an
                        .Custom("gazetteer_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(new[] { "lowercase", "asciifolding", "boundary_edge_ngram" })
                        )
                        .Custom("gazetteer_search_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(new[] { "lowercase", "asciifolding" })
                        )
                    )
                    .TokenFilters(tf => tf
                        .EdgeNGram("boundary_edge_ngram", eng => eng
                            .MinGram(2)
                            .MaxGram(15)
                        )
                    )
                )
            )
            .Mappings(m => m
                .Properties<BoundaryIndexDocument>(p => p
                    .LongNumber(d => d.Id)
                    .LongNumber(d => d.OsmId)
                    .Text(d => d.Name, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                        .Fields(f => f
                            .Keyword(k => k.Name!.Suffix("raw"))
                        )
                    )
                    .Text(d => d.NameEn, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Keyword(d => d.LocationType)
                    .Keyword(d => d.CountryCode)
                    .IntegerNumber(d => d.AdminLevel)
                    .FloatNumber(d => d.Latitude)
                    .FloatNumber(d => d.Longitude)
                    .LongNumber(d => d.Population)
                    .Text(d => d.ParentChain)
                    .GeoShape(d => d.Boundary)
                )
            ),
            ct
        );

        if (!createResponse.IsValidResponse)
        {
            _logger.LogError("Failed to create boundaries index: {Error}", createResponse.DebugInformation);
            throw new InvalidOperationException($"Failed to create Elasticsearch boundaries index: {createResponse.DebugInformation}");
        }

        _logger.LogInformation("Created Elasticsearch index '{Index}'", BoundariesIndexName);
    }

    public async Task BulkIndexBoundariesAsync(IEnumerable<BoundaryIndexDocument> documents, CancellationToken ct = default)
    {
        var batch = documents.ToList();
        if (batch.Count == 0) return;

        var response = await _client.BulkAsync(b => b
            .Index(BoundariesIndexName)
            .IndexMany(batch),
            ct
        );

        if (response.Errors)
        {
            var errorCount = response.ItemsWithErrors.Count();
            _logger.LogWarning("Boundaries bulk index had {ErrorCount} errors out of {Total}", errorCount, batch.Count);
        }
        else
        {
            _logger.LogDebug("Bulk indexed {Count} boundary documents", batch.Count);
        }
    }

    public async Task DeleteBoundariesIndexAsync(CancellationToken ct = default)
    {
        await _client.Indices.DeleteAsync(BoundariesIndexName, ct);
        _logger.LogInformation("Deleted Elasticsearch index '{Index}'", BoundariesIndexName);
    }

    private static void BuildQuery(QueryDescriptor<LocationIndexDocument> q, SearchRequest request)
    {
        q.Bool(b =>
        {
            b.Must(must =>
            {
                must.Bool(innerBool =>
                {
                    innerBool.Should(
                        should => should.MultiMatch(mm => mm
                            .Query(request.Query)
                            .Fields(new[] { "name^3", "nameEn^2", "alternateNames", "postalCode^4", "parentChain" })
                            .Fuzziness(new Fuzziness("AUTO"))
                            .Type(TextQueryType.BestFields)
                        ),
                        should => should.MatchPhrasePrefix(mp => mp
                            .Field(f => f.Name)
                            .Query(request.Query)
                            .Boost(5)
                        ),
                        should => should.Term(t => t
                            .Field(f => f.PostalCode)
                            .Value(request.Query.ToUpperInvariant())
                            .Boost(10)
                        )
                    );
                    innerBool.MinimumShouldMatch(1);
                });
            });

            var filters = new List<Action<QueryDescriptor<LocationIndexDocument>>>();

            if (!string.IsNullOrEmpty(request.CountryCode))
            {
                filters.Add(f => f.Term(t => t
                    .Field(d => d.CountryCode)
                    .Value(request.CountryCode.ToUpperInvariant())
                ));
            }

            if (request.LocationType.HasValue)
            {
                filters.Add(f => f.Term(t => t
                    .Field(d => d.LocationType)
                    .Value(request.LocationType.Value.ToString())
                ));
            }

            if (filters.Count > 0)
                b.Filter(filters.ToArray());

            return b;
        });
    }
}

public class GeoPointField
{
    public double Lat { get; set; }
    public double Lon { get; set; }
}
