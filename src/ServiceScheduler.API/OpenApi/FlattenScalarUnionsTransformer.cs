using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ServiceScheduler.API.OpenApi;

/// <summary>
/// .NET 10 types a simple query parameter as "integer or string", because the value
/// arrives on the wire as text. It appears as a 3.1 type array, or as <c>anyOf</c> once
/// the document is emitted as 3.0. Swagger UI can render neither, so it treats the field
/// as empty, reports "Required field is not provided" and never sends the request.
///
/// This collapses such a union back to the single non-string type it was built from.
/// </summary>
public sealed class FlattenScalarUnionsTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (schema.AnyOf is { Count: > 0 })
        {
            var concrete = schema.AnyOf
                .FirstOrDefault(s => s.Type is not null && s.Type != JsonSchemaType.String);

            if (concrete?.Type is not null)
            {
                schema.Type = concrete.Type;
                schema.AnyOf = null;
                schema.Pattern = null;
            }
        }
        else if (schema.Type is { } type && type.HasFlag(JsonSchemaType.String) && type != JsonSchemaType.String)
        {
            // 3.1 form: a single Type carrying several flags, e.g. Integer | String.
            schema.Type = type & ~JsonSchemaType.String;
            schema.Pattern = null;
        }

        return Task.CompletedTask;
    }
}
