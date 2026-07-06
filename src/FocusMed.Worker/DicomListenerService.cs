using FellowOakDicom;
using FellowOakDicom.Network;
using FocusMed.Dicom;
using FocusMed.Dicom.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusMed.Worker;

public class DicomListenerService : BackgroundService
{
    private readonly ILogger<DicomListenerService> _logger;
    private readonly IDicomServerFactory _serverFactory;
    private readonly int _port;
    private readonly string _aeTitle;
    private readonly string _bindAddress;
    private IDicomServer? _server;

    public DicomListenerService(ILogger<DicomListenerService> logger, IDicomServerFactory serverFactory, IOptions<DicomNetworkingOptions> options)
    {
        _logger = logger;
        _serverFactory = serverFactory;
        _port = options.Value.DicomPort;
        _aeTitle = options.Value.AETitle;
        _bindAddress = options.Value.BindAddress;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var activeListeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (activeListeners.Any(endpoint => endpoint.Port == _port))
            {
                _logger.LogCritical("FATAL: Port {Port} is already in use. The DICOM listener cannot start.", _port);
                return Task.CompletedTask;
            }

            _logger.LogInformation("DICOM listener successfully starting on {BindAddress}:{Port} as AE Title '{AETitle}'", _bindAddress, _port, _aeTitle);
            _server = _serverFactory.Create<FocusMedScp>(_bindAddress, _port);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Failed to start DICOM listener on {BindAddress}:{Port}", _bindAddress, _port);
        }

        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping DICOM listener...");
        _server?.Stop();
        return base.StopAsync(cancellationToken);
    }
}
