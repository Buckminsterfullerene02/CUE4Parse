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
using Newtonsoft.Json;

public class Progam
{
    // SET THESE FOR YOUR GAME

    private const string _pakDir = @"C:\Program Files (x86)\Steam\steamapps\common\Deep Rock Galactic\FSD\Content\Paks";
    private const string _aesKey = ""; // If your game does not have an AES key, leave this empty
    private const string _mapping = ""; // If your game does not need a mappings file, leave this empty
    private const EGame  _version = EGame.GAME_UE4_27;
    private const bool   _exportMaterials = false; // This needs to be false if generating for CAS+UEAT

    private const bool   _useInternalName = true; // Sometimes package path is not set properly meaning paths are not synced, so if it isn't, set to true 
    private const string _outputDir = @"F:\DRG Modding\DRGPacker\JSON\Animation Stuff\";
    private const bool   _replaceFiles = false;
    private const bool   _printSuccess = true; // Once you've verified this works, set this to false to reduce console spam

    /*
    private const string _pakDir = @"D:\STEAM GAMES\steamapps\common\AliensDarkDescent\ASF\Content\Paks";
    private const string _aesKey = ""; // If your game does not have an AES key, leave this empty
    private const string _mapping = ""; // If your game does not need a mappings file, leave this empty
    private const EGame  _version = EGame.GAME_UE4_27;
    private const bool   _exportMaterials = false;
    
    private const bool   _useInternalName = false; // Sometimes package path is not set properly meaning paths are not synced, so if it isn't, set to true  
    private const string _outputDir = @"F:\Other Modding\AliensDD\FBX\";
    private const bool   _printSuccess = true; // Once you've verified this works, set this to false to reduce console spam
    */

    private static List<string> animAdditives = new();

    static async Task Main(string[] args)
    {
        var provider = new DefaultFileProvider(_pakDir, SearchOption.TopDirectoryOnly, true,
            new VersionContainer(_version));
        if (!string.IsNullOrEmpty(_mapping)) provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_mapping);
        provider.Initialize();
        if (!string.IsNullOrEmpty(_aesKey)) await provider.SubmitKeyAsync(new FGuid(), new FAesKey(_aesKey));
        await provider.MountAsync();

        List<FAssetData> assets = new();
        var assetArchive =
            await provider.TryCreateReaderAsync(Path.Join(provider.InternalGameName, "AssetRegistry.bin"));
        if (assetArchive is not null)
            assets.AddRange(new FAssetRegistryState(assetArchive).PreallocatedAssetDataBuffers);

        foreach (var asset in assets)
        {
            if (!asset.PackagePath.ToString().StartsWith("/Game")) continue;

            // SET THIS FOR ANY ADDITIVE ASSETS THAT ARE FAILING TO EXPORT DUE TO INCORRECT REF POSE
            if (asset.PackageName.ToString() == @"/Game/Enemies/HydraWeed/Assets/ANIM_HydraWeed_Heart_Damaged_Additive")
            {
                Export<UAnimSequence>(provider, "AnimSeqs", asset, assets[assets.IndexOf(asset) + 1]);
                continue;
            }

            switch (asset.AssetClass.Text)
            {
                case "Skeleton":
                    Export<USkeleton>(provider, "SKs", asset);
                    break;
                case "AnimSequence":
                    Export<UAnimSequence>(provider, "AnimSeqs", asset);
                    break;
                case "AnimMontage":
                    Export<UAnimMontage>(provider, "AnimMonts", asset);
                    break;
                case "AnimComposite":
                    Export<UAnimComposite>(provider, "AnimComps", asset);
                    break;
                case "SkeletalMesh":
                    Export<USkeletalMesh>(provider, "SKMs", asset);
                    break;
                case "StaticMesh":
                    Export<UStaticMesh>(provider, "SMs", asset);
                    break;
            }
        }

        CreateBlenderSKs(provider, "SKs");

        PrintAnimAdditives();
    }

    private static void Export<T>(DefaultFileProvider provider, string outFolder,
        FAssetData asset, FAssetData? extraAsset = null) where T : UObject
    {
        var contentDir = _useInternalName ? Path.Join(provider.InternalGameName.ToUpper(), "Content") : "Game";
        var name = asset.AssetName.ToString();
        var dir = asset.PackagePath.ToString().Remove(0, 5);
        var jsonDir = Path.Join(_outputDir, outFolder, contentDir, dir);
        var path = Path.Join(contentDir, dir, name).Replace(Path.DirectorySeparatorChar, '/');

        if (File.Exists(Path.Join(_outputDir, outFolder, path) + ".json") && !_replaceFiles)
        {
            Console.WriteLine("Skipping " + Path.Join(_outputDir, outFolder, path));
            return;
        }

        var objPath = asset.ObjectPath.Split("/");
        var objName = objPath[^1].Split(".");
        if (objPath.Length > 1 && objName[0] != objName[1]) return;

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
            var refUAnimSequence = refObject as UAnimSequence;
            //refUAnimSequence.RefPoseSeq = addUAnimSequence.RefPoseSeq;
            refUAnimSequence.RefPoseSeq = new ResolvedLoadedObject(addUAnimSequence);
            refObject = refUAnimSequence as T;
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
            var options = new ExporterOptions() { ExportMaterials = _exportMaterials };
            var exporter = new Exporter(refObject, options);
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