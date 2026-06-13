using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Infrastructure.Internal;

/// <summary>
///     Options extension registered by <c>UseTimescaleDb()</c>. Registers all TimescaleDB
///     services on top of the Npgsql provider.
/// </summary>
public class TimescaleDbOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>
    ///     When <see langword="true" /> (default), the first migration emits
    ///     <c>CREATE EXTENSION IF NOT EXISTS timescaledb;</c>.
    /// </summary>
    public virtual bool CreateExtension { get; private set; } = true;

    public TimescaleDbOptionsExtension()
    {
    }

    protected TimescaleDbOptionsExtension(TimescaleDbOptionsExtension copyFrom)
        => CreateExtension = copyFrom.CreateExtension;

    public virtual DbContextOptionsExtensionInfo Info
        => _info ??= new ExtensionInfo(this);

    protected virtual TimescaleDbOptionsExtension Clone()
        => new(this);

    public virtual TimescaleDbOptionsExtension WithCreateExtension(bool createExtension)
    {
        var clone = Clone();
        clone.CreateExtension = createExtension;
        return clone;
    }

    public virtual void ApplyServices(IServiceCollection services)
        => services.AddEntityFrameworkTimescaleDb();

    public virtual void Validate(IDbContextOptions options)
    {
        if (options.FindExtension<NpgsqlOptionsExtension>() is null)
        {
            throw new InvalidOperationException(
                "TimescaleDB support requires the Npgsql provider. Call 'UseNpgsql(...)' before 'UseTimescaleDb()'.");
        }
    }

    private sealed class ExtensionInfo(TimescaleDbOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new TimescaleDbOptionsExtension Extension
            => (TimescaleDbOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider
            => false;

        public override string LogFragment
            => "using TimescaleDB ";

        public override int GetServiceProviderHashCode()
            => Extension.CreateExtension.GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo otherInfo
                && Extension.CreateExtension == otherInfo.Extension.CreateExtension;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["TimescaleDb:CreateExtension"] = Extension.CreateExtension.ToString();
    }
}
