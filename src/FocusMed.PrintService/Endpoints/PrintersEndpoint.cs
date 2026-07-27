using FocusMed.PrintService.Abstractions;

namespace FocusMed.PrintService.Endpoints;

public static class PrintersEndpoint
{
    public static IEndpointRouteBuilder MapPrinters(this IEndpointRouteBuilder app)
    {
        app.MapGet("/printers", async (IPhysicalPrintService svc, CancellationToken ct) =>
        {
            var printers = await svc.GetConfiguredPrintersAsync();
            return Results.Ok(printers);
        });
        return app;
    }
}
