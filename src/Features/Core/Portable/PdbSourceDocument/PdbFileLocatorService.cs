// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.VisualStudio.Debugger.Contracts;

namespace Microsoft.CodeAnalysis.PdbSourceDocument
{
    [Export(typeof(IPdbFileLocatorService)), Shared]
    internal sealed class PdbFileLocatorService : IPdbFileLocatorService
    {
        private readonly IDebuggerSymbolLocatorService _debuggerSymbolLocatorService;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public PdbFileLocatorService(IDebuggerSymbolLocatorService debuggerSymbolLocatorService)
        {
            _debuggerSymbolLocatorService = debuggerSymbolLocatorService;
        }

        public async Task<DocumentDebugInfoReader?> GetDocumentDebugInfoReaderAsync(string dllPath, CancellationToken cancellationToken)
        {
            var dllStream = IOUtilities.PerformIO(() => File.OpenRead(dllPath));
            if (dllStream is null)
                return null;

            Stream? pdbStream = null;
            DocumentDebugInfoReader? result = null;
            var peReader = new PEReader(dllStream);
            try
            {

                // The simplest possible thing is that the PDB happens to be right next to the DLL. You never know, we might get lucky.
                var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
                if (File.Exists(pdbPath))
                {
                    pdbStream = IOUtilities.PerformIO(() => File.OpenRead(pdbPath));

                    if (pdbStream is not null &&
                        IsPortable(pdbStream))
                    {
                        var pdbReaderProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);

                        result = new DocumentDebugInfoReader(peReader, pdbReaderProvider);
                    }
                }

                // Otherwise lets see if its an embedded PDB
                if (result is null)
                {
                    var entry = peReader.ReadDebugDirectory().FirstOrDefault(x => x.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
                    if (entry.Type != DebugDirectoryEntryType.Unknown)
                    {
                        var pdbReaderProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);

                        result = new DocumentDebugInfoReader(peReader, pdbReaderProvider);
                    }
                }

                // TODO: Otherwise call the debugger to find the PDB from a symbol server etc.
                if (result is null)
                {
                    // Debugger needs:
                    // - PDB MVID
                    // - PDB Age
                    // - PDB TimeStamp
                    // - PDB Path
                    // - DLL Path
                    // 
                    // Most of this info comes from the CodeView Debug Directory from the dll

                    var entry = peReader.ReadDebugDirectory().FirstOrDefault(x => x.Type == DebugDirectoryEntryType.CodeView);
                    if (entry.Type != DebugDirectoryEntryType.Unknown)
                    {
                        var codeViewEntry = peReader.ReadCodeViewDebugDirectoryData(entry);

                        // check for presence of pdb checksums
                        var checksums = peReader.ReadDebugDirectory().Where(x => x.Type == DebugDirectoryEntryType.PdbChecksum)
                            .Select(x => peReader.ReadPdbChecksumDebugDirectoryData(x))
                            .Select(x =>
                            {
                                var checksumString = x.Checksum.Aggregate(new StringBuilder(x.Checksum.Length * 2), (sb, b) => sb.AppendFormat(CultureInfo.InvariantCulture, "{0:x2}", b), sb => sb.ToString());
                                return FormattableString.Invariant($"{x.AlgorithmName}:{checksumString}");
                            })
                            .ToImmutableArray();

                        var pdbInfo = new SymbolLocatorPdbInfo(
                            Path.GetFileName(codeViewEntry.Path),
                            codeViewEntry.Guid,
                            (uint)codeViewEntry.Age,
                            dllPath,
                            entry.Stamp,
                            null,
                            codeViewEntry.Path,
                            checksums
                            );

                        var symbolResult = await _debuggerSymbolLocatorService.GetSymbolFileAsync(
                            pdbInfo,
                            null,
                            CancellationToken.None).ConfigureAwait(false);

                        if (symbolResult.Success)
                        {
                            var pdbReaderProvider = MetadataReaderProvider.FromPortablePdbStream(symbolResult.SymbolStream!);
                            result = new DocumentDebugInfoReader(peReader, pdbReaderProvider);
                        }
                    }
                }
            }
            catch (BadImageFormatException)
            {
                // If the PDB is corrupt in some way we can just ignore it, and let the system fall through to another provider
                // TODO: Log this to the output window: https://github.com/dotnet/roslyn/issues/57352
                result = null;
            }
            finally
            {
                // If we're returning a result then it will own the disposal of the reader, but if not
                // then we need to do it ourselves.
                if (result is null)
                {
                    pdbStream?.Dispose();
                    peReader.Dispose();
                }
            }

            return result;
        }

        private static bool IsPortable(Stream pdbStream)
        {
            var isPortable = pdbStream.ReadByte() == 'B' && pdbStream.ReadByte() == 'S' && pdbStream.ReadByte() == 'J' && pdbStream.ReadByte() == 'B';
            pdbStream.Position = 0;

            return isPortable;
        }
    }
}
