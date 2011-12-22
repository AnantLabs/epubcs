using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;

namespace libEpub
{
    public class ZipArchive : IDisposable, IEnumerable<CompressedFile>
    {

        private readonly CompressionOption _compression;
        private Package _package;

        public ZipArchive(Stream zipArchive, FileMode mode, CompressionOption compression)
        {
            _compression = compression;
            _package = Package.Open(zipArchive, mode);
        }

        public virtual void AddFile(CompressedFile file)
        {
            var fileUri = PackUriHelper.CreatePartUri(new Uri(file.Path, UriKind.Relative));
            if (_package.PartExists(fileUri))
            {
                throw new IOException(string.Format("file [{0}] already exists in the archive", file.Path));
            }
            var part = _package.CreatePart(fileUri, file.ContentType, _compression);
            using(var s = part.GetStream())
            {
                file.Save(s);
                _package.Flush();
            }
        }

        public void AddFile(string path, Stream contents, string contentType)
        {
            AddFile(new CompressedFile(contents){ContentType = contentType,Path = path});
        }

        public void AddFile(string path)
        {
            var fileInfo = new FileInfo(path);
//            fileInfo.
        }

        public void Dispose()
        {
            var packagePartCollection = _package.GetParts();
            var items = (from i in packagePartCollection
                        select i.Uri.ToString()).ToArray();
            _package.Close();
        }

        public IEnumerator<CompressedFile> GetEnumerator()
        {
            throw new NotImplementedException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
