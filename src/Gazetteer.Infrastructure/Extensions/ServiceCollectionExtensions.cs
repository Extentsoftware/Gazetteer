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
        // PostgreSQL + PostGIS
        services.AddDbContext<GazetteerDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsql => npgsql.UseNetTopologySuite()
            )
        );

        // Elasticsearch
        var esUrl = configuration["Elasticsearch:Url"] ?? "http://127.0.0.1:9200";
        var settings = new ElasticsearchClientSettings(new Uri(esUrl))
            .DefaultIndex("locations")
            .EnableDebugMode();
        var client = new ElasticsearchClient(settings);
        services.AddSingleton(client);

        // Services
        services.AddScoped<ILocationRepository, LocationRepository>();
        services.AddScoped<IElasticsearchService, ElasticsearchService>();
        services.AddScoped<ISearchService, SearchService>();

        // Caching
        services.AddMemoryCache();

        return services;
    }
}
