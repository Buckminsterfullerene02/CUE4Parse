﻿using CUE4Parse.ACL;
using CUE4Parse.UE4.Assets.Readers;

namespace CUE4Parse.UE4.Assets.Exports.Animation.ACL
{
    public class FACLDatabaseCompressedAnimData : ICompressedAnimData
    {
        public int CompressedNumberOfFrames { get; set; }

        /** Maps the compressed_tracks instance. Used in cooked build only. */
        public byte[] CompressedByteStream;
        public int CompressedByteStreamNum;

        /** The sequence name hash that owns this data. */
        public uint SequenceNameHash;

        /*/** Holds the compressed_tracks instance for the anim sequence #1#
        public byte[] CompressedClip;*/

        public void SerializeCompressedData(FAssetArchive Ar)
        {
            FCompressedAnimDataBase.BaseSerializeCompressedData(this, Ar);

            SequenceNameHash = Ar.Read<uint>();

            /*if (!Ar.Owner.HasFlags(EPackageFlags.PKG_FilterEditorOnly))
            {
                CompressedClip = Ar.ReadArray<byte>();
            }*/
        }

        public void Bind(byte[] bulkData)
        {
            var compressedClipData = new CompressedTracks(bulkData);
        }
    }
}