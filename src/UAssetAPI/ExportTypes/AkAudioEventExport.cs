using System;
using System.IO;
using Newtonsoft.Json;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.ExportTypes
{
    /// <summary>
    /// A Wwise event asset (UAkAudioEvent) from the Wwise UE integration (2022.1 and later).
    ///
    /// UAkAudioEvent::Serialize writes the object's tagged properties (usually none in a cooked
    /// asset) and then, natively, the event's cooked data: the struct
    /// <c>FWwiseLocalizedEventCookedData</c> serialised like any other struct (unversioned in a
    /// cooked game, so it needs mappings), followed by four plain fields. Marvel Rivals' engine
    /// adds three more after those (MaxAttenuationRadiusP2, MaxAttenuationRadiusP3 and
    /// bHasMotionEvent; see the game's SDK dump, AkAudio_classes.hpp).
    ///
    /// Without this type all of that sat in <see cref="NormalExport.Extras"/> as an opaque blob,
    /// so an event's id, its banks or its media could only be changed by patching bytes.
    ///
    /// The parse is accepted only if writing it back reproduces the original bytes exactly;
    /// otherwise (no mappings, an older integration, an unknown layout) the bytes stay in
    /// Extras untouched, which is what a NormalExport would have done. Anything after the
    /// fields this type knows also stays in Extras, so reading is never lossy.
    /// </summary>
    public class AkAudioEventExport : NormalExport
    {
        /// <summary>FWwiseLocalizedEventCookedData: EventLanguageMap, DebugName, EventId. Null when not parsed.</summary>
        [JsonProperty]
        public StructPropertyData EventCookedData;

        /// <summary>Whether the four integration fields below were present and parsed.</summary>
        [JsonProperty]
        public bool HasDurationFields;
        [JsonProperty]
        public float MaximumDuration;
        [JsonProperty]
        public float MinimumDuration;
        [JsonProperty]
        public bool IsInfinite;
        [JsonProperty]
        public float MaxAttenuationRadius;

        /// <summary>Whether the three Marvel Rivals (NetEase) fields below were present and parsed.</summary>
        [JsonProperty]
        public bool HasMarvelFields;
        [JsonProperty]
        public float MaxAttenuationRadiusP2;
        [JsonProperty]
        public float MaxAttenuationRadiusP3;
        [JsonProperty]
        public bool bHasMotionEvent;

        public AkAudioEventExport(Export super) : base(super)
        {
        }

        public AkAudioEventExport(UAsset asset, byte[] extras) : base(asset, extras)
        {
        }

        public AkAudioEventExport()
        {
        }

        public override void Read(AssetBinaryReader reader, int nextStarting)
        {
            base.Read(reader, nextStarting);

            EventCookedData = null;
            HasDurationFields = HasMarvelFields = false;
            if (ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject)) return;
            if (reader.Asset.HasUnversionedProperties && reader.Asset.Mappings == null) return;

            long start = reader.BaseStream.Position;
            long end = nextStarting > 0 ? nextStarting : reader.BaseStream.Length;
            if (end <= start) return;
            byte[] original = reader.ReadBytes((int)(end - start));

            try
            {
                using var ms = new MemoryStream(original);
                using var r = new AssetBinaryReader(ms, reader.Asset);
                var cooked = new StructPropertyData(FName.DefineDummy(reader.Asset, "EventCookedData"))
                {
                    StructType = FName.DefineDummy(reader.Asset, "WwiseLocalizedEventCookedData")
                };
                // A non-zero length tells StructPropertyData there is a body to read.
                cooked.Read(r, false, 1, 0, PropertySerializationContext.Normal);
                EventCookedData = cooked;

                if (original.Length - r.BaseStream.Position >= 16)
                {
                    MaximumDuration = r.ReadSingle();
                    MinimumDuration = r.ReadSingle();
                    IsInfinite = r.ReadInt32() != 0;
                    MaxAttenuationRadius = r.ReadSingle();
                    HasDurationFields = true;

                    if (original.Length - r.BaseStream.Position >= 12)
                    {
                        MaxAttenuationRadiusP2 = r.ReadSingle();
                        MaxAttenuationRadiusP3 = r.ReadSingle();
                        bHasMotionEvent = r.ReadInt32() != 0;
                        HasMarvelFields = true;
                    }
                }

                // Accept the parse only if it writes back to exactly the bytes it consumed.
                long consumed = r.BaseStream.Position;
                byte[] again = WriteCooked(reader.Asset);
                if (again.Length != consumed || !again.AsSpan().SequenceEqual(original.AsSpan(0, (int)consumed)))
                    throw new FormatException("AkAudioEvent cooked data does not round-trip");

                reader.BaseStream.Position = start + consumed;
            }
            catch
            {
                EventCookedData = null;
                HasDurationFields = HasMarvelFields = false;
                reader.BaseStream.Position = start;   // everything after the properties stays in Extras
            }
        }

        private byte[] WriteCooked(UAsset asset)
        {
            using var ms = new MemoryStream();
            using var w = new AssetBinaryWriter(ms, asset);
            WriteCooked(w);
            w.Flush();
            return ms.ToArray();
        }

        private void WriteCooked(AssetBinaryWriter writer)
        {
            if (EventCookedData == null) return;
            EventCookedData.Write(writer, false);
            if (!HasDurationFields) return;
            writer.Write(MaximumDuration);
            writer.Write(MinimumDuration);
            writer.Write(IsInfinite ? 1 : 0);
            writer.Write(MaxAttenuationRadius);
            if (!HasMarvelFields) return;
            writer.Write(MaxAttenuationRadiusP2);
            writer.Write(MaxAttenuationRadiusP3);
            writer.Write(bHasMotionEvent ? 1 : 0);
        }

        public override void ResolveAncestries(UAsset asset, AncestryInfo ancestrySoFar)
        {
            var ancestryNew = (AncestryInfo)ancestrySoFar.Clone();
            ancestryNew.SetAsParent(EventCookedData?.StructType ?? FName.DefineDummy(asset, "WwiseLocalizedEventCookedData"));
            EventCookedData?.ResolveAncestries(asset, ancestryNew);
            base.ResolveAncestries(asset, ancestrySoFar);
        }

        public override void Write(AssetBinaryWriter writer)
        {
            base.Write(writer);
            if (ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject)) return;
            WriteCooked(writer);
        }
    }
}
