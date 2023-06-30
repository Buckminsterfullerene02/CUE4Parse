using CUE4Parse_Conversion;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

public class Progam
{
    // SET THESE VARIABLES
    const string PAK_DIR = @"C:\Program Files (x86)\Steam\steamapps\common\Deep Rock Galactic\FSD\Content\Paks";
    const string INTERNAL_NAME = "FSD";
    const string OUTPUT_DIR = @"F:\DRG Modding\DRGPacker\JSON\Animation Stuff\Temp\";
    const bool PRINT_SUCCESS = true;

    static async Task Main(string[] args)
    {
        var provider = new DefaultFileProvider(PAK_DIR, SearchOption.AllDirectories, true, 
            new VersionContainer(EGame.GAME_UE4_27));
        provider.Initialize();
        await provider.MountAsync();

        List<FAssetData> assets = new();
        var assetArchive = await provider.TryCreateReaderAsync(Path.Join(INTERNAL_NAME, "AssetRegistry.bin"));
        if (assetArchive is not null) assets.AddRange(new FAssetRegistryState(assetArchive).PreallocatedAssetDataBuffers);

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
                    Export<USkeleton>(provider, "SKs",asset);
                    break;
                case "AnimSequence":
                    Export<UAnimSequence>(provider, "AnimSeqs",asset);
                    break;
                case "AnimMontage":
                    Export<UAnimMontage>(provider, "AnimMonts",asset);
                    break;
                case "AnimComposite":
                    Export<UAnimComposite>(provider, "AnimComps",asset);
                    break;
                case "SkeletalMesh":
                    Export<USkeletalMesh>(provider, "SKMs",asset);
                    break;
                case "StaticMesh":
                    Export<UStaticMesh>(provider, "SMs",asset);
                    break;
            }
        }
        
        CreateBlenderSKs("SKs");
    }
    
    private static void Export<T>(DefaultFileProvider provider, string outFolder, 
        FAssetData asset, FAssetData? extraAsset = null) where T : UObject
    {
        var contentDir = Path.Join(INTERNAL_NAME, "Content");
        var name = asset.AssetName.ToString();
        var dir = asset.PackagePath.ToString().Remove(0, 5);
        var jsonDir = Path.Join(OUTPUT_DIR, outFolder, contentDir, dir);
        var path = Path.Join(contentDir, dir, name).Replace(Path.DirectorySeparatorChar, '/');
        var refObject = provider.LoadObject<T>(path);
        
        if (extraAsset != null)
        {
            var tAnimSeqName = extraAsset.AssetName.ToString();
            var tAnimSeqDir = extraAsset.PackagePath.ToString().Remove(0, 5);;
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
            File.WriteAllText(Path.Join(OUTPUT_DIR, outFolder, path) + ".json", json);
            if (PRINT_SUCCESS) Console.WriteLine(Path.Join(OUTPUT_DIR, outFolder, path) + ".json");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            Console.WriteLine(path);
        }
        
        try
        {
            var options = new ExporterOptions() { ExportMaterials = false };
            var exporter = new Exporter(refObject, options);   
            exporter.TryWriteToDir(new DirectoryInfo(Path.Join(OUTPUT_DIR, outFolder)), out _, out var fileName);
            if (PRINT_SUCCESS) Console.WriteLine(fileName);
        } 
        catch (Exception e)
        {
            Console.WriteLine(e);
            Console.WriteLine(path);
        }
    }

    private static void CreateBlenderSKs(string outFolder)
    {
        var blenderDir = Path.Join(OUTPUT_DIR, outFolder, "Blender");
        if (!Directory.Exists(blenderDir)) Directory.CreateDirectory(blenderDir);
        foreach (var file in Directory.GetFiles(Path.Join(OUTPUT_DIR, outFolder, INTERNAL_NAME, "Content"), 
                     "*.*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) == ".json") continue;
            File.Copy(file, Path.Join(blenderDir, Path.GetFileName(file)), true);
        }
    }
}