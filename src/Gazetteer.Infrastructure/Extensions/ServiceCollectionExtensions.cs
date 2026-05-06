using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Gazetteer.Core.Interfaces;
using Gazetteer.Infrastructure.Data;
using Gazetteer.Infrastructure.Repositories;
using Gazetteer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gazetteer.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGazetteerInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<GazetteerDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsql => npgsql.UseNetTopologySuite()));

        var esUri = configuration["Elasticsearch:Uri"] ?? "http://localhost:9200";
        var settings = new ElasticsearchClientSettings(new Uri(esUri))
            .DisableDirectStreaming();
        services.AddSingleton(new ElasticsearchClient(settings));

        services.AddScoped<ILocationRepository, LocationRepository>();
        services.AddScoped<IElasticsearchService, ElasticsearchService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddMemoryCache();

        return services;
    }
}
