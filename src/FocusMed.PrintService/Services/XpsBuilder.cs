using System.IO;
using System.IO.Packaging;
using System.Text;

namespace FocusMed.PrintService.Services;

public static class XpsBuilder
{
    private const string XpsNs = "http://schemas.microsoft.com/xps/2005/06";
    private const string PtContentType = "application/vnd.ms-printing.printticket+xml";
    private const string PtRelType = "http://schemas.microsoft.com/xps/2005/06/printticket";

    public static byte[] CreateXpsFromPngImages(
        IReadOnlyList<byte[]> imagePngBytes,
        int pageWidthPx,
        int pageHeightPx)
    {
        using var ms = new MemoryStream();
        using var pkg = Package.Open(ms, FileMode.Create, FileAccess.ReadWrite);

        WritePart(pkg, "/[Content_Types].xml",
            "application/vnd.openxmlformats-package.content-types+xml",
            BuildContentTypes(imagePngBytes.Count));

        WritePart(pkg, "/_rels/.rels",
            "application/vnd.openxmlformats-package.relationships+xml",
            BuildPackageRels());

        WritePart(pkg, "/FixedDocumentSequence.fdseq",
            "application/vnd.ms-xps.fixeddocseq+xml",
            BuildFixedDocSeq());

        WritePart(pkg, "/FixedDocumentSequence.fdseq.rels",
            "application/vnd.openxmlformats-package.relationships+xml",
            BuildDocSeqRels());

        WritePart(pkg, "/Documents/1/FixedDocument.fdoc",
            "application/vnd.ms-xps.fixeddoc+xml",
            BuildFixedDoc(imagePngBytes.Count));

        WritePart(pkg, "/Documents/1/_rels/FixedDocument.fdoc.rels",
            "application/vnd.openxmlformats-package.relationships+xml",
            BuildFixedDocRels(imagePngBytes.Count));

        for (int i = 0; i < imagePngBytes.Count; i++)
        {
            var num = i + 1;
            WritePart(pkg, $"/Documents/1/Resources/Images/Image_{num}.png",
                "image/png", imagePngBytes[i]);

            WritePart(pkg, $"/Documents/1/Pages/FixedPage{num}.fpage",
                "application/vnd.ms-xps.fixedpage+xml",
                BuildFixedPage(num, pageWidthPx, pageHeightPx));

            WritePart(pkg, $"/Documents/1/Pages/_rels/FixedPage{num}.fpage.rels",
                "application/vnd.openxmlformats-package.relationships+xml",
                BuildPageRels(num));
        }

        pkg.Flush();
        return ms.ToArray();
    }

    public static byte[] InjectPrintTicket(byte[] xpsBytes, DuplexMode duplex, int copies, bool portrait = true)
    {
        using var ms = new MemoryStream(xpsBytes);
        using var pkg = Package.Open(ms, FileMode.Open, FileAccess.ReadWrite);

        var ptBytes = Encoding.UTF8.GetBytes(BuildPrintTicket(duplex, copies, portrait));

        var ptUri = new Uri("/PrintTicket.pt", UriKind.Relative);
        var existing = pkg.GetPart(ptUri);
        if (existing != null) pkg.DeletePart(ptUri);

        var ptPart = pkg.CreatePart(ptUri, PtContentType, CompressionOption.SuperFast);
        ptPart.GetStream().Write(ptBytes, 0, ptBytes.Length);

        var relsUri = new Uri("/_rels/.rels", UriKind.Relative);
        var relsPart = pkg.GetPart(relsUri);
        using var relsStream = relsPart.GetStream();
        var relsText = new StreamReader(relsStream, Encoding.UTF8).ReadToEnd();

        if (!relsText.Contains("printticket", StringComparison.OrdinalIgnoreCase))
        {
            var relId = $"Rpt{Guid.NewGuid():N}";
            var newRel = $@"<Relationship xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"" Id=""{relId}"" Type=""{PtRelType}"" Target=""PrintTicket.pt""/>";
            var insertPos = relsText.LastIndexOf("</Relationships>");
            relsText = relsText.Insert(insertPos, newRel);
        }

        var relsBytes = Encoding.UTF8.GetBytes(relsText);
        relsStream.Position = 0;
        relsStream.SetLength(0);
        relsStream.Write(relsBytes, 0, relsBytes.Length);

        pkg.Flush();
        return ms.ToArray();
    }

    private static void WritePart(Package pkg, string partPath, string contentType, string xml)
    {
        var part = pkg.CreatePart(new Uri(partPath, UriKind.Relative), contentType, CompressionOption.SuperFast);
        var bytes = Encoding.UTF8.GetBytes(xml);
        part.GetStream().Write(bytes, 0, bytes.Length);
    }

    private static void WritePart(Package pkg, string partPath, string contentType, byte[] data)
    {
        var part = pkg.CreatePart(new Uri(partPath, UriKind.Relative), contentType, CompressionOption.SuperFast);
        part.GetStream().Write(data, 0, data.Length);
    }

    private static string BuildContentTypes(int pageCount)
    {
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""png"" ContentType=""image/png""/>
  <Default Extension=""fpage"" ContentType=""application/vnd.ms-xps.fixedpage+xml""/>
  <Default Extension=""fdoc"" ContentType=""application/vnd.ms-xps.fixeddoc+xml""/>
  <Default Extension=""fdseq"" ContentType=""application/vnd.ms-xps.fixeddocseq+xml""/>
  <Default Extension=""pt"" ContentType=""{PtContentType}""/>
</Types>";
    }

    private static string BuildPackageRels()
    {
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""R1"" Type=""{XpsNs}/fixeddocseq"" Target=""FixedDocumentSequence.fdseq""/>
</Relationships>";
    }

    private static string BuildFixedDocSeq()
    {
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<FixedDocumentSequence xmlns=""{XpsNs}"">
  <DocumentReference Source=""#Rdoc1""/>
</FixedDocumentSequence>";
    }

    private static string BuildDocSeqRels()
    {
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""Rdoc1"" Type=""{XpsNs}/fixeddoc"" Target=""Documents/1/FixedDocument.fdoc""/>
</Relationships>";
    }

    private static string BuildFixedDoc(int pageCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($@"<?xml version=""1.0"" encoding=""utf-8""?>
<FixedDocument xmlns=""{XpsNs}"">");
        for (int i = 1; i <= pageCount; i++)
            sb.AppendLine($@"  <PageContent Source=""#Rpage{i}""/>");
        sb.Append("</FixedDocument>");
        return sb.ToString();
    }

    private static string BuildFixedDocRels(int pageCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($@"<?xml version=""1.0"" encoding=""utf-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">");
        for (int i = 1; i <= pageCount; i++)
            sb.AppendLine($@"  <Relationship Id=""Rpage{i}"" Type=""{XpsNs}/fixedpage"" Target=""Pages/FixedPage{i}.fpage""/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string BuildFixedPage(int pageNum, int widthPx, int heightPx)
    {
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<FixedPage xmlns=""{XpsNs}"" Width=""{widthPx}"" Height=""{heightPx}"" xml:lang=""en"">
  <Canvas>
    <Path Data=""M 0,0 L {widthPx},0 {widthPx},{heightPx} 0,{heightPx} Z"">
      <Path.Fill>
        <ImageBrush ImageSource=""#Rimg{pageNum}""/>
      </Path.Fill>
    </Path>
  </Canvas>
</FixedPage>";
    }

    private static string BuildPageRels(int pageNum)
    {
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""Rimg{pageNum}"" Type=""{XpsNs}/image"" Target=""../Resources/Images/Image_{pageNum}.png""/>
</Relationships>";
    }

    private static string BuildPrintTicket(DuplexMode duplex, int copies, bool portrait)
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
