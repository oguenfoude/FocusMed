using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Xml.Linq;

namespace FocusMed.PrintService.Services;

/// <summary>
/// Builds XPS packages from PNG image byte arrays.
/// XPS = OPC (ZIP) with FixedDocument/FixedPage structure.
/// Uses relationship IDs per XPS spec (ISO/IEC 29500-1).
/// </summary>
public static class XpsBuilder
{
    private static readonly XNamespace XpsNs = "http://schemas.microsoft.com/xps/2005/06";
    private static readonly XNamespace CtNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const string PtContentType = "application/vnd.ms-printing.printticket+xml";
    private const string PtRelType = "http://schemas.microsoft.com/xps/2005/06/printticket";
    private const string RelsContentType = "application/vnd.openxmlformats-package.relationships+xml";
    private const string FixedDocSeqContentType = "application/vnd.ms-xps.fixeddocseq+xml";
    private const string FixedDocContentType = "application/vnd.ms-xps.fixeddoc+xml";
    private const string FixedPageContentType = "application/vnd.ms-xps.fixedpage+xml";

    public static byte[] CreateXpsFromPngImages(
        IReadOnlyList<byte[]> imagePngBytes,
        int pageWidthPx,
        int pageHeightPx)
    {
        using var ms = new MemoryStream();
        using var pkg = Package.Open(ms, FileMode.Create, FileAccess.ReadWrite);

        WriteContentTypes(pkg);
        WritePackageRelationships(pkg);
        WriteDocumentSequence(pkg, imagePngBytes.Count);
        WriteDocument(pkg, imagePngBytes.Count);
        WritePages(pkg, imagePngBytes, pageWidthPx, pageHeightPx);

        pkg.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Injects a PrintTicket into an existing XPS package at the document sequence level.
    /// Builds PrintTicket XML manually (XmlSerializer cannot handle PrintTicket).
    /// </summary>
    public static byte[] InjectPrintTicket(byte[] xpsBytes, DuplexMode duplex, int copies, bool portrait = true)
    {
        using var ms = new MemoryStream(xpsBytes);
        using var pkg = Package.Open(ms, FileMode.Open, FileAccess.ReadWrite);

        // 1. Write PrintTicket XML part
        var ptXml = BuildPrintTicketXml(duplex, copies, portrait);
        var ptBytes = Encoding.UTF8.GetBytes(ptXml);

        var ptUri = new Uri("/PrintTicket.pt", UriKind.Relative);
        var existing = pkg.GetPart(ptUri);
        if (existing != null) pkg.DeletePart(ptUri);

        var ptPart = pkg.CreatePart(ptUri, PtContentType, CompressionOption.SuperFast);
        ptPart.GetStream().Write(ptBytes, 0, ptBytes.Length);

        // 2. Add relationship to package-level .rels (document sequence level)
        var relsUri = new Uri("/_rels/.rels", UriKind.Relative);
        var relsPart = pkg.GetPart(relsUri);
        using var relsStream = relsPart.GetStream();
        var relsXml = XElement.Load(relsStream);

        var hasPt = relsXml.Elements()
            .Any(r => ((string?)r.Attribute("Type"))?.Contains("printticket") == true);

        if (!hasPt)
        {
            relsXml.Add(new XElement(RelNs + "Relationship",
                new XAttribute("Id", $"Rpt{Guid.NewGuid():N}"),
                new XAttribute("Type", PtRelType),
                new XAttribute("Target", "PrintTicket.pt")));

            relsStream.Position = 0;
            relsStream.SetLength(0);
            relsXml.Save(relsStream);
        }

        pkg.Flush();
        return ms.ToArray();
    }

    private static void WriteContentTypes(Package pkg)
    {
        var xml = new XElement(CtNs + "Types",
            new XElement(CtNs + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(CtNs + "Default",
                new XAttribute("Extension", "png"),
                new XAttribute("ContentType", "image/png")),
            new XElement(CtNs + "Default",
                new XAttribute("Extension", "fpage"),
                new XAttribute("ContentType", "application/vnd.ms-xps.fixedpage+xml")),
            new XElement(CtNs + "Default",
                new XAttribute("Extension", "fdoc"),
                new XAttribute("ContentType", "application/vnd.ms-xps.fixeddoc+xml")),
            new XElement(CtNs + "Default",
                new XAttribute("Extension", "fdseq"),
                new XAttribute("ContentType", "application/vnd.ms-xps.fixeddocseq+xml")),
            new XElement(CtNs + "Default",
                new XAttribute("Extension", "pt"),
                new XAttribute("ContentType", PtContentType)));

        WriteXmlPart(pkg, new Uri("/[Content_Types].xml", UriKind.Relative), xml,
            "application/vnd.openxmlformats-package.content-types+xml");
    }

    private static void WritePackageRelationships(Package pkg)
    {
        var rels = new XElement(RelNs + "Relationships",
            new XElement(RelNs + "Relationship",
                new XAttribute("Id", "R1"),
                new XAttribute("Type", "http://schemas.microsoft.com/xps/2005/06/fixeddocseq"),
                new XAttribute("Target", "FixedDocumentSequence.fdseq")));

        WriteXmlPart(pkg, new Uri("/_rels/.rels", UriKind.Relative), rels, RelsContentType);
    }

    private static void WriteDocumentSequence(Package pkg, int pageCount)
    {
        var fdseq = new XElement(XpsNs + "FixedDocumentSequence",
            new XElement(XpsNs + "DocumentReference",
                new XAttribute("Source", "#Rdoc1")));

        WriteXmlPart(pkg, new Uri("/FixedDocumentSequence.fdseq", UriKind.Relative), fdseq, FixedDocSeqContentType);

        // Document sequence _rels
        var docSeqRels = new XElement(RelNs + "Relationships",
            new XElement(RelNs + "Relationship",
                new XAttribute("Id", "Rdoc1"),
                new XAttribute("Type", "http://schemas.microsoft.com/xps/2005/06/fixeddoc"),
                new XAttribute("Target", "Documents/1/FixedDocument.fdoc")));

        var docSeqRelsDir = new Uri("/FixedDocumentSequence.fdseq.rels", UriKind.Relative);
        var docSeqRelsPart = pkg.CreatePart(docSeqRelsDir,
            "application/vnd.openxmlformats-package.relationships+xml");
        docSeqRels.Save(docSeqRelsPart.GetStream());
    }

    private static void WriteDocument(Package pkg, int pageCount)
    {
        var fdoc = new XElement(XpsNs + "FixedDocument");
        for (int i = 1; i <= pageCount; i++)
        {
            fdoc.Add(new XElement(XpsNs + "PageContent",
                new XAttribute("Source", $"#Rpage{i}")));
        }

        WriteXmlPart(pkg, new Uri("/Documents/1/FixedDocument.fdoc", UriKind.Relative), fdoc, FixedDocContentType);

        // Document _rels
        var docRels = new XElement(RelNs + "Relationships");
        for (int i = 1; i <= pageCount; i++)
        {
            docRels.Add(new XElement(RelNs + "Relationship",
                new XAttribute("Id", $"Rpage{i}"),
                new XAttribute("Type", "http://schemas.microsoft.com/xps/2005/06/fixedpage"),
                new XAttribute("Target", $"Pages/FixedPage{i}.fpage")));
        }

        WriteXmlPart(pkg, new Uri("/Documents/1/_rels/FixedDocument.fdoc.rels", UriKind.Relative), docRels, RelsContentType);
    }

    private static void WritePages(Package pkg, IReadOnlyList<byte[]> images, int widthPx, int heightPx)
    {
        for (int i = 0; i < images.Count; i++)
        {
            var pageNum = i + 1;
            WritePage(pkg, pageNum, images[i], widthPx, heightPx);
        }
    }

    private static void WritePage(Package pkg, int pageNum, byte[] pngBytes, int widthPx, int heightPx)
    {
        // Image part
        var imgUri = new Uri($"/Documents/1/Resources/Images/Image_{pageNum}.png", UriKind.Relative);
        var imgPart = pkg.CreatePart(imgUri, "image/png", CompressionOption.SuperFast);
        imgPart.GetStream().Write(pngBytes, 0, pngBytes.Length);

        // FixedPage XML — use relationship ID for image
        var fixedPage = new XElement(XpsNs + "FixedPage",
            new XAttribute("Width", widthPx),
            new XAttribute("Height", heightPx),
            new XAttribute("xml:lang", "en"),
            new XElement(XpsNs + "Canvas",
                new XElement(XpsNs + "Path",
                    new XAttribute("Data", $"M 0,0 L {widthPx},0 {widthPx},{heightPx} 0,{heightPx} Z"),
                    new XElement(XpsNs + "Path.Fill",
                        new XElement(XpsNs + "ImageBrush",
                            new XAttribute("ImageSource", $"#Rimg{pageNum}"))))));

        WriteXmlPart(pkg, new Uri($"/Documents/1/Pages/FixedPage{pageNum}.fpage", UriKind.Relative), fixedPage, FixedPageContentType);

        // Page _rels — point to image via relationship
        var pageRels = new XElement(RelNs + "Relationships",
            new XElement(RelNs + "Relationship",
                new XAttribute("Id", $"Rimg{pageNum}"),
                new XAttribute("Type", "http://schemas.microsoft.com/xps/2005/06/image"),
                new XAttribute("Target", $"../Resources/Images/Image_{pageNum}.png")));

        WriteXmlPart(pkg, new Uri($"/Documents/1/Pages/_rels/FixedPage{pageNum}.fpage.rels", UriKind.Relative), pageRels, RelsContentType);
    }

    private static void WriteXmlPart(Package pkg, Uri uri, XElement xml, string contentType)
    {
        var part = pkg.CreatePart(uri, contentType, CompressionOption.SuperFast);
        using var stream = part.GetStream();
        xml.Save(stream);
    }

    private static string BuildPrintTicketXml(DuplexMode duplex, int copies, bool portrait)
    {
        var duplexValue = duplex switch
        {
            DuplexMode.Simplex => "psk:OneSided",
            DuplexMode.LongEdge => "psk:TwoSidedLongEdge",
            DuplexMode.ShortEdge => "psk:TwoSidedShortEdge",
            _ => "psk:OneSided"
        };

        var orientation = portrait ? "psk:Portrait" : "psk:Landscape";

        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<psf:PrintTicket
  xmlns:psf=""http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework""
  xmlns:psk=""http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords""
  xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
  Version=""1"">
  <psf:Feature Name=""psk:JobDuplexAllDocumentsContiguously"">
    <psf:Option Name=""{duplexValue}""/>
  </psf:Feature>
  <psf:Feature Name=""psk:PageOrientation"">
    <psf:Option Name=""{orientation}""/>
  </psf:Feature>
  <psf:ParameterInit Name=""psk:JobCopyCount"">
    <psf:Value>{copies}</psf:Value>
  </psf:ParameterInit>
</psf:PrintTicket>";
    }
}

public enum DuplexMode
{
    Simplex,
    LongEdge,
    ShortEdge
}
