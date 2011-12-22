﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using System.Xml.XPath;
using epub;

namespace libEpub
{
    public class Book : IDisposable
    {
        private readonly ZipArchive _archive;
        private string _title;
        public string Title
        {
            get { return _title; }
            set
            {
                _title = value;
                _tableOfContents.docTitle.text = value;
            }
        }

        public List<string> Authors { get; set; }
        public string Publisher { get; set; }

        public Book(Stream bookStream)
        {
            _archive = new ZipArchive(bookStream,FileMode.CreateNew,CompressionOption.Normal);

            _title = "Untitled";

            AddMimeType();
            AddMetaInfo();
            AddNotFoundImage();

            InitializeToc();
            InitializeContentOpf();
        }

        private void InitializeContentOpf()
        {
            _contentOpf = new package()
              {
                  metadata = new packageMetadata(),
                  spine = new packageSpine()
                      {
                          toc = "ncx",
                      },
                  guide = new packageGuide(){ reference = new packageGuideReference()
                      {
                          href = "cover.xhtml",
                          type = "cover",
                          title = "conver"
                      }},
                  uniqueidentifier = "uidParam",
                  version = "2.0",
              };
        }

        private void AddNotFoundImage()
        {
            _archive.AddFile("OEBPS/images/not-found.jpg", Assembly.GetExecutingAssembly().GetManifestResourceStream("libEpub.not-found.jpg"), "image/jpeg");
        }

        private List<ncxNavPoint>  _navigationPoints = new List<ncxNavPoint>();
        private void InitializeToc()
        {
            _tableOfContents = new epub.ncx()
            {
                docTitle = new ncxDocTitle(),
                version = "2005-1",
                head = new[]
                              { 
                                  new ncxMeta() { content = Guid.NewGuid().ToString(), name = "dtb:uid" },
                                  new ncxMeta() { content = "1", name = "dtb:depth" },
                                  new ncxMeta() { content = "0", name = "dtb:totalPageCount" },
                                  new ncxMeta() { content = "0", name = "dtb:maxPageNumber" },
                              },
            };
        }

        private void AddMimeType()
        {
            using(var mimeType = new MemoryStream())
            {
                var text = Encoding.UTF8.GetBytes("application/epub+zip");
                mimeType.Write(text,0,text.Length);
                mimeType.Position = 0;
                _archive.AddFile("mimetype",mimeType,"text/plain");
            }
        }

        private void AddMetaInfo()
        {
            var containerInfo = new XmlSerializer(typeof(epub.container));
            using(var containerData = new MemoryStream())
            {
                var container = new epub.container()
                                    { version = "1.0", rootfiles = new containerRootfiles()
                                       {
                                           rootfile = new containerRootfilesRootfile()
                                              {
                                                  fullpath = "OEBPS/Content.opf",
                                                  mediatype = "application/oebps-package+xml"
                                              }
                                       }
                                    };
                containerInfo.Serialize(containerData,container);
                containerData.Position = 0;
                _archive.AddFile("META-INF/container.xml",containerData,"text/xml");
            }
        }

        private int _chapterCounter = 1;
        private ncx _tableOfContents;
        private package _contentOpf;
        private List<packageItem> _packageItems = new List<packageItem>();
        private List<packageSpineItemref> _itemReferences = new List<packageSpineItemref>();
        
        private bool _isClosed = false;

        /// <summary>
        /// Adds another chapter to the book. Chapter will be stored as a separate file
        /// </summary>
        /// <param name="content">content for the chapter. Expected to be html</param>
        /// <param name="suggestedFilename">a filename you want to use for the chapter in the book</param>
        /// <param name="onImageLoading">method called for every image to be loaded.</param>
        public void AddChapter(TextReader content, string suggestedFilename, Func<string, Stream> onImageLoading)
        {
            var fileName = Path.GetFileNameWithoutExtension(suggestedFilename) + ".xhtml";

            var sourceReader = new Sgml.SgmlReader
            {
                DocType = "HTML",
                WhitespaceHandling = WhitespaceHandling.All,
                CaseFolding = Sgml.CaseFolding.ToLower,
                InputStream = content,
            };

            var document = new XmlDocument()
                               {
                                   PreserveWhitespace = true, 
                                   XmlResolver = null,
                               };
            document.Load(sourceReader);

            var navigator = document.CreateNavigator();

            var chapterTitle = GetChapterTitle(navigator);

            AddNavigationPointToTableOfContents(fileName, chapterTitle);

            CleanUpTags(document);
            AddCssReference(document, navigator);
            ResolveAndDownloadImages(document, onImageLoading);
            SaveDocument(document, fileName);
            AddContentOpfRecord(fileName);
            _chapterCounter++;
        }

        private void CleanUpTags(XmlDocument document)
        {
            var bodyTag = document.GetElementsByTagName("body").Cast<XmlNode>().DefaultIfEmpty(null).First();
            if (bodyTag == null) return;
            if(bodyTag.Attributes != null)
            {
                bodyTag.Attributes.RemoveAll();
            }
        }

        private void AddContentOpfRecord(string fileName)
        {
            var itemId = string.Format("id{0}", _chapterCounter);
            _packageItems.Add(new packageItem()
                                  {
                                      href = fileName,
                                      id = itemId,
                                      mediatype = "application/xhtml+xml",
                                  });
            _itemReferences.Add(new packageSpineItemref()
                                    {
                                        idref = itemId
                                    });
        }

        private void ResolveAndDownloadImages(XmlDocument document, Func<string, Stream> onImageLoading)
        {
            var imageCounter = 1;
            foreach(var i in document.GetElementsByTagName("img").Cast<XmlNode>())
            {
                if (i.Attributes == null || i.Attributes["src"] == null)
                {
                    var srcAttribute = document.CreateAttribute("src");
                    srcAttribute.Value = "images/not-found.jpg";
                    i.AppendChild(srcAttribute);
                }
                else
                {
                    var src = i.Attributes["src"].Value;

                    var imageName = string.Format("OEBPS/images/{0}{1}", Guid.NewGuid(), Path.GetExtension(src));
                    try
                    {
                        using (var imageStream = new BufferedStream(onImageLoading(src)))
                        {
                            _archive.AddFile(imageName, imageStream, "");
                        }
                        i.Attributes["src"].Value = imageName;
                    }
                    catch(Exception)
                    {
                        i.Attributes["src"].Value = "images/not-found.jpg";
                    }


                    _packageItems.Add(new packageItem()
                                          {
                                              href = i.Attributes["src"].Value,
                                              id = string.Format("imageId{0}_{1}", _chapterCounter, imageCounter),
                                              mediatype = GetMimeType(i.Attributes["src"].Value)
                                          });
                }
                imageCounter++;
            }
        }

        private string GetMimeType(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(extension)) return "image/jpeg";
            extension = extension.ToLower();
            switch (extension)
            {
                case "gif":
                    return "image/gif";
                case "jpg":
                case "jpeg":
                    return "image/jpeg";
                case "bmp":
                    return "image/bmp";
                case "png":
                    return "image/png";
            }
            return "image/jpeg";
        }

        private void AddCssReference(XmlDocument document, XPathNavigator navigator)
        {
            var headNode = navigator.SelectSingleNode("//head");

            XmlNodeList linkElements = document.GetElementsByTagName("link");
            if(linkElements != null && linkElements.Count > 0)
            {
                var linkElementsToRemove = (from XmlNode i in linkElements
                               select i).ToList();
                foreach (XmlNode element in linkElementsToRemove)
                {
                    element.ParentNode.RemoveChild(element);
                }
            }

            var linkElement = CreateLinkToStylesheet(document);

            if(headNode == null)
            {
                var headElement = document.CreateElement("head", "");
                headElement.AppendChild(linkElement);
                var htmlTag = document.GetElementsByTagName("html")[0];
                htmlTag.InnerXml = headElement.OuterXml + htmlTag.InnerXml;
            }
            else
            {
                headNode.AppendChild(linkElement.OuterXml.Replace(@"xmlns=""http://www.w3.org/1999/xhtml""",""));
            }
        }

        private void AddNavigationPointToTableOfContents(string fileName, string chapterTitle)
        {
            var navPoint = new ncxNavPoint()
                               {
                                   content = new ncxNavPointContent() { src = "OEBPS/" + fileName },
                                   playOrder = _chapterCounter.ToString(),
                                   id = string.Format("NavPoint-{0}", _chapterCounter.ToString()),
                                   navLabel = new ncxNavPointNavLabel() { text = chapterTitle },
                               };
            _navigationPoints.Add(navPoint);
        }

        private void SaveDocument(XmlDocument document, string fileName)
        {

            var settings = new XmlWriterSettings()
                               {
                                   CloseOutput = false,
                                   Encoding = Encoding.UTF8,
                                   ConformanceLevel = ConformanceLevel.Document,
                                   Indent = true,
                                   OmitXmlDeclaration = false,
                               };

            using(var s = new MemoryStream())
            {
                var xmlTextWriter = XmlWriter.Create(s,settings);
                document.Save(xmlTextWriter);
                s.Position = 0;
                ReformatAndCleanXhtml(s);
                _archive.AddFile(string.Format("OEBPS/{0}", fileName), s, "application/xhtml+xml");

            }
        }

        private static Regex _noscript = new Regex("<\\/?script[^>]*>");
        private static Regex _nometa = new Regex("<\\/?meta[^>]*>");
        private static Regex _nocomment = new Regex("\\<![ \\r\\n\\t]*(--([^\\-]|[\\r\\n]|-[^\\-])*--[ \\r\\n\\t]*)\\>");
        private static Regex _nocdata = new Regex(@"\<\!\[CDATA\[(?<text>[^\]]*)\]\]\>");

        private void ReformatAndCleanXhtml(MemoryStream memoryStream)
        {
            memoryStream.Position = 0;
            var xhtmlReader = new StreamReader(memoryStream);
            var xhtmlData = xhtmlReader.ReadToEnd();
            xhtmlData = xhtmlData.Replace("<html>", "<html xmlns=\"http://www.w3.org/1999/xhtml\">");
            xhtmlData = _noscript.Replace(xhtmlData, "");
            xhtmlData = _nometa.Replace(xhtmlData, "");
            xhtmlData = _nocomment.Replace(xhtmlData, "");
            xhtmlData = _nocdata.Replace(xhtmlData, "");
            memoryStream.Position = 0;
            memoryStream.SetLength(0);
            var streamWriter = new StreamWriter(memoryStream);
            streamWriter.Write(xhtmlData);
            memoryStream.Flush();
            streamWriter.Flush();
            memoryStream.Position = 0;
        }

        private string GetChapterTitle(XPathNavigator navigator)
        {
            var titleNode = navigator.SelectSingleNode("//title");
            return titleNode == null ? _chapterCounter.ToString() : titleNode.Value;
        }

        private XmlElement CreateLinkToStylesheet(XmlDocument document)
        {
            var linkElement = document.CreateElement("", "link", "");

            linkElement.SetAttribute("href", null, "stylesheet.css");
            linkElement.SetAttribute("rel", null, "stylesheet");
            linkElement.SetAttribute("type", null, "text/css");
            return linkElement;
        }

        public void AddChapter(TextReader content, Func<string, Stream> onImageLoading)
        {
            AddChapter(content, Guid.NewGuid().ToString(), onImageLoading);
        }


        private void SerializeToArchive<T>(string fileName, string contentType, T data)
        {
            var xmlSerializer = new XmlSerializer(typeof(T));
            using(var memoryStream = new MemoryStream())
            {
                xmlSerializer.Serialize(memoryStream,data);
                memoryStream.Position = 0;
                _archive.AddFile(fileName, memoryStream, contentType);
            }
        }

        public void Close()
        {
            if (_isClosed) return;

            _tableOfContents.navMap = _navigationPoints.ToArray();
            SerializeToArchive("toc.ncx", "text/xml", _tableOfContents);

            _packageItems.Add(new packageItem()
            {
                href = "../toc.ncx",
                id = "ncx",
                mediatype = "application/x-dtbncx+xml"
            });

            _contentOpf.manifest = _packageItems.ToArray();
            _contentOpf.spine.itemref = _itemReferences.ToArray();

            using(var ms = new  MemoryStream())
            {
                var serializer = new XmlSerializer(typeof (package));
                serializer.Serialize(ms,_contentOpf);
                ms.Position = 0;
                var document = new XmlDocument();
                document.Load(ms);
                var metadata = document.GetElementsByTagName("metadata")[0];

                var description = document.CreateElement("dc","description","http://purl.org/dc/elements/1.1/");
                description.InnerText = Title;
                var title = document.CreateElement("dc", "title", "http://purl.org/dc/elements/1.1/");
                title.InnerText = Title;
                var meta = document.CreateElement("", "meta", "http://www.idpf.org/2007/opf");
                meta.SetAttribute("name", "cover");
                meta.SetAttribute("content", "cover");
                var language = document.CreateElement("dc", "language", "http://purl.org/dc/elements/1.1/");
                language.InnerText = "ru";

                var id = document.CreateElement("dc","identifier","http://purl.org/dc/elements/1.1/");
                id.SetAttribute("id", "uidParam");
                id.InnerText = Guid.NewGuid().ToString();


                var authors = Authors.Select(i =>
                                        {
                                            var author = document.CreateElement("dc", "creator","http://purl.org/dc/elements/1.1/");
                                            author.SetAttribute("file-as", "http://www.idpf.org/2007/opf", i);
                                            author.SetAttribute("role", "http://www.idpf.org/2007/opf", "aut");
                                            return author;
                                        }).ToList();
                
                var dateTime = document.CreateElement("dc", "date", "http://purl.org/dc/elements/1.1/");
                dateTime.InnerText = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                metadata.AppendChild(description);
                metadata.AppendChild(title);
                metadata.AppendChild(meta);
                metadata.AppendChild(language);
                metadata.AppendChild(id);
                
                authors.ForEach(i => metadata.AppendChild(i));
                metadata.AppendChild(dateTime);

                ms.Position = 0;
                ms.SetLength(0);

                document.Save(ms);
                ms.Position = 0;
                _archive.AddFile("OEBPS/Content.opf", ms, "application/oebps-package+xml");
                ms.Flush();

            }


            _isClosed = true;
        }

        public void Dispose()
        {
            Close();
            _archive.Dispose();
        }
    }
}
