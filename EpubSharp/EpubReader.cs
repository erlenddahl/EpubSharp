using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using EpubSharp.Format;
using EpubSharp.Format.Readers;

namespace EpubSharp
{
    public static class EpubReader
    {
        public static EpubBook Read(string filePath, Encoding encoding = null)
        {
            if (filePath == null) throw new ArgumentNullException(nameof(filePath));
            if (encoding == null) encoding = Constants.DefaultEncoding;

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Specified epub file not found.", filePath);
            }

            return Read(File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read), false, encoding);
        }

        public static EpubBook Read(byte[] epubData, Encoding encoding = null)
        {
            if (encoding == null) encoding = Constants.DefaultEncoding;
            return Read(new MemoryStream(epubData), false, encoding);
        }

        public static EpubBook Read(Stream stream, bool leaveOpen, Encoding encoding = null, bool metaDataOnly = false)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (encoding == null) encoding = Constants.DefaultEncoding;

            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen, encoding))
            {
                var format = new EpubFormat { Ocf = OcfReader.Read(archive.LoadXml(Constants.OcfPath)) };

                format.Paths.OcfAbsolutePath = Constants.OcfPath;

                format.Paths.OpfAbsolutePath = format.Ocf.RootFilePath;
                if (format.Paths.OpfAbsolutePath == null)
                {
                    throw new EpubParseException("Epub OCF doesn't specify a root file.");
                }

                format.Opf = OpfReader.Read(archive.LoadXml(format.Paths.OpfAbsolutePath));

                if (metaDataOnly)
                    return new EpubBook {Format = format};

                var navPath = format.Opf.FindNavPath();
                if (navPath != null)
                {
                    format.Paths.NavAbsolutePath = navPath.ToAbsolutePath(format.Paths.OpfAbsolutePath);
                    format.Nav = NavReader.Read(archive.LoadHtml(format.Paths.NavAbsolutePath));
                }

                var ncxPath = format.Opf.FindNcxPath();
                if (ncxPath != null)
                {
                    format.Paths.NcxAbsolutePath = ncxPath.ToAbsolutePath(format.Paths.OpfAbsolutePath);
                    format.Ncx = NcxReader.Read(archive.LoadXml(format.Paths.NcxAbsolutePath));
                }

                var book = new EpubBook { Format = format };
                book.Resources = LoadResources(archive, book);
                book.SpecialResources = LoadSpecialResources(archive, book);

                book.CoverImage = LoadCoverImage(book);

                book.TableOfContents = LoadChapters(book);
                book.FileSize = stream.Length;
                return book;
            }
        }

        private static EpubByteFile LoadCoverImage(EpubBook book)
        {
            if (book == null) throw new ArgumentNullException(nameof(book));
            if (book.Format == null) throw new ArgumentNullException(nameof(book.Format));

            // Direct image cover via meta name="cover"
            // or EPUB 3 properties="cover-image".
            var coverPath = book.Format.Opf.FindCoverPath();

            if (!string.IsNullOrWhiteSpace(coverPath))
            {
                var directCoverImage = book.Resources.Images
                    .SingleOrDefault(e => e.Href == coverPath);

                if (directCoverImage != null)
                {
                    return directCoverImage;
                }
            }

            // Fallback if above properties are missing, and cover is an XHTML/SVG page.
            var fileFromCoverPage = FindCoverImageFromCoverPage(book);
            if (fileFromCoverPage != null) return fileFromCoverPage;

            // Or if any of the first spine items is a cover page
            var fileFromFirstSpineImagePage = FindCoverFromFirstSpineImagePage(book);
            if (fileFromFirstSpineImagePage != null) return fileFromFirstSpineImagePage;

            // Final fallback: just look for an image with "cover" in the filename
            return book.Resources.Images.FirstOrDefault(p => p.AbsolutePath.ToLower().Contains("cover") || p.Href.ToLower().Contains("cover"))
                   ?? book.Resources.Images.OrderBy(p => p.AbsolutePath).FirstOrDefault();
        }

        private static EpubByteFile FindCoverFromFirstSpineImagePage(EpubBook book)
        {
            var firstSpineItems = book.Format.Opf.Spine.ItemRefs.Take(3);

            foreach (var spineItem in firstSpineItems)
            {
                var manifestItem = book.Format.Opf.Manifest.Items.FirstOrDefault(item => item.Id == spineItem.IdRef);

                if (manifestItem?.MediaType != "application/xhtml+xml") continue;

                var htmlFile = book.Resources.Html.FirstOrDefault(file => file.Href == manifestItem.Href);
                if (htmlFile?.Content == null) continue;

                var imageHref = FindFirstImageHrefInHtml(htmlFile.TextContent);

                if (string.IsNullOrWhiteSpace(imageHref)) continue;

                var htmlAbsolutePath = manifestItem.Href.ToAbsolutePath(book.Format.Paths.OpfAbsolutePath);
                var imageAbsolutePath = imageHref.ToAbsolutePath(htmlAbsolutePath);

                var imageFile = book.Resources.Images.FirstOrDefault(file => file.AbsolutePath == imageAbsolutePath);

                if (imageFile != null && LooksLikeCoverImagePage(htmlFile.TextContent))
                {
                    return imageFile;
                }
            }

            return null;
        }

        private static bool LooksLikeCoverImagePage(string html)
        {
            try
            {
                var document = XDocument.Parse(html);

                XNamespace xhtml = "http://www.w3.org/1999/xhtml";
                XNamespace svg = "http://www.w3.org/2000/svg";

                var imageCount =
                    document.Descendants(xhtml + "img").Count() +
                    document.Descendants(svg + "image").Count();

                var text = string.Concat(
                    document
                        .DescendantNodes()
                        .OfType<XText>()
                        .Select(textNode => textNode.Value)
                ).Trim();

                return imageCount == 1 && text.Length < 100;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        private static EpubByteFile FindCoverImageFromCoverPage(EpubBook book)
        {
            var coverPageHref = book.Format.Opf.FindCoverPagePath();

            if (string.IsNullOrWhiteSpace(coverPageHref)) return null;

            var coverPage = book.Resources.Html.SingleOrDefault(e => e.Href == coverPageHref);
            if (coverPage?.Content != null)
            {
                var imageHref = FindFirstImageHrefInHtml(coverPage.TextContent);

                if (string.IsNullOrWhiteSpace(imageHref)) return null;

                var coverPageAbsolutePath = coverPageHref.ToAbsolutePath(book.Format.Paths.OpfAbsolutePath);
                var imageAbsolutePath = imageHref.ToAbsolutePath(coverPageAbsolutePath);
                var coverImageFile = book.Resources.Images.SingleOrDefault(e => e.AbsolutePath == imageAbsolutePath);

                return coverImageFile;
            }

            return null;
        }

        private static string FindFirstImageHrefInHtml(string html)
        {
            try
            {
                var document = XDocument.Parse(html);

                XNamespace xhtml = "http://www.w3.org/1999/xhtml";
                XNamespace svg = "http://www.w3.org/2000/svg";
                XNamespace xlink = "http://www.w3.org/1999/xlink";

                var img = document
                    .Descendants(xhtml + "img")
                    .FirstOrDefault(e => e.Attribute("src") != null);

                if (img != null)
                {
                    return (string)img.Attribute("src");
                }

                var svgImage = document
                    .Descendants(svg + "image")
                    .FirstOrDefault(e =>
                        e.Attribute("href") != null ||
                        e.Attribute(xlink + "href") != null);

                if (svgImage != null)
                {
                    return
                        (string)svgImage.Attribute("href") ??
                        (string)svgImage.Attribute(xlink + "href");
                }
            }
            catch (Exception ex)
            {
                return null;
            }

            return null;
        }

        private static List<EpubChapter> LoadChapters(EpubBook book)
        {
            if (book.Format.Nav != null)
            {
                var tocNav = book.Format.Nav.Body.Navs.SingleOrDefault(e => e.Type == NavNav.Attributes.TypeValues.Toc);
                if (tocNav != null)
                {
                    return LoadChaptersFromNav(book.Format.Paths.NavAbsolutePath, tocNav.Dom);
                }
            }

            if (book.Format.Ncx != null)
            {
                return LoadChaptersFromNcx(book.Format.Paths.NcxAbsolutePath, book.Format.Ncx.NavMap.NavPoints);
            }

            return new List<EpubChapter>();
        }

        private static List<EpubChapter> LoadChaptersFromNav(string navAbsolutePath, XElement element, EpubChapter parentChapter = null)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            var ns = element.Name.Namespace;

            var result = new List<EpubChapter>();
            var previous = parentChapter;

            var ol = element.Element(ns + NavElements.Ol);
            if (ol == null)
                return result;

            foreach (var li in ol.Elements(ns + NavElements.Li))
            {
                var chapter = new EpubChapter
                {
                    Parent = parentChapter,
                    Previous = previous
                };

                if (previous != null)
                    previous.Next = chapter;

                var link = li.Element(ns + NavElements.A);
                if (link != null)
                {
                    var id = link.Attribute("id")?.Value;
                    if (id != null)
                    {
                        chapter.Id = id;
                    }

                    var url = link.Attribute("href")?.Value;
                    if (url != null)
                    {
                        var href = new Href(url);
                        chapter.RelativePath = href.Path;
                        chapter.HashLocation = href.HashLocation;
                        chapter.AbsolutePath = chapter.RelativePath.ToAbsolutePath(navAbsolutePath);
                    }

                    var titleTextElement = li.Descendants().FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Value));
                    if (titleTextElement != null)
                    {
                        chapter.Title = titleTextElement.Value;
                    }

                    if (li.Element(ns + NavElements.Ol) != null)
                    {
                        chapter.SubChapters = LoadChaptersFromNav(navAbsolutePath, li, chapter);
                    }
                    result.Add(chapter);

                    previous = chapter.SubChapters.Any() ? chapter.SubChapters.Last() : chapter;
                }
            }

            return result;
        }

        private static List<EpubChapter> LoadChaptersFromNcx(string ncxAbsolutePath, IEnumerable<NcxNavPoint> navigationPoints, EpubChapter parentChapter = null)
        {
            var result = new List<EpubChapter>();
            var previous = parentChapter;

            foreach (var navigationPoint in navigationPoints)
            {
                var chapter = new EpubChapter
                {
                    Title = navigationPoint.NavLabelText,
                    Parent = parentChapter,
                    Previous = previous
                };

                if (previous != null)
                    previous.Next = chapter;

                var href = new Href(navigationPoint.ContentSrc);
                chapter.RelativePath = href.Path;
                chapter.AbsolutePath = href.Path.ToAbsolutePath(ncxAbsolutePath);
                chapter.HashLocation = href.HashLocation;
                chapter.SubChapters = LoadChaptersFromNcx(ncxAbsolutePath, navigationPoint.NavPoints, chapter);
                result.Add(chapter);

                previous = chapter.SubChapters.Any() ? chapter.SubChapters.Last() : chapter;
            }
            return result;
        }

        private static EpubResources LoadResources(ZipArchive epubArchive, EpubBook book)
        {
            var resources = new EpubResources();

            foreach (var item in book.Format.Opf.Manifest.Items)
            {
                var path = item.Href.ToAbsolutePath(book.Format.Paths.OpfAbsolutePath);
                ZipArchiveEntry entry = null;

                var href = item.Href;
                var mimeType = item.MediaType;

                EpubContentType contentType;
                contentType = ContentType.MimeTypeToContentType.TryGetValue(mimeType, out contentType)
                    ? contentType
                    : EpubContentType.Other;

                void AddTextFile(byte[] contents)
                {
                    var file = new EpubTextFile
                    {
                        AbsolutePath = path,
                        Href = href,
                        MimeType = mimeType,
                        ContentType = contentType,
                        Content = contents
                    };

                    resources.All.Add(file);

                    switch (contentType)
                    {
                        case EpubContentType.Xhtml11:
                            resources.Html.Add(file);
                            break;
                        case EpubContentType.Css:
                            resources.Css.Add(file);
                            break;
                        default:
                            resources.Other.Add(file);
                            break;
                    }
                }

                try
                {
                    entry = epubArchive.GetEntryImproved(path);
                }
                catch (EpubParseException epex)
                {
                    // Add "placeholders" for missing files. This solves an issue with some (Epub 2?) book files
                    // that references a file that does not exist in the archive (_page_map_.xml).
                    AddTextFile(Encoding.UTF8.GetBytes("Failed to load file: " + epex.Message));

                    book.AddReadError(epex);

                    // Then move on to the next entry.
                    continue;
                }

                if (entry == null)
                {
                    throw new EpubParseException($"file {path} not found in archive.");
                }
                if (entry.Length > int.MaxValue)
                {
                    throw new EpubParseException($"file {path} is bigger than 2 Gb.");
                }

                switch (contentType)
                {
                    case EpubContentType.Xhtml11:
                    case EpubContentType.Css:
                    case EpubContentType.Oeb1Document:
                    case EpubContentType.Oeb1Css:
                    case EpubContentType.Xml:
                    case EpubContentType.Dtbook:
                    case EpubContentType.DtbookNcx:
                        {
                            using (var stream = entry.Open())
                            {
                                AddTextFile(stream.ReadToEnd());
                            }
                            break;
                        }
                    default:
                        {
                            var file = new EpubByteFile
                            {
                                AbsolutePath = path,
                                Href = href,
                                MimeType = mimeType,
                                ContentType = contentType
                            };

                            resources.All.Add(file);

                            using (var stream = entry.Open())
                            {
                                if (stream == null)
                                {
                                    throw new EpubException($"Incorrect EPUB file: content file \"{href}\" specified in manifest is not found");
                                }

                                using (var memoryStream = new MemoryStream((int)entry.Length))
                                {
                                    stream.CopyTo(memoryStream);
                                    file.Content = memoryStream.ToArray();
                                }
                            }

                            switch (contentType)
                            {
                                case EpubContentType.ImageGif:
                                case EpubContentType.ImageJpeg:
                                case EpubContentType.ImagePng:
                                case EpubContentType.ImageSvg:
                                    resources.Images.Add(file);
                                    break;
                                case EpubContentType.FontTruetype:
                                case EpubContentType.FontOpentype:
                                    resources.Fonts.Add(file);
                                    break;
                                default:
                                    resources.Other.Add(file);
                                    break;
                            }
                            break;
                        }
                }
            }

            return resources;
        }

        private static EpubSpecialResources LoadSpecialResources(ZipArchive epubArchive, EpubBook book)
        {
            var result = new EpubSpecialResources
            {
                Ocf = new EpubTextFile
                {
                    AbsolutePath = Constants.OcfPath,
                    Href = Constants.OcfPath,
                    ContentType = EpubContentType.Xml,
                    MimeType = ContentType.ContentTypeToMimeType[EpubContentType.Xml],
                    Content = epubArchive.LoadBytes(Constants.OcfPath)
                },
                Opf = new EpubTextFile
                {
                    AbsolutePath = book.Format.Paths.OpfAbsolutePath,
                    Href = book.Format.Paths.OpfAbsolutePath,
                    ContentType = EpubContentType.Xml,
                    MimeType = ContentType.ContentTypeToMimeType[EpubContentType.Xml],
                    Content = epubArchive.LoadBytes(book.Format.Paths.OpfAbsolutePath)
                },
                HtmlInReadingOrder = new List<EpubTextFile>()
            };

            var htmlFiles = book.Format.Opf.Manifest.Items
                .Where(item => ContentType.MimeTypeToContentType.ContainsKey(item.MediaType) && ContentType.MimeTypeToContentType[item.MediaType] == EpubContentType.Xhtml11)
                .ToDictionary(item => item.Id, item => item.Href);

            foreach (var item in book.Format.Opf.Spine.ItemRefs)
            {
                if (!htmlFiles.TryGetValue(item.IdRef, out string href))
                {
                    continue;
                }

                var html = book.Resources.Html.SingleOrDefault(e => e.Href == href);
                if (html != null)
                {
                    result.HtmlInReadingOrder.Add(html);
                }
            }

            return result;
        }
    }
}
