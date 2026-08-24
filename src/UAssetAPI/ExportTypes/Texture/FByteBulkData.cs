using System;
using System.IO;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.ExportTypes.Texture
{
    /// <summary>
    /// Bulk data container for texture mipmap data.
    /// Ported from CUE4Parse with write support added.
    /// </summary>
    public class FByteBulkData
    {
        public FByteBulkDataHeader Header;
        public byte[] Data;

        /// <summary>
        /// Path to external bulk file (.ubulk) if data is stored externally.
        /// </summary>
        public string ExternalBulkFilePath;

        public FByteBulkData()
        {
            Header = new FByteBulkDataHeader();
            Data = Array.Empty<byte>();
        }

        public FByteBulkData(byte[] data)
        {
            Header = new FByteBulkDataHeader();
            Data = data ?? Array.Empty<byte>();
            Header.ElementCount = Data.Length;
            Header.SizeOnDisk = Data.Length;
            Header.BulkDataFlags = EBulkDataFlags.BULKDATA_ForceInlinePayload;
        }

        public FByteBulkData(AssetBinaryReader reader, string bulkFilePath = null)
        {
            ExternalBulkFilePath = bulkFilePath;
            Read(reader);
        }

        public void Read(AssetBinaryReader reader)
        {
            Header = new FByteBulkDataHeader(reader);

            if (Header.ElementCount == 0 || Header.BulkDataFlags.HasFlag(EBulkDataFlags.BULKDATA_Unused))
            {
                Data = Array.Empty<byte>();
                return;
            }

            // UE5.3+ DataResources: the header only consumed a 4-byte DataResourceIndex.
            // In this format the payload is interleaved per mip and cannot be read here,
            // because the caller must decide based on the resolved bulk flags. See
            // ReadDataResourcePayload, which FTexture2DMipMap.Read calls immediately after.
            if (Header.DataResourceIndex >= 0)
            {
                Data = Array.Empty<byte>();
                return;
            }

            // Legacy format: Check if data is inline by ForceInlinePayload flag
            bool isInline = Header.BulkDataFlags.HasFlag(EBulkDataFlags.BULKDATA_ForceInlinePayload);

            if (isInline)
            {
                // Data is inline - read it directly from current position
                if (Header.ElementCount > 0 && Header.ElementCount < int.MaxValue)
                {
                    Data = reader.ReadBytes((int)Header.ElementCount);
                }
                else
                {
                    Data = Array.Empty<byte>();
                }
            }
            else if (!string.IsNullOrEmpty(ExternalBulkFilePath) && File.Exists(ExternalBulkFilePath))
            {
                // Data is in .ubulk file - read from the offset
                try
                {
                    using (var bulkReader = new BinaryReader(File.OpenRead(ExternalBulkFilePath)))
                    {
                        bulkReader.BaseStream.Seek(Header.OffsetInFile, SeekOrigin.Begin);
                        Data = bulkReader.ReadBytes((int)Header.ElementCount);
                    }
                }
                catch
                {
                    // Failed to read from ubulk - store empty
                    Data = Array.Empty<byte>();
                }
            }
            else
            {
                // External data but no ubulk file - store empty
                Data = Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Read the inline pixel payload for the UE5.3+ DataResources format.
        /// Must be called straight after <see cref="Read"/> consumed the 4-byte
        /// DataResourceIndex, with the stream sitting on the payload.
        ///
        /// Only inline mips carry their bytes here; streaming (.ubulk) and optional (.uptnl)
        /// mips keep their payload in the sidecar file and are deliberately left with empty
        /// Data, matching what the writer and TextureInjector expect. The DataResource entry
        /// for those mips already points at the correct offset inside its own file.
        /// </summary>
        public void ReadDataResourcePayload(AssetBinaryReader reader)
        {
            if (Header == null || Header.DataResourceIndex < 0) return;
            if (!Header.IsInline) return;

            long size = Header.ElementCount;
            if (size <= 0 || size > int.MaxValue) return;

            Data = reader.ReadBytes((int)size);
        }

        public void Write(AssetBinaryWriter writer)
        {
            // Update header with current data size
            Header.ElementCount = Data?.Length ?? 0;
            Header.SizeOnDisk = Header.ElementCount;

            Header.Write(writer);

            // For UE5.3+ with DataResourceIndex, the pixel data is written separately
            // at the end of the mip array, not inline with each mip's header.
            // The DataResource's SerialOffset points to where the data is.
            // Only write inline data here for legacy format (no DataResourceIndex)
            if (Header.DataResourceIndex < 0 && Header.IsInline && Data != null && Data.Length > 0)
            {
                writer.Write(Data);
            }
        }

        /// <summary>
        /// Write just the pixel data (for UE5.3+ format where data comes after all mip headers)
        /// </summary>
        public void WriteData(AssetBinaryWriter writer)
        {
            if (Data != null && Data.Length > 0)
            {
                writer.Write(Data);
            }
        }

        /// <summary>
        /// Convert bulk data to inline format (for mipmap stripping).
        /// This removes external file references and embeds data directly.
        /// </summary>
        public void ConvertToInline()
        {
            // Clear external file flags
            Header.BulkDataFlags &= ~EBulkDataFlags.BULKDATA_PayloadInSeperateFile;
            Header.BulkDataFlags &= ~EBulkDataFlags.BULKDATA_PayloadAtEndOfFile;
            Header.BulkDataFlags &= ~EBulkDataFlags.BULKDATA_OptionalPayload;
            
            // Set inline flag
            Header.BulkDataFlags |= EBulkDataFlags.BULKDATA_ForceInlinePayload;
            
            // Clear offset since data is now inline
            Header.OffsetInFile = 0;
            Header.CookedIndex = -1;
            
            // Keep DataResourceIndex for UE5.3+ - the DataResource will be updated with correct offset
            // Don't clear it here as it's needed for the Write to work correctly
        }

        /// <summary>
        /// Check if this bulk data has actual pixel data.
        /// </summary>
        public bool HasData => Data != null && Data.Length > 0;
    }
}
