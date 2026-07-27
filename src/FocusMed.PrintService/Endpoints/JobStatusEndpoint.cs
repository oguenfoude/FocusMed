using FocusMed.PrintService.Abstractions;

namespace FocusMed.PrintService.Endpoints;

public static class JobStatusEndpoint
{
    public static IEndpointRouteBuilder MapJobStatus(this IEndpointRouteBuilder app)
    {
        app.MapGet("/job-status/{printerName}/{jobId:int}", async (
            string printerName,
            int jobId,
            IPhysicalPrintService svc,
            CancellationToken ct) =>
        {
            var status = await svc.GetJobStatusAsync(printerName, jobId);
            return Results.Ok(status);
        });
        return app;
    }
}
