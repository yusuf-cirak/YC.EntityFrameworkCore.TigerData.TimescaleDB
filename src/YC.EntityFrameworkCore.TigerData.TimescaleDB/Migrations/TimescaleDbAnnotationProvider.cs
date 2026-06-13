using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.Internal;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations;

/// <summary>
///     Projects the canonical <c>TimescaleDb:*</c> annotations of a hypertable entity onto its
///     table, so EF's model differ carries them on <c>CreateTableOperation</c> /
///     <c>AlterTableOperation</c> (with old + new values). The SQL generator then interprets the
///     delta to emit forward, reverse or rebuild SQL — no custom operations required.
/// </summary>
public class TimescaleDbAnnotationProvider : NpgsqlAnnotationProvider
{
    public TimescaleDbAnnotationProvider(RelationalAnnotationProviderDependencies dependencies)
        : base(dependencies)
    {
    }

    public override IEnumerable<IAnnotation> For(ITable table, bool designTime)
    {
        foreach (var annotation in base.For(table, designTime))
        {
            yield return annotation;
        }

        var hypertable = table.EntityTypeMappings
            .Select(mapping => mapping.TypeBase)
            .FirstOrDefault(type => type.FindAnnotation(TimescaleDbAnnotationNames.IsHypertable)?.Value is true);

        if (hypertable is null)
        {
            yield break;
        }

        foreach (var name in TimescaleDbAnnotationNames.TableAttached)
        {
            if (hypertable.FindAnnotation(name) is { Value: not null } annotation)
            {
                yield return new Annotation(name, annotation.Value);
            }
        }
    }
}
