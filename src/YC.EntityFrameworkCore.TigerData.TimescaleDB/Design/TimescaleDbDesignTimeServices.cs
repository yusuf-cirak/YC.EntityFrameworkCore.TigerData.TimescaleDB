using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Design.Scaffolding;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Design;

/// <summary>
///     Design-time services for TimescaleDB: discovered through the
///     <c>DesignTimeServicesReference</c> attribute injected into the consuming
///     project by this package's buildTransitive props. Migrations are produced from the
///     standard operations + <c>migrationBuilder.Sql(...)</c>, so no custom code generator is
///     needed; only the scaffolding factory is registered.
/// </summary>
public class TimescaleDbDesignTimeServices : IDesignTimeServices
{
    public virtual void ConfigureDesignTimeServices(IServiceCollection serviceCollection)
        => serviceCollection
            .AddSingleton<IDatabaseModelFactory, TimescaleDbDatabaseModelFactory>();
}
