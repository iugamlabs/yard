using System.Reflection;

namespace Yard.AspNetCore;

public sealed class BodyValidation : IEndpointFilter {
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    ) {
        foreach (var arg in context.Arguments) {
            if (
                arg == null
                || arg.GetType().Namespace != typeof(BodyValidation).Namespace
                || !arg.GetType().Name.EndsWith("Body")
            )
                continue;
            foreach (var property in arg.GetType().GetProperties()) {
                var value = property.GetValue(arg);
                var nullable = new NullabilityInfoContext().Create(property).ReadState;
                if (value == null && nullable == NullabilityState.NotNull)
                    throw new YardHttpException(400, "invalid_request");
                if (value is string text && text.Length > 1024)
                    throw new YardHttpException(400, "invalid_request");
                if (value is Array a && (a.Length > 1000 || a.Cast<object?>().Any(x => x == null)))
                    throw new YardHttpException(400, "invalid_request");
            }
        }
        return await next(context);
    }
}
