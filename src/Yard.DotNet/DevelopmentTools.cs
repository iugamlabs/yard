using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Yard.DotNet;

public static class DevelopmentTools {
    public static async Task<int> Run(
        string[] args,
        Func<DbContext> createDatabase,
        Func<WebApplication> createApplication
    ) {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try {
            if (args is ["contracts"]) {
                await using var app = createApplication();
                app.UseRouting();
                app.UseEndpoints(_ => { });
                await using var scope = app.Services.CreateAsyncScope();
                var document = await scope
                    .ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1")
                    .GetOpenApiDocumentAsync();
                await document.SerializeAsync(
                    Console.OpenStandardOutput(),
                    OpenApiSpecVersion.OpenApi3_0,
                    "json",
                    cancellation.Token
                );
            } else if (args is ["db", var verb]) {
                await using var db = createDatabase();
                await DatabaseManagement.Run(db, verb, cancellation.Token);
            } else
                throw new InvalidOperationException("Unknown development tool operation.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            return 130;
        }
        catch (Exception e)
            when (e
                    is InvalidOperationException
                        or NotSupportedException
                        or DbUpdateException
                        or System.Data.Common.DbException
            ) {
            Console.Error.WriteLine(
                e is DbUpdateException or System.Data.Common.DbException
                    ? "Database operation failed. Check availability, constraints and permissions."
                    : e.Message
            );
            return 1;
        }
        finally {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
