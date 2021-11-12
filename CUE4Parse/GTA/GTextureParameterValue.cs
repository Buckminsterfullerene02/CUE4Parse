﻿using CUE4Parse.UE4;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;

namespace CUE4Parse.GTA
{
    public struct GTextureParameterValue : IUStruct
    {
        public FMaterialParameterInfo ParameterInfo;
        public FPackageIndex ParameterValue;
        public FGuid ExpressionGUID;

        public GTextureParameterValue(FAssetArchive Ar)
        {
            ParameterInfo = new FMaterialParameterInfo(Ar);
            ParameterValue = new FPackageIndex(Ar);
            ExpressionGUID = Ar.Read<FGuid>();
        }
    }
}
