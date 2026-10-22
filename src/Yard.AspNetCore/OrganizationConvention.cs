using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Yard.AspNetCore;

// Finalization runs after business modules register their entities and mappings.
internal sealed class OrganizationConvention(YardDbContext context) : IModelFinalizingConvention {
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> conventionContext
    ) {
        foreach (
            var type in modelBuilder
                .Metadata.GetEntityTypes()
                .Where(x => typeof(IOrganizationEntity).IsAssignableFrom(x.ClrType))
                .ToArray()
        ) {
            if (type.BaseType != null)
                throw new InvalidOperationException(
                    "Tenant entities must declare their organization on the EF root entity."
                );
            var row = Expression.Parameter(type.ClrType, "row");
            var current = Expression.Constant(context);
            var id = Expression.Convert(
                Expression.Property(row, nameof(IOrganizationEntity.OrganizationId)),
                typeof(Guid?)
            );
            var predicate = Expression.Lambda(
                Expression.OrElse(
                    Expression.Property(current, nameof(YardDbContext.OrganizationBypass)),
                    Expression.Equal(
                        id,
                        Expression.Property(current, nameof(YardDbContext.ActiveOrganizationId))
                    )
                ),
                row
            );
            ((IMutableEntityType)type).SetQueryFilter("Yard.Organization", predicate);
            var property = (IMutableProperty)(
                type.FindProperty(nameof(IOrganizationEntity.OrganizationId))
                ?? throw new InvalidOperationException("OrganizationId must be mapped.")
            );
            property.IsConcurrencyToken = true;
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }
}
