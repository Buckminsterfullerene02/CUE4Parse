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
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
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
    private static bool _dumpBlueprints = false;
    private static bool _dumpRegistryAssets = false;
    private static string _blueprintDumpOutput = "BlueprintDump.txt";
    private static string _registryDumpOutput = "AssetRegistryAssets.txt";
    private static string _assetRegistryPath = null;
    private static ETexturePlatform _texturePlatform = ETexturePlatform.DesktopMobile;
    private static bool _moveBlueprints = false;
    private static string _moveBlueprintsSource = null;
    private static string _moveBlueprintsCooked = null;
    private static bool _dryRun = false;
    private static string _assetTypesFile = null;
    private static string _blacklistAssetTypesFile = null;
    private static string _ignorePathPrefixesFile = null;
    
    private const string AssetTypesList = "DiscoveredAssetTypes.txt";
    
    private static readonly HashSet<string> _assetTypesToCopy = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _assetTypesToExclude = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _ignorePathPrefixes = new(StringComparer.OrdinalIgnoreCase);
    
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

            // If in dump blueprints mode, dump blueprint/widget info and exit
            if (_dumpBlueprints)
            {
                Console.WriteLine("Dumping blueprint asset info...\n");
                await DumpBlueprints();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            // If in dump asset registry mode, write all asset package names and exit
            if (_dumpRegistryAssets)
            {
                Console.WriteLine("Dumping all mounted file paths...\n");
                await DumpAssetList();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            // If in move-blueprints mode, move matching assets from project dir to cooked dir
            if (_moveBlueprints)
            {
                // Load asset type filter from file (optional for -mb; if not found, all uassets are moved)
                if (!LoadAssetTypeFilters(requireFilter: false))
                {
                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                    return;
                }

                Console.WriteLine("Moving blueprint assets from project to cooked directory...\n");
                MoveAssets();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            // Load asset type filter from file for export mode
            if (!LoadAssetTypeFilters(requireFilter: true))
            {
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"Target: {_projectDir}");
            if (_assetTypesToCopy.Count > 0)
            {
                Console.WriteLine($"Asset Types Whitelisted ({_assetTypesToCopy.Count}):");
                foreach (var assetType in _assetTypesToCopy.OrderBy(x => x))
                {
                    Console.WriteLine($"  - {assetType}");
                }
            }
            else if (_assetTypesToExclude.Count > 0)
            {
                Console.WriteLine($"Asset Types Blacklisted ({_assetTypesToExclude.Count}):");
                foreach (var assetType in _assetTypesToExclude.OrderBy(x => x))
                {
                    Console.WriteLine($"  - {assetType}");
                }
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
                
                case "--dump-blueprints":
                case "-b":
                    _dumpBlueprints = true;
                    break;

                case "--dump-registry-assets":
                case "-dra":
                    _dumpRegistryAssets = true;
                    break;
                
                case "--blueprint-output":
                case "-bo":
                    if (i + 1 < args.Length)
                        _blueprintDumpOutput = args[++i];
                    break;

                case "--registry-output":
                case "-ro":
                    if (i + 1 < args.Length)
                        _registryDumpOutput = args[++i];
                    break;
                
                case "--asset-registry":
                case "-ar":
                    if (i + 1 < args.Length)
                        _assetRegistryPath = args[++i];
                    break;
                
                case "--move-blueprints":
                case "-mb":
                    _moveBlueprints = true;
                    break;
                
                case "--dry-run":
                case "-dr":
                    _dryRun = true;
                    break;
                
                case "--source":
                case "-s":
                    if (i + 1 < args.Length)
                        _moveBlueprintsSource = args[++i];
                    break;
                
                case "--cooked":
                case "-c":
                    if (i + 1 < args.Length)
                        _moveBlueprintsCooked = args[++i];
                    break;
                
                case "--asset-types":
                case "-at":
                    if (i + 1 < args.Length)
                        _assetTypesFile = args[++i];
                    break;

                case "--blacklist-asset-types":
                case "-bat":
                    if (i + 1 < args.Length)
                        _blacklistAssetTypesFile = args[++i];
                    break;

                case "--ignore-path-prefix":
                case "-ipp":
                    if (i + 1 < args.Length)
                    {
                        var prefix = NormalizePathPrefix(args[++i]);
                        if (!string.IsNullOrWhiteSpace(prefix))
                            _ignorePathPrefixes.Add(prefix);
                    }
                    break;

                case "--ignore-path-prefixes-file":
                case "-ippf":
                    if (i + 1 < args.Length)
                        _ignorePathPrefixesFile = args[++i];
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
        if (string.IsNullOrEmpty(_pakDir) && !_moveBlueprints)
        {
            Console.WriteLine("Error: --pakdir is required");
            return false;
        }

        // Output directory is not required when listing asset types
        if (!_listAssetTypes && !_extractTextures && !_dumpBlueprints && !_dumpRegistryAssets && !_moveBlueprints && string.IsNullOrEmpty(_projectDir))
        {
            Console.WriteLine("Error: --output is required");
            return false;
        }

        // move-blueprints requires --source and --cooked (--pakdir not required)
        if (_moveBlueprints)
        {
            if (string.IsNullOrEmpty(_moveBlueprintsSource))
            {
                Console.WriteLine("Error: --move-blueprints requires --source <projectDir>");
                return false;
            }
            if (string.IsNullOrEmpty(_moveBlueprintsCooked))
            {
                Console.WriteLine("Error: --move-blueprints requires --cooked <cookedDir>");
                return false;
            }
        }

        if (!_moveBlueprints && !Directory.Exists(_pakDir))
        {
            Console.WriteLine($"Error: Pak directory does not exist: {_pakDir}");
            return false;
        }

        if (!string.IsNullOrEmpty(_assetTypesFile) && !string.IsNullOrEmpty(_blacklistAssetTypesFile))
        {
            Console.WriteLine("Error: --asset-types and --blacklist-asset-types cannot be used together.");
            return false;
        }

        if (!string.IsNullOrEmpty(_ignorePathPrefixesFile))
        {
            var ignoreFilePath = ResolveIgnorePathPrefixesFilePath();
            if (!LoadIgnorePathPrefixesFromFile(ignoreFilePath))
            {
                Console.WriteLine($"Error: Could not load ignore path prefixes from {ignoreFilePath}");
                return false;
            }
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
        Console.WriteLine("  --dump-blueprints, -b     Dump Blueprint/WidgetBlueprint asset info to a txt file");
        Console.WriteLine("  --dump-registry-assets, -dra  Dump all mounted provider file paths to a txt file");
        Console.WriteLine("  --blueprint-output, -bo <path>  Output path for blueprint dump (default: BlueprintDump.txt)");
        Console.WriteLine("  --registry-output, -ro <path>   Output path for registry dump (default: AssetRegistryAssets.txt)");
        Console.WriteLine("  --asset-registry, -ar <path>    Path to a pre-extracted AssetRegistry.bin file");
        Console.WriteLine("  --move-blueprints, -mb    Copy blueprint assets from project dir to cooked dir (no pak required)");
        Console.WriteLine("  --source, -s <path>       Source project root (e.g. F:\\Subnautica2 Modding\\Projects\\Subnautica2)");
        Console.WriteLine("  --cooked, -c <path>       Destination cooked dir (e.g. F:\\Subnautica2 Modding\\Cooked)");
        Console.WriteLine("  --dry-run, -dr            Simulate --move-blueprints without copying any files");
        Console.WriteLine("  --asset-types, -at <path> Path to asset types filter file (default: AssetTypes.txt next to exe)");
        Console.WriteLine("  --blacklist-asset-types, -bat <path>  Path to blacklist asset types file");
        Console.WriteLine("  --ignore-path-prefix, -ipp <prefix>   Ignore mounted paths that start with this prefix (repeatable)");
        Console.WriteLine("  --ignore-path-prefixes-file, -ippf <path>  File containing prefixes to ignore, one per line");
        Console.WriteLine("  --help, -h                Show this help message");
        Console.WriteLine();
        Console.WriteLine("Use --asset-types for whitelist mode (only listed classes are copied/moved).");
        Console.WriteLine("Use --blacklist-asset-types for blacklist mode (listed classes are skipped).");
        Console.WriteLine("If --asset-types is omitted, AssetTypes.txt next to the executable is used in export mode.");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  CookedExport -p \"C:\\Game\\Paks\" -o \"C:\\Output\" -m \"mappings.usmap\"");
    }

    private static string ResolveAssetTypesFilePath()
    {
        if (!string.IsNullOrEmpty(_assetTypesFile))
        {
            return Path.IsPathRooted(_assetTypesFile)
                ? _assetTypesFile
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _assetTypesFile);
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AssetTypes.txt");
    }

    private static string ResolveBlacklistAssetTypesFilePath()
    {
        if (string.IsNullOrEmpty(_blacklistAssetTypesFile))
        {
            return null;
        }

        return Path.IsPathRooted(_blacklistAssetTypesFile)
            ? _blacklistAssetTypesFile
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _blacklistAssetTypesFile);
    }

    private static string ResolveIgnorePathPrefixesFilePath()
    {
        if (string.IsNullOrEmpty(_ignorePathPrefixesFile))
        {
            return null;
        }

        return Path.IsPathRooted(_ignorePathPrefixesFile)
            ? _ignorePathPrefixesFile
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _ignorePathPrefixesFile);
    }

    private static bool LoadIgnorePathPrefixesFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Ignore path prefixes file not found: {filePath}");
                return false;
            }

            var lines = File.ReadAllLines(filePath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                {
                    continue;
                }

                var prefix = NormalizePathPrefix(trimmed);
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    _ignorePathPrefixes.Add(prefix);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading ignore path prefixes from file: {ex.Message}");
            return false;
        }
    }

    private static string NormalizePathPrefix(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('\\', '/');
    }

    private static bool ShouldIgnoreMountedPath(string path)
    {
        if (_ignorePathPrefixes.Count == 0)
        {
            return false;
        }

        var normalizedPath = NormalizePathPrefix(path);
        foreach (var prefix in _ignorePathPrefixes)
        {
            if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (prefix.StartsWith('/') &&
                normalizedPath.StartsWith(prefix.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!prefix.StartsWith('/') &&
                normalizedPath.StartsWith('/' + prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LoadAssetTypeFilters(bool requireFilter)
    {
        _assetTypesToCopy.Clear();
        _assetTypesToExclude.Clear();

        if (!string.IsNullOrEmpty(_blacklistAssetTypesFile))
        {
            var blacklistPath = ResolveBlacklistAssetTypesFilePath();
            if (!LoadAssetTypesFromFile(blacklistPath, _assetTypesToExclude, "blacklist"))
            {
                Console.WriteLine($"Error: Could not load blacklist asset types from {blacklistPath}");
                return false;
            }

            return true;
        }

        var whitelistPath = ResolveAssetTypesFilePath();
        var shouldTryWhitelist = requireFilter || !string.IsNullOrEmpty(_assetTypesFile) || File.Exists(whitelistPath);

        if (!shouldTryWhitelist)
        {
            return true;
        }

        if (!LoadAssetTypesFromFile(whitelistPath, _assetTypesToCopy, "whitelist"))
        {
            if (requireFilter || !string.IsNullOrEmpty(_assetTypesFile))
            {
                Console.WriteLine($"Error: Could not load whitelist asset types from {whitelistPath}");
                return false;
            }

            _assetTypesToCopy.Clear();
            Console.WriteLine($"Warning: Could not load optional whitelist asset types from {whitelistPath}. Continuing without filtering.");
        }

        return true;
    }

    private static bool LoadAssetTypesFromFile(string filePath, HashSet<string> targetSet, string filterKind)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"{filterKind} asset types file not found: {filePath}");
                return false;
            }

            var lines = File.ReadAllLines(filePath);
            targetSet.Clear();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                // Skip empty lines and comments
                if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("#") && !trimmed.StartsWith("//"))
                {
                    targetSet.Add(trimmed);
                }
            }

            if (targetSet.Count == 0)
            {
                Console.WriteLine($"Warning: No asset types found in {filePath}");
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

    private static bool AssetTypePassesFilter(string assetClass)
    {
        if (_assetTypesToCopy.Count > 0)
        {
            return !string.IsNullOrEmpty(assetClass) && _assetTypesToCopy.Contains(assetClass);
        }

        if (_assetTypesToExclude.Count > 0)
        {
            return string.IsNullOrEmpty(assetClass) || !_assetTypesToExclude.Contains(assetClass);
        }

        return true;
    }

    private static bool HasAssetTypeFilter()
    {
        return _assetTypesToCopy.Count > 0 || _assetTypesToExclude.Count > 0;
    }

    /// <summary>
    /// Attempts to load the AssetRegistry from the provider by trying multiple common paths.
    /// Falls back to building a minimal asset list from provider.Files when the registry is empty or missing.
    /// </summary>
    private static List<FAssetData> LoadAssetRegistry(DefaultFileProvider provider)
    {
        List<FAssetData> assets = new();

        // If an external AssetRegistry.bin was provided, use it directly
        if (!string.IsNullOrEmpty(_assetRegistryPath))
        {
            if (File.Exists(_assetRegistryPath))
            {
                try
                {
                    var bytes = File.ReadAllBytes(_assetRegistryPath);
                    using var archive = new FByteArchive(_assetRegistryPath, bytes, provider.Versions);
                    assets.AddRange(new FAssetRegistryState(archive).PreallocatedAssetDataBuffers);
                    Console.WriteLine($"Loaded external AssetRegistry from: {_assetRegistryPath} ({assets.Count} entries)");
                    return assets;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to read external AssetRegistry at {_assetRegistryPath}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"Warning: External AssetRegistry not found: {_assetRegistryPath}");
            }
        }

        // Try common AssetRegistry paths
        var candidates = new[]
        {
            $"{provider.ProjectName}/AssetRegistry.bin",
            $"{provider.ProjectName}/Content/AssetRegistry.bin",
            "AssetRegistry.bin",
        };

        foreach (var candidate in candidates)
        {
            if (provider.TryGetGameFile(candidate, out var registryFile))
            {
                try
                {
                    var bytes = registryFile.Read();
                    using var archive = new FByteArchive(candidate, bytes, provider.Versions);
                    assets.AddRange(new FAssetRegistryState(archive).PreallocatedAssetDataBuffers);
                    if (assets.Count > 0)
                    {
                        Console.WriteLine($"Loaded AssetRegistry from: {candidate} ({assets.Count} entries)");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to read registry at {candidate}: {ex.Message}");
                }
            }
        }

        // If still nothing, check for plugin asset registries (IoStore games often have per-plugin registries)
        if (assets.Count == 0)
        {
            var pluginRegistries = provider.Files.Keys
                .Where(k => k.EndsWith("AssetRegistry.bin", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var regPath in pluginRegistries)
            {
                if (provider.TryGetGameFile(regPath, out var registryFile))
                {
                    try
                    {
                        var bytes = registryFile.Read();
                        using var archive = new FByteArchive(regPath, bytes, provider.Versions);
                        var entries = new FAssetRegistryState(archive).PreallocatedAssetDataBuffers;
                        assets.AddRange(entries);
                        Console.WriteLine($"Loaded plugin AssetRegistry: {regPath} ({entries.Length} entries)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to read registry at {regPath}: {ex.Message}");
                    }
                }
            }
        }

        // Last resort: synthesize asset data from file paths (covers IoStore stripped registry)
        if (assets.Count == 0)
        {
            Console.WriteLine("No AssetRegistry found or it was empty. Falling back to file-based scanning...");
            // Return empty – callers will handle file-based fallback
        }

        return assets;
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

        var assets = LoadAssetRegistry(provider);

        var gameAssets = assets.ToList();

        if (gameAssets.Count == 0)
        {
            Console.WriteLine("Using file-based fallback for asset type discovery (IoStore mode)...\n");
        }
        else
        {
            Console.WriteLine($"Found {gameAssets.Count} assets in registry...\n");
        }

        // Collect all unique asset types with counts
        var assetTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (gameAssets.Count > 0)
        {
            foreach (var asset in gameAssets)
            {
                var assetClass = asset.AssetClass.Text;
                assetTypeCounts.AddOrUpdate(assetClass, 1, (key, oldValue) => oldValue + 1);
            }
        }
        else
        {
            // IoStore fallback: load each file and inspect the primary export type
            var gameContentPrefix = $"{provider.ProjectName}/Content/";
            var gamePluginsPrefix = $"{provider.ProjectName}/Plugins/";
            var gameFiles = provider.Files.Values
                .Where(f => (f.Path.StartsWith(gameContentPrefix, StringComparison.OrdinalIgnoreCase) ||
                             f.Path.StartsWith(gamePluginsPrefix, StringComparison.OrdinalIgnoreCase))
                            && !f.Path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)
                            && !f.Path.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase)
                            && !f.Path.EndsWith(".uptnl", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Console.WriteLine($"Scanning {gameFiles.Count} files...\n");

            var parallelOptions = new ParallelOptions();
            if (!_useMultiThreading) parallelOptions.MaxDegreeOfParallelism = 1;
            else if (_maxDegreeOfParallelism > 0) parallelOptions.MaxDegreeOfParallelism = _maxDegreeOfParallelism;

            Parallel.ForEach(gameFiles, parallelOptions, file =>
            {
                try
                {
                    var pkg = provider.LoadPackage(file);
                    var primary = pkg?.GetExports().FirstOrDefault();
                    if (primary == null) return;
                    var typeName = primary.ExportType;
                    assetTypeCounts.AddOrUpdate(typeName, 1, (_, v) => v + 1);
                }
                catch { /* skip unreadable */ }
            });
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

        var assets = LoadAssetRegistry(provider);

        // Filter for texture assets - registry-based
        var textureAssets = assets.Where(asset => 
            asset.AssetClass.Text.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
             asset.AssetClass.Text.Equals("TextureCube", StringComparison.OrdinalIgnoreCase) ||
             asset.AssetClass.Text.Equals("Texture2DArray", StringComparison.OrdinalIgnoreCase) ||
             asset.AssetClass.Text.Equals("TextureRenderTarget2D", StringComparison.OrdinalIgnoreCase)
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

        if (textureAssets.Count > 0)
        {
            // Registry-based extraction
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
        }
        else
        {
            // IoStore fallback: scan provider.Files for uasset/uexp/utoc that match texture paths
            Console.WriteLine("Registry returned 0 textures. Falling back to file-based texture scanning (IoStore mode)...\n");

            var gameContentPrefix = $"{provider.ProjectName}/Content/";
            var gamePluginsPrefix = $"{provider.ProjectName}/Plugins/";
            var textureFiles = provider.Files.Values
                .Where(f => (f.Path.StartsWith(gameContentPrefix, StringComparison.OrdinalIgnoreCase) ||
                             f.Path.StartsWith(gamePluginsPrefix, StringComparison.OrdinalIgnoreCase))
                            && !f.Path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)
                            && !f.Path.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase)
                            && !f.Path.EndsWith(".uptnl", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Console.WriteLine($"Scanning {textureFiles.Count} asset files for textures...\n");

            if (_useMultiThreading)
            {
                Parallel.ForEach(textureFiles, parallelOptions, file =>
                {
                    if (ExtractTextureFromFile(provider, file, outputDir))
                        Interlocked.Increment(ref successCount);
                    else
                        Interlocked.Increment(ref failCount);
                });
            }
            else
            {
                foreach (var file in textureFiles)
                {
                    if (ExtractTextureFromFile(provider, file, outputDir))
                        successCount++;
                    else
                        failCount++;
                }
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
            var packagePath = asset.PackagePath.ToString();
            string assetPath;
            string dir;
            if (packagePath.StartsWith("/Game", StringComparison.OrdinalIgnoreCase))
            {
                dir = packagePath.Substring(5); // remove "/Game"
                assetPath = Path.Join(contentDir, dir, name).Replace(Path.DirectorySeparatorChar, '/');
            }
            else
            {
                var pluginRelDir = packagePath.TrimStart('/');
                assetPath = $"{provider.ProjectName}/Plugins/{pluginRelDir}/{name}";
                dir = "/" + pluginRelDir;
            }

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

    private static async Task DumpBlueprints()
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
        assets.AddRange(LoadAssetRegistry(provider));

        // Filter using _assetTypesToCopy if loaded, otherwise use sensible defaults
        var bpFilterTypes = _assetTypesToCopy.Count > 0
            ? _assetTypesToCopy
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BlueprintGeneratedClass", 
                "UBlueprintGeneratedClass",
                "WidgetBlueprintGeneratedClass",
                "UWidgetBlueprintGeneratedClass",
                "AnimBlueprintGeneratedClass",
                "RigVMBlueprintGeneratedClass",
                "GameplayAbilityBlueprint",
                "ControlRigBlueprintGeneratedClass"
            };

        var blueprintAssets = assets.Where(asset =>
            bpFilterTypes.Contains(asset.AssetClass.Text)
        ).ToList();

        Console.WriteLine($"Found {blueprintAssets.Count} blueprint/widget assets in registry...\n");

        var outputPath = Path.IsPathRooted(_blueprintDumpOutput)
            ? _blueprintDumpOutput
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _blueprintDumpOutput);

        using var writer = new StreamWriter(outputPath, false);

        int count = 0;

        if (blueprintAssets.Count > 0)
        {
            // Registry-based path
            foreach (var asset in blueprintAssets)
            {
                if (!asset.PackagePath.ToString().StartsWith("/Game", StringComparison.OrdinalIgnoreCase) && 
                    !asset.PackagePath.ToString().StartsWith("/UWE", StringComparison.OrdinalIgnoreCase)  &&
                    !asset.PackagePath.ToString().StartsWith("/MeshBlend", StringComparison.OrdinalIgnoreCase))
                {
                    // Skip engine assets
                    continue;
                }
                var line = FormatBlueprintLine(asset);
                writer.WriteLine(line);
                count++;
                if (_printSuccess) Console.WriteLine(line);
            }
        }
    }

    private static async Task DumpAssetList()
    {
        var provider = new DefaultFileProvider(_pakDir, SearchOption.TopDirectoryOnly,
            new VersionContainer(_version), StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(_mapping))
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_mapping);

        provider.Initialize();

        if (!string.IsNullOrEmpty(_aesKey))
            await provider.SubmitKeyAsync(new FGuid(), new FAesKey(_aesKey));

        await provider.MountAsync();

        if (_ignorePathPrefixes.Count > 0)
        {
            Console.WriteLine("Ignoring mounted path prefixes:");
            foreach (var prefix in _ignorePathPrefixes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  - {prefix}");
            }
            Console.WriteLine();
        }

        var totalMountedPaths = provider.Files.Count;

        var mountedEntries = provider.Files
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
            .Where(kvp => !ShouldIgnoreMountedPath(kvp.Key))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var outputPath = Path.IsPathRooted(_registryDumpOutput)
            ? _registryDumpOutput
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _registryDumpOutput);

        using var writer = new StreamWriter(outputPath, false);
        foreach (var entry in mountedEntries)
        {
            writer.WriteLine($"{entry.Key} | {entry.Value.Size}");
        }

        var ignoredCount = totalMountedPaths - mountedEntries.Count;
        Console.WriteLine($"Wrote {mountedEntries.Count} mounted file paths to: {outputPath}");
        if (ignoredCount > 0)
        {
            Console.WriteLine($"Ignored {ignoredCount} path(s) by prefix filter.");
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
        var assets = LoadAssetRegistry(provider);

        var gameAssets = assets.ToList();
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

        if (gameAssets.Count > 0)
        {
            // Registry-based export
            if (_useMultiThreading)
            {
                Parallel.ForEach(gameAssets, parallelOptions, asset => { ProcessAsset(provider, asset); });
            }
            else
            {
                foreach (var asset in gameAssets) ProcessAsset(provider, asset);
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
            if (!AssetTypePassesFilter(assetClass))
            {
                return;
            }

            var name = asset.AssetName.ToString();
            var packagePath = asset.PackagePath.ToString();

            // Build physical path: strip leading "/Game" -> map to ProjectName/Content,
            // or for plugin assets ("/PluginName/...") -> map to ProjectName/Plugins/PluginName/Content/...
            // The simplest approach: use asset.PackageName which gives "/Game/Dir/Name" or "/PluginMount/Dir/Name"
            // and let CopyAssetFiles resolve via TryGetGameFile which uses VFS paths.
            string path;
            if (packagePath.StartsWith("/Game", StringComparison.OrdinalIgnoreCase))
            {
                var dir = packagePath.Substring(5); // remove "/Game"
                var contentDir = provider.ProjectName + "/Content";
                path = Path.Join(contentDir, dir, name).Replace(Path.DirectorySeparatorChar, '/');
            }
            else
            {
                // Plugin-mounted path: packagePath like "/PluginName/SubDir"
                // Provider VFS has it as "ProjectName/Plugins/PluginName/Content/SubDir"
                // but we can also just try loading by package name directly
                var dir = packagePath.TrimStart('/');
                path = $"{provider.ProjectName}/Plugins/{dir}/{name}";
            }
            
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
            // Determine if this is a plugin asset (contains /Plugins/) or a regular content asset
            var pluginsIndex = assetPath.IndexOf("/Plugins/", StringComparison.OrdinalIgnoreCase);
            var contentIndex = assetPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);

            string relativePath;
            string baseFolder;

            if (pluginsIndex >= 0)
            {
                // e.g. ProjectName/Plugins/PluginName/Content/Sub/Asset -> Plugins/PluginName/Content/Sub/Asset
                relativePath = assetPath.Substring(pluginsIndex + 1); // strip leading slash
                baseFolder = string.Empty; // full relative path already includes Plugins/...
            }
            else if (contentIndex >= 0)
            {
                relativePath = assetPath.Substring(contentIndex + "/Content/".Length);
                baseFolder = "Content";
            }
            else return;

            var fileName = Path.GetFileName(relativePath);
            var dirPath = Path.GetDirectoryName(relativePath);

            // Build target directory
            var targetDir = string.IsNullOrEmpty(baseFolder)
                ? Path.Join(_projectDir, dirPath)
                : Path.Join(_projectDir, baseFolder, dirPath);
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

    private static string FormatBlueprintLine(FAssetData asset)
    {
        var assetType = asset.AssetClass.Text;
        var packageName = asset.PackageName.ToString(); // e.g. /Game/Art/Animation/.../ABP_SN2PlayerCharacter_Main
        var name = packageName.Substring(packageName.LastIndexOf('/') + 1);
        var path = asset.PackagePath.ToString() + "/";

        string parentClass = string.Empty;
        if (asset.TagsAndValues != null)
        {
            var parentKey = asset.TagsAndValues.Keys
                .FirstOrDefault(k => k.Text.Equals("ParentClass", StringComparison.OrdinalIgnoreCase));
            if (parentKey != null)
                parentClass = asset.TagsAndValues[parentKey];
        }

        return $"\"{assetType}\",\"{name}\",\"{path}\",\"{parentClass}\";";
    }

    /// <summary>
    /// IoStore fallback: tries to load a file as a UTexture and encode it to PNG.
    /// </summary>
    private static bool ExtractTextureFromFile(DefaultFileProvider provider, GameFile file, string outputDir)
    {
        try
        {
            var pkg = provider.LoadPackage(file);
            if (pkg == null) return false;

            var texture = pkg.GetExports().OfType<UTexture>().FirstOrDefault();
            if (texture == null) return false;

            var processed = Interlocked.Increment(ref _processedAssets);
            if (processed % 100 == 0)
                Console.WriteLine($"Processed {processed} textures...");

            var bitmap = texture.Decode(_texturePlatform);
            if (bitmap == null)
            {
                lock (Console.Out) Console.WriteLine($"[FAIL] Could not decode texture: {file.Name}");
                return false;
            }

            if (texture is UTextureCube)
                bitmap = bitmap.ToPanorama();

            var pngBytes = bitmap.Encode(ETextureFormat.Png, false, out var extension);

            // Build relative output path: handle both Content and Plugins paths
            var path = file.PathWithoutExtension;
            var pluginsMarker = "/Plugins/";
            var pluginsIdx = path.IndexOf(pluginsMarker, StringComparison.OrdinalIgnoreCase);
            var contentMarker = "/Content/";
            var contentIdx = path.IndexOf(contentMarker, StringComparison.OrdinalIgnoreCase);

            string relativePath;
            if (pluginsIdx >= 0)
            {
                // Preserve full Plugins/PluginName/Content/... structure
                relativePath = path.Substring(pluginsIdx + 1); // Plugins/...
            }
            else if (contentIdx >= 0)
            {
                relativePath = path.Substring(contentIdx + contentMarker.Length);
            }
            else
            {
                relativePath = path.TrimStart('/');
            }

            var name = Path.GetFileName(relativePath);
            var dirPart = Path.GetDirectoryName(relativePath) ?? string.Empty;
            var targetDir = Path.Combine(outputDir, dirPart);
            EnsureDirectoryExists(targetDir);

            var targetPath = Path.Combine(targetDir, $"{name}.{extension}");

            if (!_replaceExisting && File.Exists(targetPath))
            {
                Interlocked.Increment(ref _skippedAssets);
                if (_printSkipped)
                    lock (Console.Out) Console.WriteLine($"[SKIP] Already exists: {name}");
                return false;
            }

            File.WriteAllBytes(targetPath, pngBytes);

            if (_printSuccess)
                lock (Console.Out) Console.WriteLine($"[EXPORT] {name}.{extension} ({bitmap.Width}x{bitmap.Height})");

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies files from the source project directory (Content and Plugins/*/Content) whose .uasset
    /// asset class matches the filters, into the cooked output directory preserving structure.
    /// Uses the AssetRegistry (--asset-registry / -ar) to determine asset class when filtering.
    /// </summary>
    private static void MoveAssets()
    {
        var source = _moveBlueprintsSource;
        var cooked = _moveBlueprintsCooked;

        if (!Directory.Exists(source))
        {
            Console.WriteLine($"Error: Source directory does not exist: {source}");
            return;
        }

        // Build asset class lookup from the AssetRegistry if a filter is active
        // Key: package name lower-case (e.g. "/game/assets/foo/bar"), Value: asset class text
        Dictionary<string, string> arClassByPackage = null;

        if (HasAssetTypeFilter())
        {
            if (!string.IsNullOrEmpty(_assetRegistryPath) && File.Exists(_assetRegistryPath))
            {
                try
                {
                    Console.WriteLine($"Loading AssetRegistry for filtering: {_assetRegistryPath}");
                    var bytes = File.ReadAllBytes(_assetRegistryPath);
                    // FByteArchive needs a VersionContainer - use a minimal one
                    using var archive = new FByteArchive(_assetRegistryPath, bytes, new VersionContainer(_version));
                    var arState = new FAssetRegistryState(archive);
                    arClassByPackage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in arState.PreallocatedAssetDataBuffers)
                    {
                        var pkgName = entry.PackageName.ToString(); // e.g. /Game/Assets/Foo/Bar
                        var cls = entry.AssetClass.Text;
                        arClassByPackage[pkgName] = cls;
                    }
                    Console.WriteLine($"AssetRegistry loaded: {arClassByPackage.Count} entries\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not load AssetRegistry for filtering: {ex.Message}");
                    Console.WriteLine("Proceeding without asset type filtering - all .uasset files will be moved.\n");
                }
            }
            else
            {
                Console.WriteLine("Warning: Asset type filtering is set but no --asset-registry (-ar) was provided.");
                Console.WriteLine("Cannot determine asset class without the AssetRegistry.");
                Console.WriteLine("Proceeding without filtering - all .uasset files will be moved.\n");
            }
        }

        // Collect scan roots: Content and Plugins/*/Content
        var scanRoots = new List<(string rootDir, string relBase)>();

        var contentDir = Path.Combine(source, "Content");
        if (Directory.Exists(contentDir))
            scanRoots.Add((contentDir, "Content"));

        var pluginsDir = Path.Combine(source, "Plugins");
        if (Directory.Exists(pluginsDir))
        {
            foreach (var pluginDir in Directory.GetDirectories(pluginsDir))
            {
                var pluginContentDir = Path.Combine(pluginDir, "Content");
                if (Directory.Exists(pluginContentDir))
                {
                    var pluginName = Path.GetFileName(pluginDir);
                    scanRoots.Add((pluginContentDir, Path.Combine("Plugins", pluginName, "Content")));
                }
            }
        }

        var uassetFiles = scanRoots
            .SelectMany(r => Directory.EnumerateFiles(r.rootDir, "*.uasset", SearchOption.AllDirectories)
                .Select(f => (file: f, rootDir: r.rootDir, relBase: r.relBase)))
            .ToList();

        Console.WriteLine($"Found {uassetFiles.Count} .uasset files to scan...\n");
        if (arClassByPackage != null)
        {
            if (_assetTypesToCopy.Count > 0)
                Console.WriteLine($"Whitelist filtering by {_assetTypesToCopy.Count} asset type(s) using AssetRegistry index\n");
            else if (_assetTypesToExclude.Count > 0)
                Console.WriteLine($"Blacklist filtering by {_assetTypesToExclude.Count} asset type(s) using AssetRegistry index\n");
        }
        if (_dryRun)
            Console.WriteLine("[DRY RUN] No files will be moved.\n");

        int moved = 0, skipped = 0, failed = 0;

        foreach (var (filePath, rootDir, relBase) in uassetFiles)
        {
            try
            {
                var relPath = Path.GetRelativePath(rootDir, filePath);

                // Filter by asset class using AR index if available
                if (HasAssetTypeFilter() && arClassByPackage != null)
                {
                    // Derive the package name from the file path
                    // relBase is either "Content" or "Plugins/PluginName/Content"
                    // relPath is e.g. "Assets\Foo\Bar.uasset"
                    var packageRelPath = Path.ChangeExtension(relPath, null); // strip .uasset
                    string packageName;

                    if (relBase.Equals("Content", StringComparison.OrdinalIgnoreCase))
                    {
                        // Content/Assets/Foo/Bar -> /Game/Assets/Foo/Bar
                        packageName = "/Game/" + packageRelPath.Replace(Path.DirectorySeparatorChar, '/');
                    }
                    else
                    {
                        // Plugins/PluginName/Content/Assets/Foo/Bar -> /PluginName/Assets/Foo/Bar
                        // relBase = "Plugins/PluginName/Content"
                        var parts = relBase.Replace('\\', '/').Split('/');
                        // parts[0]=Plugins, parts[1]=PluginName, parts[2]=Content
                        var pluginName = parts.Length >= 2 ? parts[1] : "Unknown";
                        packageName = "/" + pluginName + "/" + packageRelPath.Replace(Path.DirectorySeparatorChar, '/');
                    }

                    arClassByPackage.TryGetValue(packageName, out var assetClass);
                    if (!AssetTypePassesFilter(assetClass))
                    {
                        skipped++;
                        if (_printSkipped)
                        {
                            if (_assetTypesToCopy.Count > 0)
                                Console.WriteLine($"[SKIP] Not in whitelist (class={assetClass ?? "unknown"}): {packageName}");
                            else
                                Console.WriteLine($"[SKIP] Matched blacklist (class={assetClass ?? "unknown"}): {packageName}");
                        }
                        continue;
                    }
                }

                var targetPath = Path.Combine(cooked, relBase, relPath);

                if (!_replaceExisting && File.Exists(targetPath))
                {
                    skipped++;
                    if (_printSkipped)
                        Console.WriteLine($"[SKIP] Already exists: {relBase}/{relPath}");
                    continue;
                }

                if (_dryRun)
                {
                    Console.WriteLine($"[DRY RUN] Would move: {relBase}/{relPath}");
                    moved++;
                    continue;
                }

                var targetDir = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(targetDir);
                File.Move(filePath, targetPath, overwrite: true);

                // Move associated files (.uexp, .ubulk, .uptnl)
                var basePath = Path.ChangeExtension(filePath, null);
                foreach (var ext in new[] { ".uexp", ".ubulk", ".uptnl" })
                {
                    var sidecar = basePath + ext;
                    if (File.Exists(sidecar))
                    {
                        var sidecarTarget = Path.ChangeExtension(targetPath, null) + ext;
                        File.Move(sidecar, sidecarTarget, overwrite: true);
                    }
                }

                moved++;
                if (_printSuccess)
                    Console.WriteLine($"[MOVE] {relBase}/{relPath}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[ERROR] {filePath}: {ex.Message}");
            }
        }

        Console.WriteLine(_dryRun
            ? $"\n[DRY RUN] Would move: {moved}, Would skip: {skipped}, Errors: {failed}"
            : $"\nDone! Moved: {moved}, Skipped: {skipped}, Failed: {failed}");
    }

    private static void EnsureDirectoryExists(string path)    {
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
