using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata.Conventions;

/// <summary>
///     Adds the TimescaleDB conventions (attribute discovery, model finalizing validation)
///     to the convention set.
/// </summary>
public class TimescaleDbConventionSetPlugin : IConventionSetPlugin
{
    public virtual ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.Add(new TimescaleDbAttributeConvention());
        conventionSet.Add(new TimescaleDbModelFinalizingConvention());
        return conventionSet;
    }
}
