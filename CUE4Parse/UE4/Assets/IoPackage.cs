using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Assets.Utils;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.Utils;
using Serilog;

namespace CUE4Parse.UE4.Assets
{
    public sealed class IoPackage : AbstractUePackage
    {
        private readonly Lazy<FObjectImport[]> _importMapLazy;
        public readonly FPackageSummary IoSummary;
        public readonly IoGlobalData GlobalData;
        
        public override FPackageFileSummary Summary { get; }
        public override FNameEntrySerialized[] NameMap { get; }
        public override FObjectImport[] ImportMap => _importMapLazy.Value;
        public override FObjectExport[] ExportMap { get; }

        public FPackageObjectIndex[] ExportIndices { get; private set; }
        public IReadOnlyList<GameFile> GraphData { get; private set; }

        public IoPackage(
            FArchive uasset, IoGlobalData globalData,
            Lazy<FArchive?>? ubulk = null, Lazy<FArchive?>? uptnl = null,
            IFileProvider? provider = null, TypeMappings? mappings = null) : base(uasset.Name.SubstringBeforeLast(".uasset"), provider, mappings)
        {
            GlobalData = globalData;
            var uassetAr = new FAssetArchive(uasset, this);
            IoSummary = uassetAr.Read<FPackageSummary>();
            Summary = new FPackageFileSummary
            {
                PackageFlags = (PackageFlags) IoSummary.PackageFlags,
                TotalHeaderSize = IoSummary.GraphDataOffset + IoSummary.GraphDataSize,
                NameCount = IoSummary.NameMapHashesSize / sizeof(ulong) - 1,
                ExportCount = (IoSummary.ExportBundlesOffset - IoSummary.ExportMapOffset) / Unsafe.SizeOf<FExportMapEntry>(),
                ImportCount = (IoSummary.ExportMapOffset - IoSummary.ImportMapOffset) / FPackageObjectIndex.Size
            };
            
            uassetAr.Position = IoSummary.NameMapNamesOffset;
            NameMap = FNameEntrySerialized.LoadNameBatch(uassetAr, Summary.NameCount);

            uassetAr.Position = IoSummary.ExportBundlesOffset;
            LoadExportBundles(uassetAr, out var bundleHeadersArray, out var bundleEntriesArray);

            ExportMap = LoadExportTable(uassetAr, Summary.ExportCount, bundleHeadersArray, bundleEntriesArray);

            uassetAr.Position = IoSummary.GraphDataOffset;
            GraphData = LoadGraphData(uassetAr);
            
            _importMapLazy = LoadImportTable(uassetAr, Summary.ImportCount);

            if (ubulk != null) uassetAr.AddPayload(PayloadType.UBULK, Summary.BulkDataStartOffset, ubulk);
            if (uptnl != null) uassetAr.AddPayload(PayloadType.UPTNL, Summary.BulkDataStartOffset, uptnl);
            
            ProcessExportMap(uassetAr);
        }
        
        
        public IoPackage(FArchive uasset, IoGlobalData globalData, FArchive? ubulk = null, FArchive? uptnl = null,
            IFileProvider? provider = null, TypeMappings? mappings = null)
            : this(uasset, globalData, ubulk != null ? new Lazy<FArchive?>(() => ubulk) : null,
                uptnl != null ? new Lazy<FArchive?>(() => uptnl) : null, provider, mappings)
        { }

        private FObjectExport[] LoadExportTable(FAssetArchive reader, int exportCount, FExportBundleHeader[] bundleHeaders, FExportBundleEntry[] bundleEntries)
        {
            ExportIndices = new FPackageObjectIndex[exportCount];
            var exportMap = new FObjectExport[exportCount];
            for (var i = 0; i < exportMap.Length; i++)
            {
                exportMap[i] = new FObjectExport();
            }

            reader.Position = IoSummary.ExportMapOffset;
            var exportEntries = reader.ReadArray<FExportMapEntry>(exportCount);
            var exportOrder = new List<uint>(exportCount);
            
            // Export data is ordered according to export bundles, so we should do the processing in bundle order
            foreach (var bundleHeader in bundleHeaders)
            {
                for (var entryIndex = 0; entryIndex < bundleHeader.EntryCount; entryIndex++)
                {
                    var entry = bundleEntries[bundleHeader.FirstEntryIndex + entryIndex];
                    if (entry.CommandType == EExportCommandType.ExportCommandType_Serialize)
                    {
                        var objectIndex = entry.LocalExportIndex;
                        exportOrder.Add(objectIndex);

                        ref var e = ref exportEntries[objectIndex];
                        ref var exp = ref exportMap[objectIndex];
                        
                        // TODO: FExportMapEntry has FilterFlags which could affect inclusion of exports
                        if (e.CookedSerialOffset >= 0x7FFFFFFF || e.CookedSerialSize >= 0x7FFFFFFF)
                            throw new ParserException("TODO: FExportMapEntry has FilterFlags");

                        //This export offset is not the "real" offset
                        exp.SerialOffset = (long) e.CookedSerialOffset;
                        exp.SerialSize = (long) e.CookedSerialSize;
                        exp.ObjectName = CreateFNameFromMappedName(e.ObjectName);
                        
                        if (e.ClassIndex.IsExport)
                            exp.ClassName = exportMap[e.ClassIndex.TypeAndId].ObjectName.Text;
                        else if (e.ClassIndex.IsScriptImport)
                            exp.ClassName = GlobalData.FindScriptEntryName(e.ClassIndex);
                        else if (e.ClassIndex.IsPackageImport)
                            exp.ClassName = "None"; // ImportHelper.FindObjectInPackages must be used here but is not yet initialized
                        else
                            exp.ClassName = "None";
                        
                        var outerIndex = (long) (e.OuterIndex.TypeAndId + 1);
                        if (outerIndex < 0 || outerIndex > exportCount) throw new ParserException($"Invalid outer index {outerIndex}, must be in range [0; {exportCount}]");
                        exp.OuterIndex = new FPackageIndex(reader, (int) outerIndex);
                        
                        ExportIndices[objectIndex] = e.GlobalImportIndex;
                    }
                }
            }
            
            var currentExportOffset = Summary.TotalHeaderSize;
            foreach (var objectIndex in exportOrder)
            {
                ref var exp = ref exportMap[objectIndex];
                exp.RealSerialOffset = currentExportOffset;
                currentExportOffset += (int) exp.SerialSize;    
            }

            Summary.BulkDataStartOffset = currentExportOffset;
            return exportMap;
        }

        private class ImportHelper
        {
            private readonly IReadOnlyList<GameFile> _packageFiles;
            private readonly IoPackage?[] _packages;
            private readonly int[] _allocatedPackageImports;
            private readonly FObjectImport[] _importTable;
            private readonly FPackageObjectIndex[] _importMap;
            private readonly IFileProvider? _provider;
            private int _nextImportToCheck;

            public ImportHelper(IReadOnlyList<GameFile> packageFiles, FObjectImport[] importTable, FPackageObjectIndex[] importMap, IFileProvider? provider)
            {
                _provider = provider;
                _packageFiles = packageFiles;
                _importTable = importTable;
                _importMap = importMap;
                
                _allocatedPackageImports = new int[packageFiles.Count];
                for (var i = 0; i < _allocatedPackageImports.Length; i++)
                {
                    _allocatedPackageImports[i] = -1;
                }
                
                // Preload dependencies
                var packagesTasks = new Task<IPackage?>[packageFiles.Count];
                for (var i = 0; i < packagesTasks.Length; i++)
                {
                    packagesTasks[i] = _provider?.TryLoadPackageAsync(packageFiles[i]) ?? Task.FromResult<IPackage?>(null);
                }
                
                _packages = new IoPackage?[packageFiles.Count];
                for (var i = 0; i < _packages.Length; i++)
                {
                    _packages[i] = packagesTasks[i].GetAwaiter().GetResult() as IoPackage;
                }
            }

            public bool FindObjectInPackages(FPackageObjectIndex objectIndex, out int outPackageIndex, out FObjectExport outExportEntry)
            {
                for (var packageIndex = 0; packageIndex < _packages.Length; packageIndex++)
                {
                    var importPackage = _packages[packageIndex];
                    if (importPackage == null) break;
                    
                    for (var i = 0; i < importPackage.ExportIndices.Length; i++)
                    {
                        if (importPackage.ExportIndices[i].Value != objectIndex.Value) continue;
                        
                        outPackageIndex = packageIndex;
                        outExportEntry = importPackage.ExportMap[i];
                        return true;
                    }
                }
                
                outPackageIndex = 0;
                outExportEntry = default;
                return false;
            }

            public int GetPackageImportIndex(int packageIndex)
            {
                var index =_allocatedPackageImports[packageIndex];
                if (index >= 0) return index; // Already allocated
                
                index = FindNullImportEntry();
                _allocatedPackageImports[packageIndex] = index;

                var imp = _importTable[index] = new FObjectImport();
                imp.ObjectName = new FName(_packageFiles[packageIndex].PathWithoutExtension); // TODO This still has mount point replaced
                imp.ClassName = new FName("Package");
                return index;
            }

            private int FindNullImportEntry()
            {
                for (var index = _nextImportToCheck; index < _importTable.Length; index++)
                {
                    if (!_importMap[index].IsNull) continue;
                    
                    _nextImportToCheck = index + 1;
                    return index;
                }
                throw new ParserException("Unable to find Null import entry");
            }
        }

        private Lazy<FObjectImport[]> LoadImportTable(FAssetArchive reader, int importCount)
        {
            return new (() =>
            {
                var savePos = reader.Position;
                reader.Position = IoSummary.ImportMapOffset;
                var importTable = new FObjectImport[importCount];
                var importMap = reader.ReadArray<FPackageObjectIndex>(importCount);
                
                var helper = new ImportHelper(GraphData, importTable, importMap, Provider);
                for (var importIndex = 0; importIndex < importCount; importIndex++)
                {
                    var objectIndex = importMap[importIndex];

                    ref var imp = ref importTable[importIndex];
                    if (objectIndex.IsNull)
                        continue;
                    if (objectIndex.IsScriptImport)
                    {
                        imp = new FObjectImport();
                        var name = GlobalData.FindScriptEntryName(objectIndex);
                        imp.ObjectName = new FName(name);
                        imp.ClassName = new FName(name[0] == '/' ? "Package" : "Class");
                        imp.OuterIndex = null;
                    }
                    else if (objectIndex.IsPackageImport)
                    {
                        if (helper.FindObjectInPackages(objectIndex, out var packageIndex, out var exp))
                        {
                            imp = new FObjectImport();
                            imp.ObjectName = exp.ObjectName;
                            imp.ClassName = new FName(exp.ClassName);
                            imp.OuterIndex = new FPackageIndex(reader, -helper.GetPackageImportIndex(packageIndex) - 1);
                        }
                        else
                        {
                            Log.Warning("Failed to resolve import {0:X}", objectIndex.Value);
                        }
                    }
                }

                reader.Position = savePos;
                return importTable;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private FName CreateFNameFromMappedName(FMappedName mappedName) =>
            new (mappedName, mappedName.IsGlobal ? GlobalData.GlobalNameMap : NameMap);

        private void LoadExportBundles(FArchive reader, out FExportBundleHeader[] bundleHeadersArray, out FExportBundleEntry[] bundleEntriesArray)
        {
            var bundleHeadersBytes = reader.ReadBytes(IoSummary.GraphDataOffset - IoSummary.ExportBundlesOffset);
            
            unsafe
            {
                fixed (byte* bundleHeadersRaw = bundleHeadersBytes)
                {
                    var bundleHeaders = (FExportBundleHeader*) bundleHeadersRaw;
                    var remainingBundleEntryCount = (IoSummary.GraphDataOffset - IoSummary.ExportBundlesOffset) / sizeof(FExportBundleEntry);
                    var foundBundlesCount = 0;
                    var currentBundleHeader = bundleHeaders;
                    while (foundBundlesCount < remainingBundleEntryCount)
                    {
                        // This location is occupied by header, so it is not a bundle entry
                        remainingBundleEntryCount--;
                        foundBundlesCount += (int) currentBundleHeader->EntryCount;
                        currentBundleHeader++;
                    }

                    if (foundBundlesCount != remainingBundleEntryCount)
                        throw new ParserException(reader, $"FoundBundlesCount {foundBundlesCount} != RemainingBundleEntryCount {remainingBundleEntryCount}");
                    
                    // Load export bundles into arrays
                    bundleHeadersArray = new FExportBundleHeader[currentBundleHeader - bundleHeaders];
                    fixed (FExportBundleHeader* bundleHeadersPtr = bundleHeadersArray)
                    {
                        Unsafe.CopyBlockUnaligned(bundleHeadersPtr, bundleHeaders, (uint) (bundleHeadersArray.Length * sizeof(FExportBundleHeader)));
                    }
                    
                    bundleEntriesArray = new FExportBundleEntry[foundBundlesCount];
                    fixed (FExportBundleEntry* bundleEntriesPtr = bundleEntriesArray)
                    {
                        Unsafe.CopyBlockUnaligned(bundleEntriesPtr, currentBundleHeader, (uint) (foundBundlesCount * sizeof(FExportBundleEntry)));
                    }
                }
            }
        }

        private IReadOnlyList<GameFile> LoadGraphData(FAssetArchive Ar)
        {
            var packageCount = Ar.Read<int>();
            if (packageCount == 0) return Array.Empty<GameFile>();
            
            if (Provider == null)
                throw new ParserException(Ar, "Cannot process graph data without a file provider");
            
            var packages = new List<GameFile>(packageCount);
            for (var packageIndex = 0; packageIndex < packageCount; packageIndex++)
            {
                var packageId = Ar.Read<FPackageId>();
                var bundleCount = Ar.Read<int>();
                Ar.Position += bundleCount * (sizeof(int) + sizeof(int)); // Skip FArcs

                if (Provider.FilesById.TryGetValue(packageId, out var file)) packages.Add(file);
                else Log.Warning("Can't locate package with id {0:X}", packageId.id);
            }
            return packages;
        }
    }
}