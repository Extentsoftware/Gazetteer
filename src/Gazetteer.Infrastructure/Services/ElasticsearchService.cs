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
    private readonly ElasticsearchClient _client;
    private readonly ILogger<ElasticsearchService> _logger;
    private const string LocationsIndexName = "locations";
    private const string BoundariesIndexName = "boundaries";

    public ElasticsearchService(ElasticsearchClient client, ILogger<ElasticsearchService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task CreateIndexAsync(CancellationToken ct = default)
    {
        var existsResponse = await _client.Indices.ExistsAsync(LocationsIndexName, ct);
        if (existsResponse.Exists) return;

        var response = await _client.Indices.CreateAsync(LocationsIndexName, c => c
            .Settings(s => s
                .Analysis(a => a
                    .Analyzers(an => an
                        .Custom("gazetteer_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(["lowercase", "asciifolding", "edge_ngram_filter"])
                        )
                        .Custom("gazetteer_search_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(["lowercase", "asciifolding"])
                        )
                    )
                    .TokenFilters(tf => tf
                        .EdgeNGram("edge_ngram_filter", eng => eng
                            .MinGram(2)
                            .MaxGram(15)
                        )
                    )
                )
            )
            .Mappings(m => m
                .Properties<LocationIndexDocument>(p => p
                    .IntegerNumber(d => d.Id)
                    .Text(d => d.Name, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Text(d => d.NameEn, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Text(d => d.LocalName, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Text(d => d.AlternateNames, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Keyword(d => d.LocationType)
                    .Keyword(d => d.CountryCode)
                    .GeoPoint(d => d.Lat)
                    .Boolean(d => d.HasGeometry)
                    .Keyword(d => d.PostalCode, k => k
                        .Fields(f => f
                            .Text("text", t => t
                                .Analyzer("gazetteer_analyzer")
                                .SearchAnalyzer("gazetteer_search_analyzer")
                            )
                        )
                    )
                    .IntegerNumber(d => d.Population)
                    .Text(d => d.ParentName, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                )
            ), ct);

        if (!response.IsValidResponse)
            _logger.LogError("Failed to create locations index: {Error}", response.DebugInformation);
    }

    public async Task IndexLocationAsync(LocationIndexDocument document, CancellationToken ct = default)
    {
        await _client.IndexAsync(document, LocationsIndexName, ct);
    }

    public async Task BulkIndexAsync(IEnumerable<LocationIndexDocument> documents, CancellationToken ct = default)
    {
        var docs = documents.ToList();
        if (docs.Count == 0) return;

        foreach (var batch in docs.Chunk(1000))
        {
            var response = await _client.BulkAsync(b =>
            {
                b.Index(LocationsIndexName);
                foreach (var doc in batch)
                    b.Index<LocationIndexDocument>(i => i.Document(doc).Id(doc.Id.ToString()));
                return b;
            }, ct);

            if (response.Errors)
                _logger.LogWarning("Bulk index had errors: {Count} failures", response.ItemsWithErrors.Count());
        }
    }

    public async Task<List<LocationSearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var mustClauses = new List<Query>();
        var shouldClauses = new List<Query>();

        shouldClauses.Add(new MultiMatchQuery
        {
            Query = request.Query,
            Fields = new[] { "name^3", "nameEn^2", "localName", "alternateNames", "parentName" },
            Fuzziness = new Fuzziness("AUTO"),
            Type = TextQueryType.BestFields
        });

        shouldClauses.Add(new MultiMatchQuery
        {
            Query = request.Query,
            Fields = new[] { "name^5" },
            Type = TextQueryType.PhrasePrefix
        });

        shouldClauses.Add(new TermQuery("postalCode") { Value = request.Query, Boost = 10 });

        shouldClauses.Add(new MatchQuery("postalCode.text")
        {
            Query = request.Query,
            Boost = 4
        });

        if (!string.IsNullOrEmpty(request.CountryCode))
        {
            mustClauses.Add(new TermQuery("countryCode") { Value = request.CountryCode.ToUpperInvariant() });
        }

        if (request.LocationType.HasValue)
        {
            mustClauses.Add(new TermQuery("locationType") { Value = request.LocationType.Value.ToString() });
        }

        var boolQuery = new BoolQuery
        {
            Must = mustClauses.Count > 0 ? mustClauses : null,
            Should = shouldClauses,
            MinimumShouldMatch = 1
        };

        var response = await _client.SearchAsync<LocationIndexDocument>(s => s
            .Index(LocationsIndexName)
            .Query(boolQuery)
            .Size(request.Limit), ct);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Search failed: {Error}", response.DebugInformation);
            return [];
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
        var exists = await _client.Indices.ExistsAsync(LocationsIndexName, ct);
        if (exists.Exists)
            await _client.Indices.DeleteAsync(LocationsIndexName, ct);
    }

    public async Task CreateBoundariesIndexAsync(CancellationToken ct = default)
    {
        var existsResponse = await _client.Indices.ExistsAsync(BoundariesIndexName, ct);
        if (existsResponse.Exists) return;

        var response = await _client.Indices.CreateAsync(BoundariesIndexName, c => c
            .Settings(s => s
                .Analysis(a => a
                    .Analyzers(an => an
                        .Custom("gazetteer_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(["lowercase", "asciifolding", "edge_ngram_filter"])
                        )
                        .Custom("gazetteer_search_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(["lowercase", "asciifolding"])
                        )
                    )
                    .TokenFilters(tf => tf
                        .EdgeNGram("edge_ngram_filter", eng => eng
                            .MinGram(2)
                            .MaxGram(15)
                        )
                    )
                )
            )
            .Mappings(m => m
                .Properties<BoundaryIndexDocument>(p => p
                    .IntegerNumber(d => d.Id)
                    .LongNumber(d => d.OsmId)
                    .Text(d => d.Name, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Text(d => d.NameEn, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Keyword(d => d.LocationType)
                    .Keyword(d => d.CountryCode)
                    .IntegerNumber(d => d.AdminLevel)
                    .Object(d => d.Boundary, o => o
                        .Enabled(false)
                    )
                )
            ), ct);

        if (!response.IsValidResponse)
            _logger.LogError("Failed to create boundaries index: {Error}", response.DebugInformation);
    }

    public async Task BulkIndexBoundariesAsync(IEnumerable<BoundaryIndexDocument> documents, CancellationToken ct = default)
    {
        var docs = documents.ToList();
        if (docs.Count == 0) return;

        foreach (var batch in docs.Chunk(500))
        {
            var response = await _client.BulkAsync(b =>
            {
                b.Index(BoundariesIndexName);
                foreach (var doc in batch)
                    b.Index<BoundaryIndexDocument>(i => i.Document(doc).Id(doc.Id.ToString()));
                return b;
            }, ct);

            if (response.Errors)
                _logger.LogWarning("Bulk boundary index had errors: {Count} failures", response.ItemsWithErrors.Count());
        }
    }

    public async Task DeleteBoundariesIndexAsync(CancellationToken ct = default)
    {
        var exists = await _client.Indices.ExistsAsync(BoundariesIndexName, ct);
        if (exists.Exists)
            await _client.Indices.DeleteAsync(BoundariesIndexName, ct);
    }
}
