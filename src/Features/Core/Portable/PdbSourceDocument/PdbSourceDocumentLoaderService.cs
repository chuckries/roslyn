// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.VisualStudio.Debugger.Contracts;

namespace Microsoft.CodeAnalysis.PdbSourceDocument
{
    [Export(typeof(IPdbSourceDocumentLoaderService)), Shared]
    internal sealed class PdbSourceDocumentLoaderService : IPdbSourceDocumentLoaderService
    {
        private readonly IDebuggerSourceLinkService _sourceLinkService;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public PdbSourceDocumentLoaderService(IDebuggerSourceLinkService sourceLinkService)
        {
            _sourceLinkService = sourceLinkService;
        }

        public async Task<TextLoader?> LoadSourceDocumentAsync(SourceDocument sourceDocument, CancellationToken cancellationToken)
        {
            // If we already have the embedded text then use that directly
            if (sourceDocument.EmbeddedText is not null)
            {
                var textAndVersion = TextAndVersion.Create(sourceDocument.EmbeddedText, VersionStamp.Default, sourceDocument.FilePath);
                return TextLoader.From(textAndVersion);
            }

            // Otherwise, check the easiest (but most unlikely) case which is the document exists on the disk
            if (File.Exists(sourceDocument.FilePath))
            {
                // TODO: Make sure the hash of the file is correct: https://github.com/dotnet/roslyn/issues/57351
                return new FileTextLoader(sourceDocument.FilePath, Encoding.UTF8);
            }

            // TODO: Call the debugger to download the file
            // Maybe they'll download to a temp file, in which case this method could return a string
            // or maybe they'll return a stream, in which case we could create a new StreamTextLoader

            if (!string.IsNullOrEmpty(sourceDocument.SourceLinkUri))
            {
                var sourceLinkStream = await _sourceLinkService.GetSourceLinkAsync(sourceDocument.SourceLinkUri, CancellationToken.None).ConfigureAwait(false);
                if (sourceLinkStream is not null)
                {
                    using (sourceLinkStream)
                    {
                        // hack, copy to tmp location
                        var tmpPath = Path.GetTempFileName();
                        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite))
                        {
                            await sourceLinkStream.CopyToAsync(fs, 81920, CancellationToken.None).ConfigureAwait(false);
                        }

                        return new FileTextLoader(tmpPath, Encoding.UTF8);
                    }
                }
            }

            return null;
        }
    }
}
