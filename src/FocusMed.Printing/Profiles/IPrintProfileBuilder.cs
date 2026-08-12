using FocusMed.Printing.Discovery;

namespace FocusMed.Printing.Profiles;

public interface IPrintProfileBuilder
{
    IReadOnlyList<PrintProfile> BuildProfiles(PrinterCapabilitySnapshot snapshot);
}
