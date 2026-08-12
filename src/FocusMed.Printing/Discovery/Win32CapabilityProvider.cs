using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Discovery;

internal sealed class Win32CapabilityProvider(ILogger<Win32CapabilityProvider> logger)
{
    private const string Winspool = "winspool.drv";

    private const int DM_OUT_BUFFER = 2;
    private const int DM_IN_BUFFER = 8;
    private const int DM_IN_AND_OUT = 10;
    private const int DM_PAPERSIZE = 0x0002;
    private const int DM_PAPERSOURCE = 0x0200;
    private const int DM_ORIENTATION = 0x0001;

    [DllImport(Winspool, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeviceCapabilities(
        string pDevice, string pPort, DeviceCapabilitiesFlags fwCapability,
        IntPtr pOutput, IntPtr pDevMode);

    [DllImport(Winspool, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetPrinter(IntPtr hPrinter, int dwLevel, IntPtr pPrinter, int dwBuf, out int dwNeeded);

    [DllImport(Winspool, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport(Winspool)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport(Winspool, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(
        IntPtr hWnd, IntPtr hPrinter, string pDeviceName,
        IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    [DllImport(Winspool, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(
        IntPtr hWnd, IntPtr hPrinter, string pDeviceName,
        byte[] pDevModeOutput, byte[] pDevModeInput, int fMode);

    [DllImport(Winspool, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(
        IntPtr hWnd, IntPtr hPrinter, string pDeviceName,
        IntPtr pDevModeOutput, byte[] pDevModeInput, int fMode);

    private enum DeviceCapabilitiesFlags : short
    {
        DC_DUPLEX = 7,
        DC_PAPERSIZE = 3,
        DC_PAPERS = 2,
        DC_PAPERNAMES = 16,
        DC_COPIES = 18,
        DC_COLLATE = 22,
        DC_STAPLE = 30,
        DC_COLORDEVICE = 32,
        DC_BINNAMES = 12,
        DC_BINS = 6,
        DC_ENUMRESOLUTIONS = 13,
        DC_PAPERBINS = 19,
        DC_PAPERSANDNAMES = 23,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_2
    {
        public string pServerName;
        public string pPrinterName;
        public string pShareName;
        public string pPortName;
        public string pDriverName;
        public string pComment;
        public string pLocation;
        public IntPtr pDevMode;
        public string pSepFile;
        public string pPrintProcessor;
        public string pDatatype;
        public string pParameters;
        public IntPtr pSecurityDescriptor;
        public int Attributes;
        public int Priority;
        public int DefaultPriority;
        public int StartTime;
        public int UntilTime;
        public int Status;
        public int cJobs;
        public int AveragePPM;
    }

    public PrinterCapabilitySnapshot? TryGet(string printerName)
    {
        try
        {
            int duplexResult = DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_DUPLEX, IntPtr.Zero, IntPtr.Zero);
            bool supportsDuplex = duplexResult == 1;

            int colorResult = DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_COLORDEVICE, IntPtr.Zero, IntPtr.Zero);
            bool supportsColor = colorResult == 1;

            int collateResult = DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_COLLATE, IntPtr.Zero, IntPtr.Zero);
            bool supportsCollation = collateResult == 1;

            // Paper sizes
            int paperCount = DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_PAPERSIZE, IntPtr.Zero, IntPtr.Zero);
            var paperSizes = new List<PaperSizeInfo>();
            if (paperCount > 0)
            {
                IntPtr sizeBuffer = Marshal.AllocHGlobal(paperCount * Marshal.SizeOf(typeof(POINT)));
                IntPtr nameBuffer = Marshal.AllocHGlobal(paperCount * 64 * sizeof(char));
                IntPtr paperIdBuffer = Marshal.AllocHGlobal(paperCount * sizeof(short));

                try
                {
                    DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_PAPERSIZE, sizeBuffer, IntPtr.Zero);
                    DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_PAPERNAMES, nameBuffer, IntPtr.Zero);
                    DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_PAPERS, paperIdBuffer, IntPtr.Zero);

                    for (int i = 0; i < paperCount; i++)
                    {
                        var point = Marshal.PtrToStructure<POINT>(new IntPtr(sizeBuffer + i * Marshal.SizeOf(typeof(POINT))));
                        string name = Marshal.PtrToStringUni(new IntPtr(nameBuffer + i * 64 * sizeof(char)))?.TrimEnd('\0') ?? $"Paper{i}";
                        short paperId = Marshal.ReadInt16(new IntPtr(paperIdBuffer + i * sizeof(short)));

                        paperSizes.Add(new PaperSizeInfo
                        {
                            Name = name,
                            WidthMm = point.X / 10f,
                            HeightMm = point.Y / 10f,
                            PaperKindId = paperId
                        });
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(sizeBuffer);
                    Marshal.FreeHGlobal(nameBuffer);
                    Marshal.FreeHGlobal(paperIdBuffer);
                }
            }

            // Paper trays with ACTUAL bin numbers from DC_BINS
            int binCount = DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_BINNAMES, IntPtr.Zero, IntPtr.Zero);
            var paperTrays = new List<PaperTrayInfo>();
            if (binCount > 0)
            {
                IntPtr binNameBuffer = Marshal.AllocHGlobal(binCount * 24 * sizeof(char));
                IntPtr binNumberBuffer = Marshal.AllocHGlobal(binCount * sizeof(short));
                try
                {
                    DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_BINNAMES, binNameBuffer, IntPtr.Zero);
                    DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_BINS, binNumberBuffer, IntPtr.Zero);
                    for (int i = 0; i < binCount; i++)
                    {
                        string binName = Marshal.PtrToStringUni(new IntPtr(binNameBuffer + i * 24 * sizeof(char)))?.TrimEnd('\0') ?? $"Tray{i + 1}";
                        short binNumber = Marshal.ReadInt16(new IntPtr(binNumberBuffer + i * sizeof(short)));
                        paperTrays.Add(new PaperTrayInfo
                        {
                            Name = binName,
                            BinNumber = binNumber
                        });
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(binNameBuffer);
                    Marshal.FreeHGlobal(binNumberBuffer);
                }
            }

            // DPI resolutions
            int resCount = DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_ENUMRESOLUTIONS, IntPtr.Zero, IntPtr.Zero);
            var resolutions = new List<ResolutionInfo>();
            if (resCount > 0)
            {
                IntPtr resBuffer = Marshal.AllocHGlobal(resCount * Marshal.SizeOf(typeof(POINT)));
                try
                {
                    DeviceCapabilities(printerName, "", DeviceCapabilitiesFlags.DC_ENUMRESOLUTIONS, resBuffer, IntPtr.Zero);
                    for (int i = 0; i < resCount; i++)
                    {
                        var point = Marshal.PtrToStructure<POINT>(new IntPtr(resBuffer + i * Marshal.SizeOf(typeof(POINT))));
                        resolutions.Add(new ResolutionInfo
                        {
                            DpiX = point.X,
                            DpiY = point.Y,
                            IsDefault = i == 0
                        });
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(resBuffer);
                }
            }

            // Get actual driver name and port via GetPrinter
            string driverName = "Win32";
            string portName = "";
            if (OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            {
                try
                {
                    int needed = 0;
                    GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out needed);
                    if (needed > 0)
                    {
                        IntPtr printerInfo = Marshal.AllocHGlobal(needed);
                        try
                        {
                            if (GetPrinter(hPrinter, 2, printerInfo, needed, out _))
                            {
                                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(printerInfo);
                                driverName = info.pDriverName ?? "Win32";
                                portName = info.pPortName ?? "";
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(printerInfo);
                        }
                    }
                }
                finally
                {
                    ClosePrinter(hPrinter);
                }
            }

            // Build paper-to-tray mapping via DEVMODE probing
            var paperToTrayMap = BuildPaperToTrayMap(printerName, portName, paperTrays, paperSizes);

            var snapshot = new PrinterCapabilitySnapshot
            {
                PrinterName = printerName,
                DriverName = driverName,
                SupportsDuplex = supportsDuplex,
                SupportsColor = supportsColor,
                SupportsCollation = supportsCollation,
                PaperSizes = paperSizes,
                PaperTrays = paperTrays,
                Resolutions = resolutions,
                DiscoverySource = "Win32.DeviceCapabilities",
                PaperToTrayMap = paperToTrayMap
            };

            logger.LogInformation("Win32CapabilityProvider: Found {PaperCount} paper sizes, {TrayCount} trays, {ResCount} resolutions, Duplex={HasDuplex}, Color={HasColor}, PaperToTrayMap={MapCount} for '{PrinterName}'",
                paperSizes.Count, paperTrays.Count, resolutions.Count, supportsDuplex, supportsColor, paperToTrayMap.Count, printerName);

            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Win32CapabilityProvider: Failed to query '{PrinterName}'", printerName);
            return null;
        }
    }

    private Dictionary<string, int> BuildPaperToTrayMap(string printerName, string portName,
        List<PaperTrayInfo> trays, List<PaperSizeInfo> paperSizes)
    {
        var map = new Dictionary<string, int>();

        // Method 1: Try DC_PAPERBINS (undocumented but supported by many drivers)
        // Returns array of bin numbers indexed by paper size index
        int paperCount = DeviceCapabilities(printerName, portName, DeviceCapabilitiesFlags.DC_PAPERSIZE, IntPtr.Zero, IntPtr.Zero);
        int binCount = DeviceCapabilities(printerName, portName, DeviceCapabilitiesFlags.DC_PAPERBINS, IntPtr.Zero, IntPtr.Zero);

        if (paperCount > 0 && binCount > 0 && paperCount == binCount)
        {
            IntPtr paperSizeBuffer = Marshal.AllocHGlobal(paperCount * Marshal.SizeOf(typeof(POINT)));
            IntPtr paperNameBuffer = Marshal.AllocHGlobal(paperCount * 64 * sizeof(char));
            IntPtr paperBinsBuffer = Marshal.AllocHGlobal(binCount * sizeof(short));
            try
            {
                DeviceCapabilities(printerName, portName, DeviceCapabilitiesFlags.DC_PAPERSIZE, paperSizeBuffer, IntPtr.Zero);
                DeviceCapabilities(printerName, portName, DeviceCapabilitiesFlags.DC_PAPERNAMES, paperNameBuffer, IntPtr.Zero);
                DeviceCapabilities(printerName, portName, DeviceCapabilitiesFlags.DC_PAPERBINS, paperBinsBuffer, IntPtr.Zero);

                for (int i = 0; i < paperCount; i++)
                {
                    string paperName = Marshal.PtrToStringUni(new IntPtr(paperNameBuffer + i * 64 * sizeof(char)))?.TrimEnd('\0') ?? $"Paper{i}";
                    short binNumber = Marshal.ReadInt16(new IntPtr(paperBinsBuffer + i * sizeof(short)));

                    if (binNumber > 0 && !map.ContainsKey(paperName))
                    {
                        map[paperName] = binNumber;
                        logger.LogDebug("Win32CapabilityProvider DC_PAPERBINS: Paper '{PaperName}' -> bin {Bin}", paperName, binNumber);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(paperSizeBuffer);
                Marshal.FreeHGlobal(paperNameBuffer);
                Marshal.FreeHGlobal(paperBinsBuffer);
            }

            if (map.Count > 0)
            {
                logger.LogInformation("Win32CapabilityProvider: DC_PAPERBINS mapped {Count} papers to trays", map.Count);
                return map;
            }
        }

        // Method 2: DEVMODE reverse probing (set paper, read source)
        // Already tried — Konica v4 class driver doesn't support this

        // Method 3: Dimension-based heuristic matching
        // Match paper dimensions to tray positions — standard office printers:
        // Tray 1 = A4 (210×297mm), Tray 2 = A3 (297×420mm), etc.
        if (map.Count == 0 && trays.Count >= 2)
        {
            logger.LogInformation("Win32CapabilityProvider: Using dimension-based tray heuristic for '{PrinterName}'", printerName);

            // Build a tray lookup by position (Tray1=index0, Tray2=index1, etc.)
            var sortedTrays = trays
                .Where(t => t.Name.Contains("Tray", StringComparison.OrdinalIgnoreCase)
                    && !t.Name.Contains("Bypass", StringComparison.OrdinalIgnoreCase)
                    && !t.Name.Contains("Manual", StringComparison.OrdinalIgnoreCase)
                    && !t.Name.Contains("Envelope", StringComparison.OrdinalIgnoreCase))
                .OrderBy(t =>
                {
                    // Extract tray number from name like "Tray1", "Tray2", etc.
                    var match = System.Text.RegularExpressions.Regex.Match(t.Name, @"\d+");
                    return match.Success ? int.Parse(match.Value) : 999;
                })
                .ToList();

            if (sortedTrays.Count >= 2)
            {
                // Standard Konica layout: Tray1=A4, Tray2=A3
                // Heuristic: find A4 and A3 papers by dimensions
                var a4Paper = paperSizes.FirstOrDefault(p =>
                    Math.Abs(p.WidthMm - 210) < 5 && Math.Abs(p.HeightMm - 297) < 5
                    || Math.Abs(p.WidthMm - 297) < 5 && Math.Abs(p.HeightMm - 210) < 5);

                var a3Paper = paperSizes.FirstOrDefault(p =>
                    Math.Abs(p.WidthMm - 297) < 5 && Math.Abs(p.HeightMm - 420) < 5
                    || Math.Abs(p.WidthMm - 420) < 5 && Math.Abs(p.HeightMm - 297) < 5);

                if (a4Paper is not null && sortedTrays.Count >= 1)
                {
                    map[a4Paper.Name] = sortedTrays[0].BinNumber;
                    logger.LogInformation("Win32CapabilityProvider: Heuristic: '{Paper}' -> Tray '{Tray}' (bin {Bin})",
                        a4Paper.Name, sortedTrays[0].Name, sortedTrays[0].BinNumber);
                }

                if (a3Paper is not null && sortedTrays.Count >= 2)
                {
                    map[a3Paper.Name] = sortedTrays[1].BinNumber;
                    logger.LogInformation("Win32CapabilityProvider: Heuristic: '{Paper}' -> Tray '{Tray}' (bin {Bin})",
                        a3Paper.Name, sortedTrays[1].Name, sortedTrays[1].BinNumber);
                }

                // Map other common sizes to additional trays
                var letterPaper = paperSizes.FirstOrDefault(p =>
                    Math.Abs(p.WidthMm - 216) < 5 && Math.Abs(p.HeightMm - 279) < 5);
                if (letterPaper is not null && !map.ContainsKey(letterPaper.Name))
                {
                    map[letterPaper.Name] = sortedTrays[0].BinNumber; // Letter usually in Tray1
                }

                var legalPaper = paperSizes.FirstOrDefault(p =>
                    Math.Abs(p.WidthMm - 216) < 5 && Math.Abs(p.HeightMm - 356) < 5);
                if (legalPaper is not null && !map.ContainsKey(legalPaper.Name))
                {
                    map[legalPaper.Name] = sortedTrays.Count >= 2 ? sortedTrays[1].BinNumber : sortedTrays[0].BinNumber;
                }
            }
        }

        logger.LogInformation("Win32CapabilityProvider: Paper-to-tray mapping: {MapCount} entries ({Entries})",
            map.Count, string.Join(", ", map.Select(kv => $"{kv.Key}->bin{kv.Value}")));

        return map;
    }
}
