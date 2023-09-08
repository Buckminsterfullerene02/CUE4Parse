using CUE4Parse_Conversion;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using Newtonsoft.Json;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;

public class Progam
{
    // SET THESE FOR YOUR GAME
    private const string _pakDir = @"C:\Program Files (x86)\Steam\steamapps\common\Deep Rock Galactic\FSD\Content\Paks";
    private const string _aesKey = ""; // If your game does not have an AES key, leave this empty
    private const string _mapping = ""; // If your game does not need a mappings file, leave this empty
    private const EGame  _version = EGame.GAME_UE4_27; // Check if your game has a custom version, as some do
    private const bool   _exportMaterials = false; // This needs to be false if generating for CAS+UEAT
    private const bool   _useInternalName = true; // Sometimes package path is not set properly meaning paths are not synced, so if it isn't, set to true
    private const string _outputDir = @"F:\DRG Modding\DRGPacker\JSON\Animation Stuff\";
    private const bool   _replaceFiles = false;
    private const bool   _printSuccess = true; // Once you've verified this works, set this to false to reduce console spam
    
    /*
     * Some games (e.g. Valorant) have a stripped AssetRegistry file so set this to true if yours does
     * You'll know if it has a stripped AR if this being false doesn't export any files
     * Warning: Always replaces existing files regardless of _replaceFiles
     * Warning: Has no support for broken RefPose animation additives
     */
    private const bool   _hasStrippedAssetRegistry = false; 
    
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
    private static List<string> animAdditives = new();

    static async Task Main(string[] args)
    {
        DefaultFileProvider provider = await ExportAssets(_hasStrippedAssetRegistry);
        CreateBlenderSKs(provider, _folderNames[5]);
        PrintAnimAdditives();
    }

    private static async Task<DefaultFileProvider> ExportAssets(bool hasStrippedAR = false)
    {
        var provider = new DefaultFileProvider(_pakDir, SearchOption.TopDirectoryOnly, true,
            new VersionContainer(_version));
        if (!string.IsNullOrEmpty(_mapping)) provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_mapping);
        provider.Initialize();
        if (!string.IsNullOrEmpty(_aesKey)) await provider.SubmitKeyAsync(new FGuid(), new FAesKey(_aesKey));
        await provider.MountAsync();
        
        if (hasStrippedAR)
        {
            var folderAssets = provider.Files.Values.Where(file =>
                file.Path.StartsWith(provider.InternalGameName + "/Content/", StringComparison.OrdinalIgnoreCase));
            foreach (var asset in folderAssets)
            {
                if (provider.TryLoadObject(asset.PathWithoutExtension, out UAnimSequence seq)) ExportFromGameFiles(seq, _folderNames[0]);
                if (provider.TryLoadObject(asset.PathWithoutExtension, out UAnimMontage mont)) ExportFromGameFiles(mont, _folderNames[1]);
                if (provider.TryLoadObject(asset.PathWithoutExtension, out UAnimComposite comp)) ExportFromGameFiles(comp, _folderNames[2]);
                if (provider.TryLoadObject(asset.PathWithoutExtension, out USkeletalMesh skm)) ExportFromGameFiles(skm, _folderNames[3]);
                if (provider.TryLoadObject(asset.PathWithoutExtension, out UStaticMesh sm)) ExportFromGameFiles(sm, _folderNames[4]);
                if (provider.TryLoadObject(asset.PathWithoutExtension, out USkeleton sk)) ExportFromGameFiles(sk, _folderNames[5]);
            }
        }
        else
        {
            List<FAssetData> assets = new();
            var assetArchive = await provider.TryCreateReaderAsync(Path.Join(provider.InternalGameName, "AssetRegistry.bin"));
            if (assetArchive is not null) assets.AddRange(new FAssetRegistryState(assetArchive).PreallocatedAssetDataBuffers);

            foreach (var asset in assets)
            {
                if (!asset.PackagePath.ToString().StartsWith("/Game")) continue;
                
                // SET THIS FOR ANY ADDITIVE ASSETS THAT ARE FAILING TO EXPORT DUE TO INCORRECT REF POSE
                if (asset.PackageName.ToString() == @"/Game/Enemies/HydraWeed/Assets/ANIM_HydraWeed_Heart_Damaged_Additive")
                {
                    ExportFromAssetData<UAnimSequence>(provider, _folderNames[0], asset, assets[assets.IndexOf(asset) + 1]);
                    continue;
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
        }

        return provider;
    }

    private static async void ExportFromGameFiles<T>(T asset, string outFolder) where T : UObject
    {
        try
        {
            var options = new ExporterOptions { ExportMaterials = _exportMaterials };
            var exporter = new Exporter(asset, options);
            if (!Directory.Exists(Path.Join(_outputDir, outFolder))) Directory.CreateDirectory(Path.Join(_outputDir, outFolder));
            exporter.TryWriteToDir(new DirectoryInfo(Path.Join(_outputDir, outFolder)), out _, out var fileName);
            var json = JsonConvert.SerializeObject(asset, Formatting.Indented);
            await File.WriteAllTextAsync(fileName.SubstringBefore(".") + ".json", json);
            if (_printSuccess) Console.WriteLine(fileName);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private static void ExportFromAssetData<T>(DefaultFileProvider provider, string outFolder,
        FAssetData asset, FAssetData? extraAsset = null) where T : UObject
    {
        var contentDir = _useInternalName ? Path.Join(provider.InternalGameName.ToUpper(), "Content") : "Game";
        var name = asset.AssetName.ToString();
        var dir = asset.PackagePath.ToString().Remove(0, 5);
        var jsonDir = Path.Join(_outputDir, outFolder, contentDir, dir);
        var path = Path.Join(contentDir, dir, name).Replace(Path.DirectorySeparatorChar, '/');

        if (File.Exists(Path.Join(_outputDir, outFolder, path) + ".json") && !_replaceFiles)
        {
            if (_printSuccess) Console.WriteLine("Skipping " + Path.Join(_outputDir, outFolder, path));
            return;
        }

        var objPath = asset.ObjectPath.Split("/");
        var objName = objPath[^1].Split(".");
        if (objPath.Length > 1 && !string.Equals(objName[0], objName[1], StringComparison.CurrentCultureIgnoreCase)) return;

        var refObject = provider.LoadObject<T>(path);
        if (refObject is UAnimSequence animSeq && animSeq.AdditiveAnimType != EAdditiveAnimationType.AAT_None)
        {
            animAdditives.Add(animSeq.GetPathName().Split(".")[0]);
        }

        if (extraAsset != null)
        {
            var tAnimSeqName = extraAsset.AssetName.ToString();
            var tAnimSeqDir = extraAsset.PackagePath.ToString().Remove(0, 5);
            var tPath = Path.Join(contentDir, tAnimSeqDir, tAnimSeqName).Replace(Path.DirectorySeparatorChar, '/');
            var addUAnimSequence = provider.LoadObject<UAnimSequence>(tPath);
            if (refObject is UAnimSequence refUAnimSequence)
            {
                refUAnimSequence.RefPoseSeq = new ResolvedLoadedObject(addUAnimSequence);
                refObject = refUAnimSequence as T;
            }
        }

        try
        {
            var json = JsonConvert.SerializeObject(refObject, Formatting.Indented);
            if (!Directory.Exists(jsonDir)) Directory.CreateDirectory(jsonDir);
            File.WriteAllText(Path.Join(_outputDir, outFolder, path) + ".json", json);
            if (_printSuccess) Console.WriteLine(Path.Join(_outputDir, outFolder, path) + ".json");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            Console.WriteLine(path);
        }

        try
        {
            var options = new ExporterOptions { ExportMaterials = _exportMaterials };
            var exporter = new Exporter(refObject!, options);
            exporter.TryWriteToDir(new DirectoryInfo(Path.Join(_outputDir, outFolder)), out _, out var fileName);
            if (_printSuccess) Console.WriteLine(fileName);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            Console.WriteLine(path);
        }
    }

    private static void CreateBlenderSKs(DefaultFileProvider provider, string outFolder)
    {
        var blenderDir = Path.Join(_outputDir, outFolder, "Blender");
        var contentDir = _useInternalName ? Path.Join(provider.InternalGameName.ToUpper(), "Content") : "Game";
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
        if (animAdditives.Count <= 0) return;
        var additiveFile = Path.Join(_outputDir, "additives.txt");
        if (!File.Exists(additiveFile)) File.Create(additiveFile).Close();
        File.WriteAllLines(additiveFile, animAdditives);
    }
}