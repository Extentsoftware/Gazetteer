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
                            .Filter(["lowercase", "asciifolding", "gazetteer_edge_ngram"])
                        )
                        .Custom("gazetteer_search_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(["lowercase", "asciifolding"])
                        )
                    )
                    .TokenFilters(tf => tf
                        .EdgeNGram("gazetteer_edge_ngram", eng => eng
                            .MinGram(2)
                            .MaxGram(15)
                        )
                    )
                    .Normalizers(n => n
                        .Custom("lowercase_normalizer", cn => cn
                            .Filter(["lowercase", "asciifolding"])
                        )
                    )
                )
            )
            .Mappings(m => m
                .Properties<LocationIndexDocument>(p => p
                    .LongNumber(d => d.Id)
                    .LongNumber(d => d.OsmId)
                    .Text(d => d.Name, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                        .Fields(f => f
                            .Keyword(k => k.Name!.Suffix("raw"), kd => kd
                                .Normalizer("lowercase_normalizer")
                            )
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
                    .LongNumber(d => d.Population)
                    .Text(d => d.ParentChain, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Text(d => d.SearchableAddress, t => t
                        .Analyzer("gazetteer_analyzer")
                        .SearchAnalyzer("gazetteer_search_analyzer")
                    )
                    .Nested(d => d.Parents, n => n
                        .Properties(pp => pp
                            .LongNumber("osmId")
                            .Text("name")
                            .Text("nameEn")
                            .Text("localName")
                            .Keyword("locationType")
                        )
                    )
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
            .Id(document.OsmId.ToString()),
            ct
        );

        if (!response.IsValidResponse)
            _logger.LogWarning("Failed to index document {OsmId}: {Error}", document.OsmId, response.DebugInformation);
    }

    public async Task BulkIndexAsync(IEnumerable<LocationIndexDocument> documents, CancellationToken ct = default)
    {
        var batch = documents.ToList();
        if (batch.Count == 0) return;

        var response = await _client.BulkAsync(b => b
            .Index(IndexName)
            .IndexMany(batch, (op, doc) => op.Id(doc.OsmId.ToString())),
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

    public async Task<List<LocationSearchHit>> SearchAsync(GazetteerSearchRequest request, CancellationToken ct = default)
    {
        var response = await _client.SearchAsync<LocationIndexDocument>(s =>
        {
            s.Index(IndexName)
             .Size(request.Limit)
             .Query(q => BuildQuery(q, request));
        }, ct);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Search failed: {Error}", response.DebugInformation);
            return [];
        }

        return [.. response.Hits
            .Where(h => h.Source != null)
            .Select(h => new LocationSearchHit
            {
                Id = h.Source!.Id,
                OsmId = h.Source.OsmId,
                Name = h.Source.Name,
                NameEn = h.Source.NameEn,
                LocationType = h.Source.LocationType,
                SubType = h.Source.SubType,
                CountryCode = h.Source.CountryCode,
                PostalCode = h.Source.PostalCode,
                Latitude = h.Source.Latitude,
                Longitude = h.Source.Longitude,
                Population = h.Source.Population,
                HasGeometry = h.Source.HasGeometry,
                ParentChain = h.Source.ParentChain,
                Parents = h.Source.Parents,
                Score = h.Score ?? 0
            })];
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
                            .Filter(["lowercase", "asciifolding", "boundary_edge_ngram"])
                        )
                        .Custom("gazetteer_search_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(["lowercase", "asciifolding"])
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
                    .Nested(d => d.Parents, n => n
                        .Properties(pp => pp
                            .LongNumber("osmId")
                            .Text("name")
                            .Text("nameEn")
                            .Text("localName")
                            .Keyword("locationType")
                        )
                    )
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
            .IndexMany(batch, (op, doc) => op.Id(doc.OsmId.ToString())),
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

    private static void BuildQuery(QueryDescriptor<LocationIndexDocument> q, GazetteerSearchRequest request)
    {
        var query = request.Query.Replace(",", " ").Trim();

        q.Bool(b =>
        {
            // Must: cross_fields across name (high boost), parentChain (medium), searchableAddress (low)
            // All query terms must appear across the combined fields
            b.Must(must =>
            {
                must.Bool(innerBool =>
                {
                    innerBool.Should(
                        // Primary: cross-field search across name + parents + searchableAddress
                        should => should.MultiMatch(mm => mm
                            .Query(query)
                            .Fields(new[] { "name^5", "parentChain^2", "searchableAddress^3" })
                            .Type(TextQueryType.CrossFields)
                            .Operator(Operator.And)
                        ),
                        // Fallback: fuzzy best-fields on name for typo tolerance
                        should => should.MultiMatch(mm => mm
                            .Query(query)
                            .Fields(new[] { "name^2", "nameEn^1", "alternateNames" })
                            .Fuzziness(new Fuzziness("AUTO"))
                            .PrefixLength(2)
                            .Type(TextQueryType.BestFields)
                        ),
                        // Postcode exact match
                        should => should.Term(t => t
                            .Field(f => f.PostalCode)
                            .Value(request.Query.ToUpperInvariant())
                            .Boost(10)
                        ),
                        should => should.Term(t => t
                            .Field(f => f.PostalCode)
                            .Value(NormalizePostcodeQuery(request.Query))
                            .Boost(10)
                        )
                    );
                    innerBool.MinimumShouldMatch(1);
                });
            });

            // Should: boost exact name matches
            b.Should(
                // Exact keyword match on name
                s => s.ConstantScore(cs => cs
                    .Filter(f => f.Term(t => t
                        .Field(d => d.Name.Suffix("raw"))
                        .Value(query.ToLowerInvariant())
                    ))
                    .Boost(10000)
                ),
                // All query tokens in name (no fuzz)
                s => s.Match(m => m
                    .Field(f => f.Name)
                    .Query(query)
                    .Operator(Operator.And)
                    .Boost(50)
                ),
                // Phrase match on name
                s => s.MatchPhrase(mp => mp
                    .Field(f => f.Name)
                    .Query(query)
                    .Boost(30)
                )
            );

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

            if (request.WithinOsmId.HasValue)
            {
                var withinOsmId = request.WithinOsmId.Value;
                filters.Add(f => f.Nested(n => n
                    .Path("parents")
                    .Query(nq => nq.Term(t => t
                        .Field("parents.osmId")
                        .Value(FieldValue.Long(withinOsmId))
                    ))
                ));
            }

            if (filters.Count > 0)
                b.Filter(filters.ToArray());
        });
    }

    /// <summary>
    /// Normalizes a postcode query by stripping spaces and uppercasing,
    /// then inserts a wildcard space pattern so "br66ef" matches "BR6 6EF".
    /// </summary>
    private static string NormalizePostcodeQuery(string query)
    {
        var normalized = query.Replace(" ", "").ToUpperInvariant();
        // Build a pattern like "BR6*6EF" won't work well. Instead use the stripped form with wildcard spaces.
        // For keyword field matching, we need to insert optional space: "BR6?6EF" or just "*BR6*6EF*"
        // Simplest: strip spaces from query and create a pattern that matches with optional spaces
        // E.g., "br66ef" -> "*BR6*6EF*" won't work because keyword is stored with space.
        // Best approach: insert a wildcard between each character group
        // Actually simplest: just try inserting a space at common UK postcode positions
        if (normalized.Length >= 5 && normalized.Length <= 7)
        {
            // UK postcodes: incode is always last 3 chars
            var outcode = normalized[..^3];
            var incode = normalized[^3..];
            return $"{outcode} {incode}";
        }
        return normalized;
    }
}

public class GeoPointField
{
    public double Lat { get; set; }
    public double Lon { get; set; }
}
