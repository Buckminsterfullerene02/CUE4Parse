using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

public class Program
{
    // Configuration fields
    private static string _pakDir;
    private static string _aesKey = "";
    private static string _mapping;
    private static EGame _version = EGame.GAME_UE5_6;
    private static string _projectDir;
    private static bool _replaceExisting = false;
    private static bool _printSuccess = true;
    private static bool _printSkipped = false;
    private static bool _useMultiThreading = true;
    private static int _maxDegreeOfParallelism = -1;
    private static bool _listAssetTypes = false;
    private static bool _extractTextures = false;
    private static ETexturePlatform _texturePlatform = ETexturePlatform.DesktopMobile;
    
    private const string AssetTypesList = "DiscoveredAssetTypes.txt";
    
    private static readonly HashSet<string> _assetTypesToCopy = new(StringComparer.OrdinalIgnoreCase);
    
    private static readonly Dictionary<string, int> _copiedCounts = new();
    private static readonly object _countLock = new object();
    private static readonly object _dirCreationLock = new object();
    private static readonly HashSet<string> _createdDirectories = new HashSet<string>();
    private static int _processedAssets = 0;
    private static int _skippedAssets = 0;

    static async Task Main(string[] args)
    {
        try
        {
            if (!ParseArguments(args))
            {
                PrintUsage();
                return;
            }

            Console.WriteLine($"Source: {_pakDir}");
            if (!string.IsNullOrEmpty(_mapping))
                Console.WriteLine($"Mapping: {_mapping}");
            Console.WriteLine($"Version: {_version}");
            Console.WriteLine();

            // If in list mode, just list asset types and exit
            if (_listAssetTypes)
            {
                Console.WriteLine("Scanning pak files for asset types...\n");
                await ListAssetTypes();
                Console.WriteLine($"\nAsset types have been written to: {AssetTypesList}");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            // If in extract textures mode, extract all textures as PNG and exit
            if (_extractTextures)
            {
                Console.WriteLine("Extracting all textures as PNG...\n");
                await ExtractTextures();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            // Load asset types from file for export mode
            var assetTypesFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AssetTypes.txt");
            if (!LoadAssetTypesFromFile(assetTypesFile))
            {
                Console.WriteLine($"Error: Could not load asset types from {assetTypesFile}");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"Target: {_projectDir}");
            Console.WriteLine($"Asset Types Enabled ({_assetTypesToCopy.Count}):");
            foreach (var assetType in _assetTypesToCopy.OrderBy(x => x))
            {
                Console.WriteLine($"  - {assetType}");
            }
            
            Console.WriteLine($"\nMulti-threading: {(_useMultiThreading ? "Enabled" : "Disabled")}");
            if (_useMultiThreading)
            {
                var maxThreads = _maxDegreeOfParallelism == -1 ? Environment.ProcessorCount : _maxDegreeOfParallelism;
                Console.WriteLine($"Max threads: {maxThreads}");
            }
            
            Console.WriteLine($"Replace existing: {(_replaceExisting ? "Yes" : "No")}");
            Console.WriteLine("\nStarting export...\n");
            
            await ExportAssets();
            
            Console.WriteLine($"\nTotal assets processed: {_processedAssets}");
            Console.WriteLine($"Total assets skipped: {_skippedAssets}");
            
            lock (_countLock)
            {
                foreach (var kvp in _copiedCounts.OrderBy(x => x.Key))
                {
                    Console.WriteLine($"{kvp.Key}: {kvp.Value} copied");
                }
            }
            
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

    private static bool ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--pakdir":
                case "-p":
                    if (i + 1 < args.Length)
                        _pakDir = args[++i];
                    break;
                
                case "--output":
                case "-o":
                    if (i + 1 < args.Length)
                        _projectDir = args[++i];
                    break;
                
                case "--mapping":
                case "-m":
                    if (i + 1 < args.Length)
                        _mapping = args[++i];
                    break;
                
                case "--aeskey":
                case "-k":
                    if (i + 1 < args.Length)
                        _aesKey = args[++i];
                    break;
                
                case "--version":
                case "-v":
                    if (i + 1 < args.Length)
                    {
                        var versionStr = args[++i];
                        if (Enum.TryParse<EGame>(versionStr, true, out var version))
                            _version = version;
                        else
                            Console.WriteLine($"Warning: Unknown version '{versionStr}', using default {_version}");
                    }
                    break;
                
                case "--replace":
                case "-r":
                    _replaceExisting = true;
                    break;
                
                case "--no-multithread":
                    _useMultiThreading = false;
                    break;
                
                case "--threads":
                case "-t":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var threads))
                        _maxDegreeOfParallelism = threads;
                    break;
                
                case "--print-success":
                    _printSuccess = true;
                    break;
                
                case "--no-print-success":
                    _printSuccess = false;
                    break;
                
                case "--print-skipped":
                    _printSkipped = true;
                    break;
                
                case "--list-asset-types":
                case "-l":
                    _listAssetTypes = true;
                    break;
                
                case "--extract-textures":
                case "-e":
                    _extractTextures = true;
                    break;
                
                case "--help":
                case "-h":
                case "/?":
                    return false;
                
                default:
                    Console.WriteLine($"Warning: Unknown argument '{args[i]}'");
                    break;
            }
        }

        // Validate required arguments
        if (string.IsNullOrEmpty(_pakDir))
        {
            Console.WriteLine("Error: --pakdir is required");
            return false;
        }

        // Output directory is not required when listing asset types
        if (!_listAssetTypes && !_extractTextures && string.IsNullOrEmpty(_projectDir))
        {
            Console.WriteLine("Error: --output is required");
            return false;
        }

        if (!Directory.Exists(_pakDir))
        {
            Console.WriteLine($"Error: Pak directory does not exist: {_pakDir}");
            return false;
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("CookedExport - Export cooked assets from Unreal Engine pak files");
        Console.WriteLine();
        Console.WriteLine("Usage: CookedExport [options]");
        Console.WriteLine();
        Console.WriteLine("Required options:");
        Console.WriteLine("  --pakdir, -p <path>       Path to the directory containing .pak files");
        Console.WriteLine("  --output, -o <path>       Output directory for exported assets");
        Console.WriteLine();
        Console.WriteLine("Optional options:");
        Console.WriteLine("  --mapping, -m <path>      Path to .usmap mapping file");
        Console.WriteLine("  --aeskey, -k <key>        AES encryption key (if required)");
        Console.WriteLine("  --version, -v <version>   Game version (e.g., GAME_UE5_6, GAME_UE5_5)");
        Console.WriteLine("  --replace, -r             Replace existing files");
        Console.WriteLine("  --no-multithread          Disable multi-threading");
        Console.WriteLine("  --threads, -t <num>       Max number of threads (-1 for all cores)");
        Console.WriteLine("  --print-success           Print successful copies (default: true)");
        Console.WriteLine("  --no-print-success        Don't print successful copies");
        Console.WriteLine("  --print-skipped           Print skipped assets");
        Console.WriteLine("  --list-asset-types, -l    List all asset types in pak files and exit");
        Console.WriteLine("  --extract-textures, -e    Extract all textures as PNG files");
        Console.WriteLine("  --help, -h                Show this help message");
        Console.WriteLine();
        Console.WriteLine("Asset types to export should be listed in AssetTypes.txt (one per line)");
        Console.WriteLine("in the same directory as the executable.");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  CookedExport -p \"C:\\Game\\Paks\" -o \"C:\\Output\" -m \"mappings.usmap\"");
    }

    private static bool LoadAssetTypesFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Asset types file not found: {filePath}");
                return false;
            }

            var lines = File.ReadAllLines(filePath);
            _assetTypesToCopy.Clear();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                // Skip empty lines and comments
                if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("#") && !trimmed.StartsWith("//"))
                {
                    _assetTypesToCopy.Add(trimmed);
                }
            }

            if (_assetTypesToCopy.Count == 0)
            {
                Console.WriteLine("Warning: No asset types found in AssetTypes.txt");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading asset types from file: {ex.Message}");
            return false;
        }
    }

    private static async Task ListAssetTypes()
    {
        var provider = new DefaultFileProvider(_pakDir, SearchOption.TopDirectoryOnly,
            new VersionContainer(_version), StringComparer.OrdinalIgnoreCase);
        
        if (!string.IsNullOrEmpty(_mapping)) 
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_mapping);
        
        provider.Initialize();
        
        if (!string.IsNullOrEmpty(_aesKey)) 
            await provider.SubmitKeyAsync(new FGuid(), new FAesKey(_aesKey));
        
        await provider.MountAsync();

        List<FAssetData> assets = new();
        var assetRegistryPath = $"{provider.ProjectName}/AssetRegistry.bin";
        
        if (provider.TryGetGameFile(assetRegistryPath, out var assetRegistryFile))
        {
            var assetArchive = await assetRegistryFile.SafeCreateReaderAsync();
            if (assetArchive is not null)
            {
                assets.AddRange(new FAssetRegistryState(assetArchive).PreallocatedAssetDataBuffers);
            }
        }

        var gameAssets = assets.Where(asset => asset.PackagePath.ToString().StartsWith("/Game")).ToList();
        Console.WriteLine($"Found {gameAssets.Count} assets in registry...\n");

        // Collect all unique asset types with counts
        var assetTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var asset in gameAssets)
        {
            var assetClass = asset.AssetClass.Text;
            assetTypeCounts.AddOrUpdate(assetClass, 1, (key, oldValue) => oldValue + 1);
        }

        // Write to file
        var sortedAssetTypes = assetTypeCounts.OrderBy(kvp => kvp.Key).ToList();
        
        var outputPath = Path.IsPathRooted(AssetTypesList) 
            ? AssetTypesList 
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AssetTypesList);

        using (var writer = new StreamWriter(outputPath))
        {
            foreach (var kvp in sortedAssetTypes)
            {
                writer.WriteLine($"{kvp.Key}");
                Console.WriteLine($"  {kvp.Key}: {kvp.Value} assets");
            }
        }

        Console.WriteLine($"\nTotal unique asset types: {sortedAssetTypes.Count}");
    }

    private static async Task ExtractTextures()
    {
        var provider = new DefaultFileProvider(_pakDir, SearchOption.TopDirectoryOnly,
            new VersionContainer(_version, _texturePlatform), StringComparer.OrdinalIgnoreCase);
        
        if (!string.IsNullOrEmpty(_mapping)) 
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_mapping);
        
        provider.Initialize();
        
        if (!string.IsNullOrEmpty(_aesKey)) 
            await provider.SubmitKeyAsync(new FGuid(), new FAesKey(_aesKey));
        
        await provider.MountAsync();

        // Determine output directory
        var outputDir = string.IsNullOrEmpty(_projectDir) 
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtractedTextures")
            : _projectDir;

        Console.WriteLine($"Output directory: {outputDir}\n");

        List<FAssetData> assets = new();
        var assetRegistryPath = $"{provider.ProjectName}/AssetRegistry.bin";
        
        if (provider.TryGetGameFile(assetRegistryPath, out var assetRegistryFile))
        {
            var assetArchive = await assetRegistryFile.SafeCreateReaderAsync();
            if (assetArchive is not null)
            {
                assets.AddRange(new FAssetRegistryState(assetArchive).PreallocatedAssetDataBuffers);
            }
        }

        // Filter for texture assets
        var textureAssets = assets.Where(asset => 
            asset.PackagePath.ToString().StartsWith("/Game") &&
            (asset.AssetClass.Text.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
             asset.AssetClass.Text.Equals("TextureCube", StringComparison.OrdinalIgnoreCase) ||
             asset.AssetClass.Text.Equals("Texture2DArray", StringComparison.OrdinalIgnoreCase) ||
             asset.AssetClass.Text.Equals("TextureRenderTarget2D", StringComparison.OrdinalIgnoreCase))
        ).ToList();

        Console.WriteLine($"Found {textureAssets.Count} texture assets in registry...\n");

        var parallelOptions = new ParallelOptions();
        if (_useMultiThreading && _maxDegreeOfParallelism > 0)
        {
            parallelOptions.MaxDegreeOfParallelism = _maxDegreeOfParallelism;
        }
        else if (!_useMultiThreading)
        {
            parallelOptions.MaxDegreeOfParallelism = 1;
        }

        var successCount = 0;
        var failCount = 0;

        if (_useMultiThreading)
        {
            Parallel.ForEach(textureAssets, parallelOptions, asset =>
            {
                if (ExtractTexture(provider, asset, outputDir))
                    Interlocked.Increment(ref successCount);
                else
                    Interlocked.Increment(ref failCount);
            });
        }
        else
        {
            foreach (var asset in textureAssets)
            {
                if (ExtractTexture(provider, asset, outputDir))
                    successCount++;
                else
                    failCount++;
            }
        }

        Console.WriteLine($"\nExtraction complete!");
        Console.WriteLine($"Successfully extracted: {successCount}");
        Console.WriteLine($"Failed: {failCount}");
    }

    private static bool ExtractTexture(DefaultFileProvider provider, FAssetData asset, string outputDir)
    {
        try
        {
            var processed = Interlocked.Increment(ref _processedAssets);
            if (processed % 100 == 0)
            {
                Console.WriteLine($"Processed {processed} textures...");
            }

            var contentDir = provider.ProjectName + "/Content";
            var name = asset.AssetName.ToString();
            var dir = asset.PackagePath.ToString().Remove(0, 5); // Remove "/Game"
            var assetPath = Path.Join(contentDir, dir, name).Replace(Path.DirectorySeparatorChar, '/');

            // Load the texture object directly
            if (!provider.TryLoadPackageObject<UTexture>(assetPath, out var texture))
            {
                if (_printSkipped)
                {
                    lock (Console.Out)
                    {
                        Console.WriteLine($"[SKIP] Could not load texture: {name}");
                    }
                }
                return false;
            }

            // Decode the texture
            var bitmap = texture.Decode(_texturePlatform);
            if (bitmap == null)
            {
                lock (Console.Out)
                {
                    Console.WriteLine($"[FAIL] Could not decode texture: {name}");
                }
                return false;
            }

            // Handle special texture types
            if (texture is UTextureCube)
            {
                bitmap = bitmap.ToPanorama();
            }

            // Encode to PNG
            var pngBytes = bitmap.Encode(ETextureFormat.Png, false, out var extension);

            // Build output path
            var relativePath = dir.TrimStart('/');
            var targetDir = Path.Combine(outputDir, relativePath);
            EnsureDirectoryExists(targetDir);

            var targetPath = Path.Combine(targetDir, $"{name}.{extension}");

            // Skip if file exists and not replacing
            if (!_replaceExisting && File.Exists(targetPath))
            {
                Interlocked.Increment(ref _skippedAssets);
                if (_printSkipped)
                {
                    lock (Console.Out)
                    {
                        Console.WriteLine($"[SKIP] Already exists: {name}");
                    }
                }
                return false;
            }

            // Write the PNG file
            File.WriteAllBytes(targetPath, pngBytes);

            if (_printSuccess)
            {
                lock (Console.Out)
                {
                    Console.WriteLine($"[EXPORT] {name}.{extension} ({bitmap.Width}x{bitmap.Height})");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            lock (Console.Out)
            {
                Console.WriteLine($"[ERROR] Failed to extract {asset.AssetName}: {ex.Message}");
            }
            return false;
        }
    }

    private static async Task ExportAssets()
    {
        var provider = new DefaultFileProvider(_pakDir, SearchOption.TopDirectoryOnly,
            new VersionContainer(_version), StringComparer.OrdinalIgnoreCase);
        
        if (!string.IsNullOrEmpty(_mapping)) 
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_mapping);
        
        provider.Initialize();
        
        if (!string.IsNullOrEmpty(_aesKey)) 
            await provider.SubmitKeyAsync(new FGuid(), new FAesKey(_aesKey));
        
        await provider.MountAsync();

        await ExportFromAssetRegistry(provider);
    }

    private static async Task ExportFromAssetRegistry(DefaultFileProvider provider)
    {
        List<FAssetData> assets = new();
        var assetRegistryPath = $"{provider.ProjectName}/AssetRegistry.bin";
        
        if (provider.TryGetGameFile(assetRegistryPath, out var assetRegistryFile))
        {
            var assetArchive = await assetRegistryFile.SafeCreateReaderAsync();
            if (assetArchive is not null)
            {
                assets.AddRange(new FAssetRegistryState(assetArchive).PreallocatedAssetDataBuffers);
            }
        }

        var gameAssets = assets.Where(asset => asset.PackagePath.ToString().StartsWith("/Game")).ToList();
        Console.WriteLine($"Found {gameAssets.Count} assets in registry...\n");

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
                ProcessAsset(provider, asset);
            });
        }
        else
        {
            foreach (var asset in gameAssets)
            {
                ProcessAsset(provider, asset);
            }
        }
    }


    private static void ProcessAsset(DefaultFileProvider provider, FAssetData asset)
    {
        try
        {
            var processed = Interlocked.Increment(ref _processedAssets);
            if (processed % 500 == 0)
            {
                Console.WriteLine($"Processed {processed} assets...");
            }

            var assetClass = asset.AssetClass.Text;
            
            // Check if this asset type should be copied
            if (!_assetTypesToCopy.Contains(assetClass))
            {
                return;
            }

            var contentDir = provider.ProjectName + "/Content";
            var name = asset.AssetName.ToString();
            var dir = asset.PackagePath.ToString().Remove(0, 5);
            var path = Path.Join(contentDir, dir, name).Replace(Path.DirectorySeparatorChar, '/');
            
            CopyAssetFiles(provider, path, assetClass);
        }
        catch (Exception e)
        {
            lock (Console.Out)
            {
                Console.WriteLine($"Error processing asset {asset.PackageName}: {e.Message}");
            }
        }
    }

    private static void CopyAssetFiles(DefaultFileProvider provider, string assetPath, string assetType)
    {
        try
        {
            // Extract the path after "Content/" to build the target directory
            var contentIndex = assetPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (contentIndex == -1) return;

            var relativePath = assetPath.Substring(contentIndex + "/Content/".Length);
            var fileName = Path.GetFileName(relativePath);
            var dirPath = Path.GetDirectoryName(relativePath);

            // Build target directory
            var targetDir = Path.Join(_projectDir, "Content", dirPath);
            EnsureDirectoryExists(targetDir);

            var targetUassetPath = Path.Join(targetDir, fileName + ".uasset");

            // Skip if .uasset file already exists and we're not replacing
            if (!_replaceExisting && File.Exists(targetUassetPath))
            {
                Interlocked.Increment(ref _skippedAssets);
                if (_printSkipped)
                {
                    lock (Console.Out)
                    {
                        Console.WriteLine($"[SKIP] {assetType}: {fileName}");
                    }
                }
                return;
            }

            var copied = false;

            // Copy .uasset file
            var uassetPath = assetPath + ".uasset";
            if (provider.TryGetGameFile(uassetPath, out var uassetFile))
            {
                var uassetData = uassetFile.Read();
                File.WriteAllBytes(targetUassetPath, uassetData);
                copied = true;
            }

            // Copy .uexp file if it exists
            var uexpPath = assetPath + ".uexp";
            if (provider.TryGetGameFile(uexpPath, out var uexpFile))
            {
                var targetUexpPath = Path.Join(targetDir, fileName + ".uexp");
                var uexpData = uexpFile.Read();
                File.WriteAllBytes(targetUexpPath, uexpData);
            }

            // Copy .ubulk file if it exists (for textures, sounds, etc.)
            var ubulkPath = assetPath + ".ubulk";
            if (provider.TryGetGameFile(ubulkPath, out var ubulkFile))
            {
                var targetUbulkPath = Path.Join(targetDir, fileName + ".ubulk");
                var ubulkData = ubulkFile.Read();
                File.WriteAllBytes(targetUbulkPath, ubulkData);
            }

            // Copy .uptnl file if it exists (for textures)
            var uptnlPath = assetPath + ".uptnl";
            if (provider.TryGetGameFile(uptnlPath, out var uptnlFile))
            {
                var targetUptnlPath = Path.Join(targetDir, fileName + ".uptnl");
                var uptnlData = uptnlFile.Read();
                File.WriteAllBytes(targetUptnlPath, uptnlData);
            }

            if (copied)
            {
                lock (_countLock)
                {
                    if (!_copiedCounts.ContainsKey(assetType))
                    {
                        _copiedCounts[assetType] = 0;
                    }
                    _copiedCounts[assetType]++;
                }

                if (_printSuccess)
                {
                    lock (Console.Out)
                    {
                        Console.WriteLine($"[COPY] {assetType}: {fileName}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            lock (Console.Out)
            {
                Console.WriteLine($"Error copying {assetType} files for {assetPath}: {e.Message}");
            }
        }
    }

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
}
