using System;
using System.Collections.Generic;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.ExportTypes.Texture
{
    /// <summary>
    /// Texture platform data containing mipmaps and format information.
    /// Ported from CUE4Parse with write support added.
    /// </summary>
    public class FTexturePlatformData
    {
        private const uint BitMask_CubeMap = 1u << 31;
        private const uint BitMask_HasOptData = 1u << 30;
        private const uint BitMask_HasCpuCopy = 1u << 29;
        private const uint BitMask_NumSlices = BitMask_HasOptData - 1u;

        public int SizeX;
        public int SizeY;
        public uint PackedData;
        public string PixelFormat;
        public FOptTexturePlatformData OptData;
        public int FirstMipToSerialize;
        public List<FTexture2DMipMap> Mips;

        /// <summary>
        /// Path to external .ubulk file for loading mipmap data.
        /// </summary>
        public string BulkFilePath;

        /// <summary>
        /// Placeholder bytes for UE5.0+ cooked assets (16 bytes).
        /// </summary>
        public byte[] PlaceholderBytes;

        public FTexturePlatformData()
        {
            SizeX = 0;
            SizeY = 0;
            PackedData = 1; // Default to 1 slice
            PixelFormat = string.Empty;
            OptData = new FOptTexturePlatformData();
            FirstMipToSerialize = 0;
            Mips = new List<FTexture2DMipMap>();
        }

        public FTexturePlatformData(AssetBinaryReader reader, string bulkFilePath = null, bool bSerializeMipData = true, bool isUE5Cooked = false)
        {
            BulkFilePath = bulkFilePath;
            Read(reader, bSerializeMipData, isUE5Cooked);
        }

        public void Read(AssetBinaryReader reader, bool bSerializeMipData = true, bool isUE5Cooked = false)
        {
            // FTexturePlatformData::SerializeCooked — CUE4Parse approach
            // UE5.2+: 1 byte bUsingDerivedData flag + 15 bytes placeholder = 16 total
            // UE5.0-5.1 (IsFilterEditorOnly): 16 bytes flat
            PlaceholderByteCount = 0;
            if (isUE5Cooked)
            {
                const int PlaceholderDerivedDataSize = 16;
                PlaceholderByteCount = PlaceholderDerivedDataSize;
                PlaceholderBytes = reader.ReadBytes(PlaceholderDerivedDataSize);
            }
            
            // Read dimensions and packed data
            SizeX = reader.ReadInt32();
            SizeY = reader.ReadInt32();
            PackedData = reader.ReadUInt32();

            // Read pixel format as FString
            PixelFormat = reader.ReadFString()?.Value ?? string.Empty;

            // Optional texture platform data (if HasOptData flag is set in PackedData)
            if (HasOptData())
            {
                OptData = new FOptTexturePlatformData();
                OptData.ExtData = reader.ReadUInt32();
                OptData.NumMipsInTail = reader.ReadUInt32();
            }

            // First mip to serialize (cooked assets only)
            FirstMipToSerialize = reader.ReadInt32();

            // Read mipmap count and mipmaps
            int mipCount = reader.ReadInt32();
            
            if (mipCount < 0 || mipCount > 20)
            {
                throw new InvalidOperationException($"Invalid mip count: {mipCount}. Parsing error at position {reader.BaseStream.Position}");
            }
            
            Mips = new List<FTexture2DMipMap>(mipCount);

            for (int i = 0; i < mipCount; i++)
            {
                var mip = new FTexture2DMipMap(reader, BulkFilePath, bSerializeMipData);
                Mips.Add(mip);
            }

            // UE5.3+ DataResources mips carry their real dimensions inline (read by
            // FTexture2DMipMap.Read). Previously they were synthesized as SizeX >> mipIndex
            // because the mip loop left the stream misaligned and never parsed them; that
            // guess also desynced everything after it, including bIsVirtual below.
            bool hasDataResources = Mips.Count > 0 && Mips[0].BulkData?.Header?.DataResourceIndex >= 0;

            // bIsVirtual (int32 as bool) comes right after mip headers
            bIsVirtual = reader.ReadInt32() != 0;

            // Update dimensions from first mip if available (CUE4Parse does this).
            // NOT valid in the DataResources format: when FirstMipToSerialize > 0 the top
            // mip levels were dropped at cook time, so Mips[0] is smaller than the texture.
            // (e.g. a 2048x2048 BC5 whose first serialized mip is 1024x1024). Overwriting
            // the header with Mips[0] there would shrink the texture on the next write.
            if (Mips.Count > 0 && !hasDataResources)
            {
                SizeX = Mips[0].SizeX;
                SizeY = Mips[0].SizeY;
            }
        }
        
        /// <summary>
        /// Whether this is a virtual texture (UE4.23+).
        /// </summary>
        public bool bIsVirtual;

        /// <summary>
        /// Number of placeholder bytes to write before the actual data (for UE5 cooked assets).
        /// CUE4Parse uses 16 bytes for UE5.0+.
        /// </summary>
        public int PlaceholderByteCount = 16;

        public void Write(AssetBinaryWriter writer)
        {
            // Write placeholder bytes for UE5.0+ cooked assets (16 bytes)
            if (PlaceholderBytes != null && PlaceholderBytes.Length > 0)
            {
                writer.Write(PlaceholderBytes);
            }
            else if (PlaceholderByteCount > 0)
            {
                writer.Write(new byte[PlaceholderByteCount]);
            }
            
            // Write dimensions and packed data.
            // In the DataResources format these are the texture's own dimensions, which differ
            // from Mips[0] whenever FirstMipToSerialize > 0 — see the matching note in Read.
            bool writeHasDataResources = Mips.Count > 0 && Mips[0].BulkData?.Header?.DataResourceIndex >= 0;
            int writeX = (Mips.Count > 0 && !writeHasDataResources) ? Mips[0].SizeX : SizeX;
            int writeY = (Mips.Count > 0 && !writeHasDataResources) ? Mips[0].SizeY : SizeY;
            writer.Write(writeX);
            writer.Write(writeY);
            writer.Write(PackedData);

            // Write pixel format
            writer.Write(new FString(PixelFormat));

            // Optional texture platform data
            if (HasOptData())
            {
                writer.Write(OptData.ExtData);
                writer.Write(OptData.NumMipsInTail);
            }

            // First mip to serialize
            writer.Write(FirstMipToSerialize);

            // Write mipmap count
            writer.Write(Mips.Count);

            // Check if using UE5.3+ DataResource format
            bool hasDataResources = Mips.Count > 0 && Mips[0].BulkData?.Header?.DataResourceIndex >= 0;

            if (hasDataResources)
            {
                // UE5.3+ cooked format is INTERLEAVED per mip:
                //   [DataResourceIndex(4)] [inline pixel data, if any] [SizeX(4)] [SizeY(4)] [SizeZ(4)]
                // Streaming/optional mips have no inline data (their pixels live in .ubulk/.uptnl), so only
                // the index + dims are written for them. bIsVirtual follows the last mip.
                foreach (var mip in Mips)
                {
                    mip.BulkData.Write(writer); // writes the 4-byte DataResourceIndex
                    if (mip.BulkData?.Data != null && mip.BulkData.Data.Length > 0)
                        mip.BulkData.WriteData(writer);
                    writer.Write(mip.SizeX);
                    writer.Write(mip.SizeY);
                    writer.Write(mip.SizeZ);
                }
                writer.Write(bIsVirtual ? 1 : 0);
            }
            else
            {
                // Legacy format: all mip headers, then bIsVirtual, then inline pixel data.
                foreach (var mip in Mips)
                {
                    mip.Write(writer);
                }
                writer.Write(bIsVirtual ? 1 : 0);
                foreach (var mip in Mips)
                {
                    if (mip.BulkData?.Data != null && mip.BulkData.Data.Length > 0 && mip.BulkData.Header?.IsInline == true)
                    {
                        mip.BulkData.WriteData(writer);
                    }
                }
            }
        }

        public bool HasCpuCopy() => (PackedData & BitMask_HasCpuCopy) == BitMask_HasCpuCopy;
        public bool HasOptData() => (PackedData & BitMask_HasOptData) == BitMask_HasOptData;
        public bool IsCubemap() => (PackedData & BitMask_CubeMap) == BitMask_CubeMap;
        public int GetNumSlices() => (int)(PackedData & BitMask_NumSlices);

        /// <summary>
        /// Strip all mipmaps except the first one and convert to inline storage.
        /// This is used for texture mods to eliminate .ubulk dependencies.
        /// </summary>
        /// <returns>True if mipmaps were stripped, false if already had 1 or 0 mipmaps.</returns>
        public bool StripMipmaps()
        {
            if (Mips.Count <= 1)
            {
                return false;
            }

            // Keep only the first (largest) mipmap
            var firstMip = Mips[0];
            
            // Convert to inline storage
            firstMip.ConvertToInline();

            // Clear the mip list and add only the first mip
            Mips.Clear();
            Mips.Add(firstMip);

            // Update FirstMipToSerialize - should be 0 for single mip textures
            // This tells the engine that mip 0 is the first one to serialize (the only one we have)
            FirstMipToSerialize = 0;

            return true;
        }

        /// <summary>
        /// Get total size of all mipmap data.
        /// </summary>
        public long GetTotalMipDataSize()
        {
            long total = 0;
            foreach (var mip in Mips)
            {
                total += mip.BulkData?.Data?.Length ?? 0;
            }
            return total;
        }
    }

    /// <summary>
    /// Optional texture platform data for UE5+.
    /// </summary>
    public struct FOptTexturePlatformData
    {
        public uint ExtData;
        public uint NumMipsInTail;
    }
}
