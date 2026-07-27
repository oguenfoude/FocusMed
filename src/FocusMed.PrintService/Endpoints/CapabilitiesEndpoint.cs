using FocusMed.PrintService.Abstractions;

namespace FocusMed.PrintService.Endpoints;

public static class CapabilitiesEndpoint
{
    public static IEndpointRouteBuilder MapCapabilities(this IEndpointRouteBuilder app)
    {
        app.MapGet("/printers/{printerName}/capabilities", async (
            string printerName,
            IPhysicalPrintService svc,
            CancellationToken ct) =>
        {
            var caps = await svc.GetCapabilitiesAsync(printerName);
            return Results.Ok(caps);
        });
        return app;
    }
}
