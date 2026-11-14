using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;

namespace NoHalluTrans.Infrastructure;

/// <summary>Produces a vector PDF inside the task workspace before publication.</summary>
public static class PdfComposer
{
    private readonly record struct PagePlacement(int SourceIndex, double Left, double Bottom, double Scale, double OriginX, double OriginY);

    public static string Compose(string translated, string? original, string layout, string workDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (layout == "target-only") return translated;
        if (layout is not ("alternating" or "side-by-side")) throw new ArgumentException("不支持的 PDF 格式。");
        if (original is null || !File.Exists(original)) throw new IOException("原文 PDF 构建失败，无法生成双语 PDF。请选择仅目标语言，或修复原文后重试。");
        string destination = Path.Combine(workDirectory, "composed.pdf");
        using var source = PdfReader.Open(original, PdfDocumentOpenMode.Import);
        using var target = PdfReader.Open(translated, PdfDocumentOpenMode.Import);
        using var result = new PdfDocument();
        result.Info.Title = layout == "alternating" ? "原文与译文交替页" : "原文与译文并排";

        var sourcePages = BuildPageIndex(source);
        var targetPages = BuildPageIndex(target);
        var sourceDests = ReadNamedDestinations(source);
        var targetDests = ReadNamedDestinations(target);

        if (layout == "alternating")
        {
            int count = Math.Max(source.PageCount, target.PageCount);
            List<(PdfPage SourcePage, PdfPage ResultPage, PagePlacement Placement, bool IsSource)> placed = [];
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                Append(source, target, i, true);
                Append(target, source, i, false);
            }
            foreach (var item in placed)
            {
                StripLinkAnnotations(item.ResultPage);
                CopyLinks(result, item.SourcePage, item.ResultPage, item.Placement,
                    item.IsSource ? sourcePages : targetPages,
                    item.IsSource ? sourceDests : targetDests,
                    idx => new PagePlacement(idx, 0, 0, 1, Origin(item.IsSource ? source.Pages[idx] : target.Pages[idx]).X, Origin(item.IsSource ? source.Pages[idx] : target.Pages[idx]).Y),
                    idx => item.IsSource ? idx * 2 : idx * 2 + 1);
            }
            token.ThrowIfCancellationRequested();
            result.Save(destination);
            return destination;

            void Append(PdfDocument document, PdfDocument counterpart, int index, bool isSource)
            {
                if (index < document.PageCount)
                {
                    var srcPage = document.Pages[index];
                    var added = result.AddPage(srcPage);
                    var origin = Origin(srcPage);
                    placed.Add((srcPage, added, new(index, 0, 0, 1, origin.X, origin.Y), isSource));
                }
                else
                {
                    var blank = result.AddPage();
                    var reference = counterpart.Pages[index];
                    blank.Width = XUnit.FromPoint(Width(reference)); blank.Height = XUnit.FromPoint(Height(reference));
                }
            }
        }
        else
        {
            using var sourceForm = XPdfForm.FromFile(original);
            using var targetForm = XPdfForm.FromFile(translated);
            int count = Math.Max(source.PageCount, target.PageCount);
            var sourcePlacements = new PagePlacement?[source.PageCount];
            var targetPlacements = new PagePlacement?[target.PageCount];
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                // Equal slots preserve a stable original-left / translation-right alignment.
                var left = source.Pages[Math.Min(i, source.PageCount - 1)];
                var right = target.Pages[Math.Min(i, target.PageCount - 1)];
                double width = Math.Max(Width(left), Width(right));
                double height = Math.Max(Height(left), Height(right));
                var page = result.AddPage();
                page.Width = XUnit.FromPoint(width * 2); page.Height = XUnit.FromPoint(height);
                using var graphics = XGraphics.FromPdfPage(page);
                if (i < source.PageCount) sourcePlacements[i] = Draw(sourceForm, source.Pages[i], i, 0, graphics, width, height);
                if (i < target.PageCount) targetPlacements[i] = Draw(targetForm, target.Pages[i], i, width, graphics, width, height);
            }
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var page = result.Pages[i];
                if (i < source.PageCount && sourcePlacements[i] is { } sp)
                    CopyLinks(result, source.Pages[i], page, sp, sourcePages, sourceDests, idx => sourcePlacements[idx], idx => idx);
                if (i < target.PageCount && targetPlacements[i] is { } tp)
                    CopyLinks(result, target.Pages[i], page, tp, targetPages, targetDests, idx => targetPlacements[idx], idx => idx);
            }
            token.ThrowIfCancellationRequested();
            result.Save(destination);
            return destination;

            static PagePlacement Draw(XPdfForm form, PdfPage srcPage, int index, double x, XGraphics graphics, double slotWidth, double slotHeight)
            {
                form.PageNumber = index + 1;
                double scale = Math.Min(slotWidth / form.PointWidth, slotHeight / form.PointHeight);
                double w = form.PointWidth * scale, h = form.PointHeight * scale;
                double drawX = x + (slotWidth - w) / 2;
                double drawY = (slotHeight - h) / 2;
                graphics.DrawImage(form, drawX, drawY, w, h);
                var origin = Origin(srcPage);
                return new(index, drawX, drawY, scale, origin.X, origin.Y);
            }
        }
    }

    private static (double X, double Y) Origin(PdfPage page)
    {
        var box = page.CropBox.IsZero ? page.MediaBox : page.CropBox;
        return (box.X1, box.Y1);
    }

    private static Dictionary<PdfReference, int> BuildPageIndex(PdfDocument doc)
    {
        Dictionary<PdfReference, int> map = [];
        for (int i = 0; i < doc.PageCount; i++)
            if (doc.Pages[i].Reference is { } reference) map[reference] = i;
        return map;
    }

    private static Dictionary<string, PdfArray> ReadNamedDestinations(PdfDocument doc)
    {
        Dictionary<string, PdfArray> dests = new(StringComparer.Ordinal);
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements.GetDictionary("/Dests") is { } directDests)
        {
            foreach (string key in directDests.Elements.Keys)
                if (ResolveDestArray(directDests.Elements[key]) is { } arr) dests[key.TrimStart('/')] = arr;
        }
        if (catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests") is { } nameTree)
            WalkNameTree(nameTree, dests);
        return dests;
    }

    private static void WalkNameTree(PdfDictionary node, Dictionary<string, PdfArray> dests)
    {
        if (node.Elements.GetArray("/Names") is { } names)
        {
            for (int i = 0; i + 1 < names.Elements.Count; i += 2)
            {
                if (Dereference(names.Elements[i]) is PdfString key && ResolveDestArray(names.Elements[i + 1]) is { } arr)
                    dests[key.Value] = arr;
            }
        }
        if (node.Elements.GetArray("/Kids") is { } kids)
        {
            foreach (var kid in kids.Elements)
                if (Dereference(kid) is PdfDictionary child) WalkNameTree(child, dests);
        }
    }

    private static PdfArray? ResolveDestArray(PdfItem? item)
    {
        item = Dereference(item);
        if (item is PdfArray arr) return arr;
        if (item is PdfDictionary dict) return Dereference(dict.Elements["/D"]) as PdfArray;
        return null;
    }

    private static PdfItem? Dereference(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static void StripLinkAnnotations(PdfPage page)
    {
        var annots = page.Elements.GetArray("/Annots");
        if (annots is null) return;
        for (int i = annots.Elements.Count - 1; i >= 0; i--)
        {
            if (Dereference(annots.Elements[i]) is PdfDictionary dict && dict.Elements.GetName("/Subtype") == "/Link")
                annots.Elements.RemoveAt(i);
        }
    }

    private static void CopyLinks(
        PdfDocument result,
        PdfPage srcPage,
        PdfPage resultPage,
        PagePlacement placement,
        Dictionary<PdfReference, int> pageIndexMap,
        Dictionary<string, PdfArray> namedDests,
        Func<int, PagePlacement?> destPlacementLookup,
        Func<int, int> resultPageIndexLookup)
    {
        var annots = srcPage.Elements.GetArray("/Annots");
        if (annots is null) return;
        foreach (var item in annots.Elements)
        {
            if (Dereference(item) is not PdfDictionary annot || annot.Elements.GetName("/Subtype") != "/Link") continue;
            if (Dereference(annot.Elements["/Rect"]) is not PdfArray rectArr || rectArr.Elements.Count < 4) continue;
            if (!TryDouble(rectArr.Elements[0], out double x1) || !TryDouble(rectArr.Elements[1], out double y1) ||
                !TryDouble(rectArr.Elements[2], out double x2) || !TryDouble(rectArr.Elements[3], out double y2)) continue;

            double nx1 = placement.Left + (Math.Min(x1, x2) - placement.OriginX) * placement.Scale;
            double ny1 = placement.Bottom + (Math.Min(y1, y2) - placement.OriginY) * placement.Scale;
            double nx2 = placement.Left + (Math.Max(x1, x2) - placement.OriginX) * placement.Scale;
            double ny2 = placement.Bottom + (Math.Max(y1, y2) - placement.OriginY) * placement.Scale;
            var rect = new PdfRectangle(new XPoint(nx1, ny1), new XPoint(nx2, ny2));

            if (Dereference(annot.Elements["/A"]) is PdfDictionary action)
            {
                string subtype = action.Elements.GetName("/S");
                if (subtype == "/URI")
                {
                    string uri = action.Elements.GetString("/URI");
                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        var webLink = PdfLinkAnnotation.CreateWebLink(rect, uri);
                        webLink.Elements["/Border"] = new PdfArray(result, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
                        resultPage.Annotations.Add(webLink);
                    }
                    continue;
                }
                if (subtype == "/GoTo")
                {
                    AddGoToLink(result, resultPage, rect, action.Elements["/D"], pageIndexMap, namedDests, destPlacementLookup, resultPageIndexLookup);
                    continue;
                }
            }
            if (annot.Elements["/Dest"] is { } dest)
                AddGoToLink(result, resultPage, rect, dest, pageIndexMap, namedDests, destPlacementLookup, resultPageIndexLookup);
        }
    }

    private static void AddGoToLink(
        PdfDocument result,
        PdfPage resultPage,
        PdfRectangle rect,
        PdfItem? destItem,
        Dictionary<PdfReference, int> pageIndexMap,
        Dictionary<string, PdfArray> namedDests,
        Func<int, PagePlacement?> destPlacementLookup,
        Func<int, int> resultPageIndexLookup)
    {
        var resolved = Dereference(destItem);
        PdfArray? destArr = resolved switch
        {
            PdfArray a => a,
            PdfString s when namedDests.TryGetValue(s.Value, out var a) => a,
            PdfName n when namedDests.TryGetValue(n.Value.TrimStart('/'), out var a) => a,
            _ => null
        };
        if (destArr is null || destArr.Elements.Count == 0) return;
        if (destArr.Elements[0] is not PdfReference pageRef || !pageIndexMap.TryGetValue(pageRef, out int srcDestIdx)) return;
        int resultDestIdx = resultPageIndexLookup(srcDestIdx);
        if (resultDestIdx < 0 || resultDestIdx >= result.PageCount) return;

        var link = PdfLinkAnnotation.CreateDocumentLink(rect, resultDestIdx + 1);
        link.Elements["/Border"] = new PdfArray(result, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
        if (destPlacementLookup(srcDestIdx) is { } dp &&
            destArr.Elements.Count >= 4 &&
            Dereference(destArr.Elements[1]) is PdfName mode &&
            mode.Value == "/XYZ" &&
            result.Pages[resultDestIdx].Reference is { } destResultRef)
        {
            PdfItem dxItem = TryDouble(destArr.Elements[2], out double dx)
                ? new PdfReal(dp.Left + (dx - dp.OriginX) * dp.Scale)
                : PdfNull.Value;
            PdfItem dyItem = TryDouble(destArr.Elements[3], out double dy)
                ? new PdfReal(dp.Bottom + (dy - dp.OriginY) * dp.Scale)
                : PdfNull.Value;
            link.Elements["/Dest"] = new PdfArray(result, destResultRef, new PdfName("/XYZ"), dxItem, dyItem, new PdfReal(0));
        }
        resultPage.Annotations.Add(link);
    }

    private static bool TryDouble(PdfItem? item, out double value)
    {
        item = Dereference(item);
        switch (item)
        {
            case PdfReal r: value = r.Value; return true;
            case PdfInteger n: value = n.Value; return true;
            default: value = 0; return false;
        }
    }

    private static double Width(PdfPage page) => page.Rotate % 180 == 0 ? page.Width.Point : page.Height.Point;
    private static double Height(PdfPage page) => page.Rotate % 180 == 0 ? page.Height.Point : page.Width.Point;
}
