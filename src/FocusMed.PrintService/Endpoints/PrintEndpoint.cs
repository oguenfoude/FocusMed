using FocusMed.PrintService.Abstractions;

namespace FocusMed.PrintService.Endpoints;

public static class PrintEndpoint
{
    public static IEndpointRouteBuilder MapPrint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/print", async (PrintRequest request, IPhysicalPrintService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.PdfPath))
                return Results.BadRequest(new PrintResult(false, null, "PdfPath est requis."));
            if (string.IsNullOrWhiteSpace(request.PrinterName))
                return Results.BadRequest(new PrintResult(false, null, "PrinterName est requis."));

            var result = await svc.PrintAsync(request);
            return Results.Ok(result);
        });
        return app;
    }
}
