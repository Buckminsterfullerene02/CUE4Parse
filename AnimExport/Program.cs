using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse_Conversion;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using Newtonsoft.Json;

public class Program
{
    // SET THESE FOR YOUR GAME
    private const string _pakDir = @"C:\Program Files (x86)\Steam\steamapps\common\Whiskerwood\Whiskerwood\Content\Paks";
    private const string _aesKey = ""; // If your game does not have an AES key, leave this empty
    private const string _mapping = @"F:\Whiskerwood Modding\Whiskerwood.usmap";  // If your game does not need a mappings file, leave this empty
    private const EGame  _version = EGame.GAME_UE5_6; // Check if your game has a custom version, as some do
    private const bool   _exportMaterials = true; // This needs to be false if generating for CAS+UEAT
    private const string _outputDir = @"F:\Whiskerwood Modding\dumps";
    private const bool   _generateJson = true; // Whether to generate JSON files alongside exported assets
    private const bool   _replaceFiles = false;
    private const bool   _printSuccess = true; // Once you've verified this works, set this to false to reduce console spam
    
    /*
     * Multi-threading configuration
     */
    private const bool   _useMultiThreading = true; // Set to false to disable multi-threading for debugging
    private const int    _maxDegreeOfParallelism = -1; // -1 = use all available cores, or set specific number (be careful with this tho)
    
    /*
     * Some games (e.g. Valorant) have a stripped AssetRegistry file so set this to true if yours does
     * You'll know if it has a stripped AR if this being false doesn't export any files
     * Warning: Always replaces existing files regardless of _replaceFiles
     * Warning: Has no support for broken RefPose animation additives
     */
    private const bool   _hasStrippedAssetRegistry = false;

    private const bool   _isPluginPak = false;
    private const string _pluginName = "DLC01";
    
    /*
     * Folder names for each item type where:
     * [0] = AnimSeqs
     * [1] = AnimMonts
     * [2] = AnimComps
     * [3] = SKMs
     * [4] = SMs
     * [5] = SKs
     * You can change this as you see fit (such as to the same names like all anims to "Anims")
     */
    private static readonly List<string> _folderNames = new() { "Anims", "Anims", "Anims", "SKMs", "SMs", "SKs" };
    
    // DO NOT TOUCH ANYTHING BELOW THIS LINE
    private static ConcurrentBag<string> animAdditives = new();
    private static int _processedAssets = 0;

    static async Task Main(string[] args)
    {
        Console.WriteLine($"Starting export process...");
        Console.WriteLine($"Multi-threading: {(_useMultiThreading ? "Enabled" : "Disabled")}");
        if (_useMultiThreading)
        {
            var maxThreads = _maxDegreeOfParallelism == -1 ? Environment.ProcessorCount : _maxDegreeOfParallelism;
            Console.WriteLine($"Max degree of parallelism: {maxThreads}");
            Console.WriteLine($"Could use up to {Environment.ProcessorCount} threads based on CPU cores");
        }
        DefaultFileProvider provider = await ExportAssets(_hasStrippedAssetRegistry);
        CreateBlenderSKs(provider, _folderNames[5]);
        PrintAnimAdditives();
    }

    private static async Task<DefaultFileProvider> ExportAssets(bool hasStrippedAR = false)
    {
        var provider = new DefaultFileProvider(_pakDir, SearchOption.TopDirectoryOnly, 
            new VersionContainer(_version), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_mapping)) provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_mapping);
        provider.Initialize();
        if (!string.IsNullOrEmpty(_aesKey)) await provider.SubmitKeyAsync(new FGuid(), new FAesKey(_aesKey));
        await provider.MountAsync();
        
        if (hasStrippedAR)
        {
            IEnumerable<GameFile> folderAssets;
            if (_isPluginPak)
            {
                folderAssets = provider.Files.Values.Where(file =>
                    file.Path.StartsWith($"{provider.ProjectName}/Plugins/{_pluginName}/Content/", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                folderAssets = provider.Files.Values.Where(file =>
                    file.Path.StartsWith($"{provider.ProjectName}/Content/", StringComparison.OrdinalIgnoreCase));    
            }

            var parallelOptions = new ParallelOptions();
            if (_useMultiThreading && _maxDegreeOfParallelism > 0)
            {
                parallelOptions.MaxDegreeOfParallelism = _maxDegreeOfParallelism;
            }
            else if (!_useMultiThreading)
            {
                parallelOptions.MaxDegreeOfParallelism = 1;
            }

            if (_useMultiThreading)
            {
                Parallel.ForEach(folderAssets, parallelOptions, asset =>
                {
                    ProcessGameFileAsset(provider, asset);
                });
            }
            else
            {
                foreach (var asset in folderAssets)
                {
                    ProcessGameFileAsset(provider, asset);
                }
            }
        }
        else
        {
            List<FAssetData> assets = new();
            var assetRegistryPath = $"{provider.ProjectName}/AssetRegistry.bin";
            FArchive assetArchive = null;
            if (provider.TryGetGameFile(assetRegistryPath, out var assetRegistryFile))
            {
                assetArchive = await assetRegistryFile.SafeCreateReaderAsync();
            }
            if (assetArchive is not null) assets.AddRange(new FAssetRegistryState(assetArchive).PreallocatedAssetDataBuffers);

            // Filter assets that start with "/Game"
            var gameAssets = assets.Where(asset => asset.PackagePath.ToString().StartsWith("/Game")).ToList();
            
            Console.WriteLine($"Found {gameAssets.Count} assets to process...");

            var parallelOptions = new ParallelOptions();
            if (_useMultiThreading && _maxDegreeOfParallelism > 0)
            {
                parallelOptions.MaxDegreeOfParallelism = _maxDegreeOfParallelism;
            }
            else if (!_useMultiThreading)
            {
                parallelOptions.MaxDegreeOfParallelism = 1;
            }

            if (_useMultiThreading)
            {
                Parallel.ForEach(gameAssets, parallelOptions, asset =>
                {
                    ProcessAssetData(provider, asset, assets);
                });
            }
            else
            {
                foreach (var asset in gameAssets)
                {
                    ProcessAssetData(provider, asset, assets);
                }
            }
        }

        return provider;
    }

    private static void ProcessGameFileAsset(DefaultFileProvider provider, GameFile asset)
    {
        try
        {
            if (provider.TryLoadPackageObject(asset.PathWithoutExtension, out UAnimSequence seq)) ExportFromGameFiles(seq, _folderNames[0]);
            if (provider.TryLoadPackageObject(asset.PathWithoutExtension, out UAnimMontage mont)) ExportFromGameFiles(mont, _folderNames[1]);
            if (provider.TryLoadPackageObject(asset.PathWithoutExtension, out UAnimComposite comp)) ExportFromGameFiles(comp, _folderNames[2]);
            if (provider.TryLoadPackageObject(asset.PathWithoutExtension, out USkeletalMesh skm)) ExportFromGameFiles(skm, _folderNames[3]);
            if (provider.TryLoadPackageObject(asset.PathWithoutExtension, out UStaticMesh sm)) ExportFromGameFiles(sm, _folderNames[4]);
            if (provider.TryLoadPackageObject(asset.PathWithoutExtension, out USkeleton sk)) ExportFromGameFiles(sk, _folderNames[5]);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error processing asset {asset.PathWithoutExtension}: {e.Message}");
        }
    }

    private static void ProcessAssetData(DefaultFileProvider provider, FAssetData asset, List<FAssetData> allAssets)
    {
        try
        {
            var processed = Interlocked.Increment(ref _processedAssets);
            if (processed % 100 == 0) 
            {
                Console.WriteLine("Processed " + processed + " assets...");
            }

            //if (!asset.PackageName.ToString().Contains("Zurg")) continue; // For debugging purposes
            
            // SET THIS FOR ANY ADDITIVE ASSETS THAT ARE FAILING TO EXPORT DUE TO INCORRECT REF POSE
            if (asset.PackageName.ToString() == @"/Game/Enemies/HydraWeed/Assets/ANIM_HydraWeed_Heart_Damaged_Additive")
            {
                // Note: This specific case might need special handling in multi-threaded environment
                // For now, we'll process it normally and handle the extra asset lookup differently
                ExportFromAssetData<UAnimSequence>(provider, _folderNames[0], asset);
                return;
            }

            switch (asset.AssetClass.Text)
            {
                case "AnimSequence":
                    ExportFromAssetData<UAnimSequence>(provider, _folderNames[0], asset);
                    break;
                case "AnimMontage":
                    ExportFromAssetData<UAnimMontage>(provider, _folderNames[1], asset);
                    break;
                case "AnimComposite":
                    ExportFromAssetData<UAnimComposite>(provider, _folderNames[2], asset);
                    break;
                case "SkeletalMesh":
                    ExportFromAssetData<USkeletalMesh>(provider, _folderNames[3], asset);
                    break;
                case "StaticMesh":
                    ExportFromAssetData<UStaticMesh>(provider, _folderNames[4], asset);
                    break;
                case "Skeleton":
                    ExportFromAssetData<USkeleton>(provider, _folderNames[5], asset);
                    break;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error processing asset {asset.PackageName}: {e.Message}");
        }
    }

    private static void ExportFromGameFiles<T>(T asset, string outFolder) where T : UObject
    {
        try
        {
            var outputPath = Path.Join(_outputDir, outFolder);
            EnsureDirectoryExists(outputPath);
            
            var options = new ExporterOptions { ExportMaterials = _exportMaterials };
            var exporter = new Exporter(asset, options);
            exporter.TryWriteToDir(new DirectoryInfo(outputPath), out _, out var fileName);

            if (_generateJson)
            {
                var json = JsonConvert.SerializeObject(asset, Formatting.Indented);
                var jsonFile = fileName.SubstringBefore(".") + ".json";
                File.WriteAllText(jsonFile, json);
                if (_printSuccess) 
                {
                    lock (Console.Out)
                    {
                        Console.WriteLine(jsonFile);
                    }
                }
            } 
            else if (_printSuccess) 
            {
                lock (Console.Out)
                {
                    Console.WriteLine(fileName);
                }
            }
        }
        catch (Exception e)
        {
            lock (Console.Out)
            {
                Console.WriteLine($"Error exporting asset: {e}");
            }
        }
    }

    private static void ExportFromAssetData<T>(DefaultFileProvider provider, string outFolder,
        FAssetData asset, FAssetData extraAsset = null) where T : UObject
    {
        var contentDir = provider.ProjectName + "/Content";
        var name = asset.AssetName.ToString();
        var dir = asset.PackagePath.ToString().Remove(0, 5);
        var jsonDir = Path.Join(_outputDir, outFolder, contentDir, dir);
        var path = Path.Join(contentDir, dir, name).Replace(Path.DirectorySeparatorChar, '/');

        if (File.Exists(Path.Join(_outputDir, outFolder, path) + ".json") && !_replaceFiles)
        {
            if (_printSuccess) 
            {
                lock (Console.Out)
                {
                    Console.WriteLine("Skipping " + Path.Join(_outputDir, outFolder, path));
                }
            }
            return;
        }

        var objPath = asset.ObjectPath.Split("/");
        var objName = objPath[^1].Split(".");
        if (objPath.Length > 1 && !string.Equals(objName[0], objName[1], StringComparison.CurrentCultureIgnoreCase)) return;

        var refObject = provider.LoadPackageObject<T>(path);
        if (refObject is UAnimSequence animSeq && animSeq.AdditiveAnimType != EAdditiveAnimationType.AAT_None)
        {
            animAdditives.Add(animSeq.GetPathName().Split(".")[0]);
        }

        if (extraAsset != null)
        {
            var tAnimSeqName = extraAsset.AssetName.ToString();
            var tAnimSeqDir = extraAsset.PackagePath.ToString().Remove(0, 5);
            var tPath = Path.Join(contentDir, tAnimSeqDir, tAnimSeqName).Replace(Path.DirectorySeparatorChar, '/');
            var addUAnimSequence = provider.LoadPackageObject<UAnimSequence>(tPath);
            if (refObject is UAnimSequence refUAnimSequence)
            {
                refUAnimSequence.RefPoseSeq = new ResolvedLoadedObject(addUAnimSequence);
                refObject = refUAnimSequence as T;
            }
        }

        if (_generateJson)
        {
            try
            {
                var json = JsonConvert.SerializeObject(refObject, Formatting.Indented);
                EnsureDirectoryExists(jsonDir);
                File.WriteAllText(Path.Join(_outputDir, outFolder, path) + ".json", json);
                if (_printSuccess) 
                {
                    lock (Console.Out)
                    {
                        Console.WriteLine(Path.Join(_outputDir, outFolder, path) + ".json");
                    }
                }
            }
            catch (Exception e)
            {
                lock (Console.Out)
                {
                    Console.WriteLine($"Error creating JSON for {path}: {e}");
                }
            }
        }
        
        try
        {
            var options = new ExporterOptions { ExportMaterials = _exportMaterials };
            var exporter = new Exporter(refObject!, options);
            exporter.TryWriteToDir(new DirectoryInfo(Path.Join(_outputDir, outFolder)), out _, out var fileName);
            if (_printSuccess) 
            {
                lock (Console.Out)
                {
                    Console.WriteLine(fileName);
                }
            }
        }
        catch (Exception e)
        {
            lock (Console.Out)
            {
                Console.WriteLine($"Error exporting {path}: {e}");
            }
        }
    }

    private static readonly object _dirCreationLock = new object();
    private static readonly HashSet<string> _createdDirectories = new HashSet<string>();
    
    private static void EnsureDirectoryExists(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        
        lock (_dirCreationLock)
        {
            if (!_createdDirectories.Contains(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                _createdDirectories.Add(path);
            }
        }
    }

    private static void CreateBlenderSKs(DefaultFileProvider provider, string outFolder)
    {
        var blenderDir = Path.Join(_outputDir, outFolder, "Blender");
        var contentDir = "Game";
        if (!Directory.Exists(Path.Join(_outputDir, outFolder, contentDir))) return;
        if (!Directory.Exists(blenderDir)) Directory.CreateDirectory(blenderDir);
        foreach (var file in Directory.GetFiles(Path.Join(_outputDir, outFolder, contentDir),
                     "*.*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) == ".json") continue;
            File.Copy(file, Path.Join(blenderDir, Path.GetFileName(file)), true);
        }
    }

    private static void PrintAnimAdditives()
    {
        if (animAdditives.IsEmpty) return;
        var additiveFile = Path.Join(_outputDir, "additives.txt");
        if (!File.Exists(additiveFile)) File.Create(additiveFile).Close();
        File.WriteAllLines(additiveFile, animAdditives.ToArray());
    }
}