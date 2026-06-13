using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Query;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Conventions;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Query.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     TimescaleDB service registration. Called by
///     <see cref="YC.EntityFrameworkCore.TigerData.TimescaleDB.Infrastructure.Internal.TimescaleDbOptionsExtension" />;
///     not usually invoked directly.
/// </summary>
public static class TimescaleDbServiceCollectionExtensions
{
    public static IServiceCollection AddEntityFrameworkTimescaleDb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Plugin-style services: registered additively alongside Npgsql's own.
        new EntityFrameworkRelationalServicesBuilder(services)
            .TryAdd<IConventionSetPlugin, TimescaleDbConventionSetPlugin>()
            .TryAdd<IMethodCallTranslatorPlugin, TimescaleDbMethodCallTranslatorPlugin>()
            .TryAdd<IAggregateMethodCallTranslatorPlugin, TimescaleDbAggregateMethodCallTranslatorPlugin>()
            .TryAdd<IEvaluatableExpressionFilterPlugin, TimescaleDbEvaluatableExpressionFilterPlugin>();

        // Single-instance services: must win over Npgsql's registration regardless of
        // the order in which the options extensions apply their services.
        services.Replace(ServiceDescriptor.Scoped<IMigrationsSqlGenerator, TimescaleDbMigrationsSqlGenerator>());
        services.Replace(ServiceDescriptor.Scoped<IMigrationsModelDiffer, TimescaleDbMigrationsModelDiffer>());
        services.Replace(ServiceDescriptor.Singleton<IRelationalAnnotationProvider, TimescaleDbAnnotationProvider>());
        services.Replace(ServiceDescriptor.Singleton<IAnnotationCodeGenerator, TimescaleDbAnnotationCodeGenerator>());

        return services;
    }
}
