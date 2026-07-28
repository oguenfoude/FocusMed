using System.IO;
using System.IO.Packaging;
using System.Printing;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace FocusMed.PrintService.Services;

/// <summary>
/// Builds XPS packages from PNG image byte arrays.
/// XPS = OPC (ZIP) with FixedDocument/FixedPage structure.
/// The driver receives XPS instead of PDF — no DEVMODE quirks.
/// </summary>
public static class XpsBuilder
{
    private static readonly XNamespace XpsNs = "http://schemas.microsoft.com/xps/2005/06";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace CtNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static byte[] CreateXpsFromPngImages(
        IReadOnlyList<byte[]> imagePngBytes,
        int pageWidthPx,
        int pageHeightPx)
    {
        using var ms = new MemoryStream();
        using var pkg = Package.Open(ms, FileMode.Create, FileAccess.ReadWrite);

        WriteContentTypes(pkg);
        WriteDocumentSequence(pkg);
        WriteDocumentRelationships(pkg, imagePngBytes.Count);
        WriteDocument(pkg, imagePngBytes.Count);

        for (int i = 0; i < imagePngBytes.Count; i++)
        {
            WritePage(pkg, i + 1, imagePngBytes[i], pageWidthPx, pageHeightPx);
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
                new XAttribute("ContentType", "application/vnd.ms-xps.fixeddocseq+xml")));

        WriteXmlPart(pkg, new Uri("/[Content_Types].xml", UriKind.Relative),
            xml, "application/vnd.openxmlformats-package.content-types+xml");
    }

    private static void WriteDocumentSequence(Package pkg)
    {
        var rels = new XElement("Relationships",
            CreateRel("Documents/1/FixedDocument.fdoc", "http://schemas.microsoft.com/xps/2005/06/fixeddoc"));

        WriteXmlPart(pkg, new Uri("/_rels/.rels", UriKind.Relative),
            rels, "application/vnd.openxmlformats-package.relationships+xml");

        var fdseq = new XElement(XpsNs + "FixedDocumentSequence",
            new XElement(XpsNs + "DocumentReference",
                new XAttribute("Source", "Documents/1/FixedDocument.fdoc")));

        WriteXmlPart(pkg, new Uri("/FixedDocumentSequence.fdseq", UriKind.Relative),
            fdseq, "application/vnd.ms-xps.fixeddocseq+xml");
    }

    private static void WriteDocumentRelationships(Package pkg, int pageCount)
    {
        var rels = new XElement("Relationships");
        for (int i = 1; i <= pageCount; i++)
        {
            rels.Add(CreateRel($"Pages/FixedPage{i}.fpage", "http://schemas.microsoft.com/xps/2005/06/fixedpage"));
        }

        WriteXmlPart(pkg, new Uri("/Documents/1/_rels/FixedDocument.fdoc.rels", UriKind.Relative),
            rels, "application/vnd.openxmlformats-package.relationships+xml");

        var fdoc = new XElement(XpsNs + "FixedDocument");
        for (int i = 1; i <= pageCount; i++)
        {
            fdoc.Add(new XElement(XpsNs + "PageContent",
                new XAttribute("Source", $"Pages/FixedPage{i}.fpage")));
        }

        WriteXmlPart(pkg, new Uri("/Documents/1/FixedDocument.fdoc", UriKind.Relative),
            fdoc, "application/vnd.ms-xps.fixeddoc+xml");
    }

    private static void WriteDocument(Package pkg, int pageCount)
    {
        var fdseq = new XElement(XpsNs + "FixedDocumentSequence",
            new XElement(XpsNs + "DocumentReference",
                new XAttribute("Source", "Documents/1/FixedDocument.fdoc")));

        WriteXmlPart(pkg, new Uri("/FixedDocumentSequence.fdseq", UriKind.Relative),
            fdseq, "application/vnd.ms-xps.fixeddocseq+xml");
    }

    private static void WritePage(Package pkg, int pageNum, byte[] pngBytes, int widthPx, int heightPx)
    {
        var imageUri = new Uri($"/Documents/1/Resources/Images/Image_{pageNum}.png", UriKind.Relative);
        var imagePart = pkg.CreatePart(imageUri, "image/png", CompressionOption.SuperFast);
        imagePart.GetStream().Write(pngBytes, 0, pngBytes.Length);

        var pageRels = new XElement("Relationships",
            CreateRel($"../Resources/Images/Image_{pageNum}.png", "http://schemas.microsoft.com/xps/2005/06/image"));

        WriteXmlPart(pkg, new Uri($"/Documents/1/Pages/_rels/FixedPage{pageNum}.fpage.rels", UriKind.Relative),
            pageRels, "application/vnd.openxmlformats-package.relationships+xml");

        var fixedPage = new XElement(XpsNs + "FixedPage",
            new XAttribute("Width", widthPx),
            new XAttribute("Height", heightPx),
            new XAttribute("xml:lang", "en"),
            new XElement(XpsNs + "Canvas",
                new XElement(XpsNs + "Path",
                    new XAttribute("Data", $"M 0,0 L {widthPx},0 {widthPx},{heightPx} 0,{heightPx} Z"),
                    new XElement(XpsNs + "Path.Fill",
                        new XElement(XpsNs + "ImageBrush",
                            new XAttribute("ImageSource", $"../Resources/Images/Image_{pageNum}.png"))))));

        WriteXmlPart(pkg, new Uri($"/Documents/1/Pages/FixedPage{pageNum}.fpage", UriKind.Relative),
            fixedPage, "application/vnd.ms-xps.fixedpage+xml");
    }

    private static XElement CreateRel(string target, string type)
    {
        return new XElement("Relationship",
            new XAttribute("Target", target),
            new XAttribute("TargetMode", "Internal"),
            new XAttribute("Type", type),
            new XAttribute("Id", $"R{Guid.NewGuid():N}"));
    }

    private static void WriteXmlPart(Package pkg, Uri uri, XElement xml, string contentType)
    {
        var part = pkg.CreatePart(uri, contentType, CompressionOption.SuperFast);
        using var stream = part.GetStream();
        xml.Save(stream);
    }

    /// <summary>
    /// Injects a PrintTicket into an existing XPS package at the document sequence level.
    /// This controls duplex, copies, page orientation, etc. at the spooler level.
    /// </summary>
    public static byte[] InjectPrintTicket(byte[] xpsBytes, PrintTicket ticket)
    {
        using var ms = new MemoryStream(xpsBytes);
        using var pkg = Package.Open(ms, FileMode.Open, FileAccess.ReadWrite);

        using var ticketMs = new MemoryStream();
        var serializer = new XmlSerializer(typeof(PrintTicket));
        serializer.Serialize(ticketMs, ticket);
        var ticketXml = Encoding.UTF8.GetString(ticketMs.ToArray());

        var ticketPartUri = new Uri("/PrintTicket.pt", UriKind.Relative);

        var existingPart = pkg.GetPart(ticketPartUri);
        if (existingPart != null)
        {
            pkg.DeletePart(ticketPartUri);
        }

        var part = pkg.CreatePart(ticketPartUri,
            "application/vnd.ms-printing.printticket+xml",
            CompressionOption.SuperFast);
        using var stream = part.GetStream();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(ticketXml);

        var relsUri = new Uri("/_rels/.rels", UriKind.Relative);
        var relsPart = pkg.GetPart(relsUri);
        if (relsPart != null)
        {
            using var relsStream = relsPart.GetStream();
            var relsXml = XElement.Load(relsStream);
            var relsNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");

            var hasPt = relsXml.Elements()
                .Any(r => r.Attribute("Type")?.Value?.Contains("printticket") == true);

            if (!hasPt)
            {
                relsXml.Add(new XElement(relsNs + "Relationship",
                    new XAttribute("Target", "PrintTicket.pt"),
                    new XAttribute("TargetMode", "Internal"),
                    new XAttribute("Type", "http://schemas.microsoft.com/xps/2005/06/printticket"),
                    new XAttribute("Id", $"R{Guid.NewGuid():N}")));

                relsStream.Position = 0;
                relsStream.SetLength(0);
                relsXml.Save(relsStream);
            }
        }

        pkg.Flush();
        return ms.ToArray();
    }
}
