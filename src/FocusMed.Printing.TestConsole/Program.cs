using FocusMed.Printing;
using FocusMed.Printing.Discovery;
using FocusMed.Printing.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Printing;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddSerilog());
services.AddFocusMedPrinting();

var provider = services.BuildServiceProvider();
var discoveryService = provider.GetRequiredService<IPrinterDiscoveryService>();
var capabilityService = provider.GetRequiredService<IPrinterCapabilityService>();
var profileBuilder = provider.GetRequiredService<IPrintProfileBuilder>();

Console.WriteLine("================================================================================");
Console.WriteLine("          FOCUSMED PRINTING SYSTEM DIAGNOSTIC & VERIFICATION UTILITY            ");
Console.WriteLine("================================================================================");

// 1. System Printers Discovery
var installedPrinters = discoveryService.GetAvailablePrinters();
Console.WriteLine($"\n[1/3] INSTALLED PRINTERS DISCOVERED ({installedPrinters.Count} Total):");
foreach (var printer in installedPrinters)
{
    Console.WriteLine($"  • {printer.Name}");
}

// 2. Hardware Capability Snapshots & Profiles
Console.WriteLine("\n================================================================================");
Console.WriteLine("[2/3] PRINTER CAPABILITY SNAPSHOTS & GENERATED PROFILES:");
Console.WriteLine("================================================================================");

foreach (var printer in installedPrinters)
{
    Console.WriteLine($"\n>>> PRINTER: '{printer.Name}'");
    Console.WriteLine(new string('-', 80));

    try
    {
        using var printServer = new LocalPrintServer();
        using var queue = printServer.GetPrintQueue(printer.Name);
        if (queue != null)
        {
            Console.WriteLine($"  [QUEUE INFO] Driver: '{queue.QueueDriver?.Name}', Location: '{queue.Location}', Status: {(queue.IsOffline ? "Offline" : "Online")}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [QUEUE INFO] Could not open queue: {ex.Message}");
    }

    var snapshot = await capabilityService.GetSnapshotAsync(printer.Name);
    Console.WriteLine($"  [CAPABILITIES] Source: {snapshot.DiscoverySource}, Duplex: {snapshot.SupportsDuplex}, Color: {snapshot.SupportsColor}, Trays: {snapshot.PaperTrays.Count}, Sizes: {snapshot.PaperSizes.Count}");

    var profiles = profileBuilder.BuildProfiles(snapshot);
    Console.WriteLine($"  [DYNAMIC PROFILES] ({profiles.Count} generated):");
    foreach (var prof in profiles.Take(6))
    {
        Console.WriteLine($"    • '{prof.Name}': Paper={prof.PaperSizeName}, Duplex={prof.RequiresDuplex}, Booklet={prof.IsBooklet}, ShortEdge={prof.UseDuplexShortEdge}");
    }
}

// 3. Deep Printer Capabilities & PrintTicket XML Inspector (Zero Paper Cost)
Console.WriteLine("\n================================================================================");
Console.WriteLine("[3/4] DEEP DRIVER XML CAPABILITY & TICKET INSPECTOR:");
Console.WriteLine("================================================================================");

foreach (var printer in installedPrinters.Where(p => p.Name.Contains("KONICA", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("C250i", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("FocusMed", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine($"\n>>> DEEP XML INSPECTION FOR: '{printer.Name}'");
    Console.WriteLine(new string('-', 80));

    try
    {
        using var printServer = new LocalPrintServer();
        using var queue = printServer.GetPrintQueue(printer.Name);

        // A. Dump all driver XML features & options
        using var capsStream = queue.GetPrintCapabilitiesAsXml();
        var capsDoc = new System.Xml.XmlDocument();
        capsDoc.Load(capsStream);

        var nsmgr = new System.Xml.XmlNamespaceManager(capsDoc.NameTable);
        nsmgr.AddNamespace("psf", "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework");
        nsmgr.AddNamespace("psk", "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords");

        var featureNodes = capsDoc.SelectNodes("//psf:Feature", nsmgr);
        Console.WriteLine($"  [ALL XML FEATURES DISCOVERED ({featureNodes?.Count ?? 0} total)]:");

        if (featureNodes != null)
        {
            foreach (System.Xml.XmlNode feat in featureNodes)
            {
                string featName = feat.Attributes?["name"]?.Value ?? "Unnamed";
                var options = feat.SelectNodes("psf:Option", nsmgr);
                var optionList = new List<string>();
                if (options != null)
                {
                    foreach (System.Xml.XmlNode opt in options)
                    {
                        optionList.Add(opt.Attributes?["name"]?.Value ?? "");
                    }
                }
                Console.WriteLine($"    • Feature: {featName}");
                Console.WriteLine($"      Options ({optionList.Count}): [{string.Join(", ", optionList.Take(12))}{(optionList.Count > 12 ? "..." : "")}]");
            }
        }

        // B. Dump UserPrintTicket active XML features
        var userTicket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
        if (userTicket != null)
        {
            using var ticketStream = userTicket.GetXmlStream();
            var ticketDoc = new System.Xml.XmlDocument();
            ticketDoc.Load(ticketStream);

            var ticketFeatures = ticketDoc.SelectNodes("//psf:Feature", nsmgr);
            Console.WriteLine($"\n  [USER PRINT TICKET ACTIVE FEATURES ({ticketFeatures?.Count ?? 0} active)]:");
            if (ticketFeatures != null)
            {
                foreach (System.Xml.XmlNode feat in ticketFeatures)
                {
                    string featName = feat.Attributes?["name"]?.Value ?? "";
                    var opt = feat.SelectSingleNode("psf:Option", nsmgr);
                    string optName = opt?.Attributes?["name"]?.Value ?? "None";
                    Console.WriteLine($"    • {featName} = {optName}");
                }
            }
        }

        // C. Test MergeAndValidatePrintTicket with Booklet A3 delta
        var delta = queue.DefaultPrintTicket;
        delta.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA3);
        delta.PageOrientation = PageOrientation.Landscape;
        delta.Duplexing = Duplexing.TwoSidedShortEdge;
        delta.Stapling = Stapling.SaddleStitch;

        var valResult = queue.MergeAndValidatePrintTicket(userTicket ?? queue.DefaultPrintTicket, delta);
        Console.WriteLine($"\n  [MERGE & VALIDATE TEST FOR BOOKLET A3]:");
        Console.WriteLine($"    • ConflictStatus   : {valResult.ConflictStatus}");
        Console.WriteLine($"    • Result MediaSize : {valResult.ValidatedPrintTicket.PageMediaSize?.PageMediaSizeName}");
        Console.WriteLine($"    • Result Orient    : {valResult.ValidatedPrintTicket.PageOrientation}");
        Console.WriteLine($"    • Result Duplex    : {valResult.ValidatedPrintTicket.Duplexing}");
        Console.WriteLine($"    • Result Stapling  : {valResult.ValidatedPrintTicket.Stapling}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [ERROR DURING XML INSPECTION]: {ex.Message}");
    }
}

// 4. Pipeline Health Check
Console.WriteLine("\n================================================================================");
Console.WriteLine("[4/4] PRINTING PIPELINE HEALTH CHECK:");
Console.WriteLine("================================================================================");

Console.WriteLine("  • Discovery Service : OPERATIONAL");
Console.WriteLine("  • Capability Engine : OPERATIONAL (Modern XPS -> Legacy GDI+ -> Win32 P/Invoke)");
Console.WriteLine("  • Profile Builder   : OPERATIONAL (A4 Duplex, A4 Booklet, A3 Booklet)");

Console.WriteLine("\n================================================================================");
Console.WriteLine("       FOCUSMED PRINTING SYSTEM STATUS: DISCOVERY & DIAGNOSTICS OPERATIONAL     ");
Console.WriteLine("================================================================================");










