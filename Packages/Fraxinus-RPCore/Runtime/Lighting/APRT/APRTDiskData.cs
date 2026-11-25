using System;
using System.IO;

namespace UnityEngine.Rendering
{
    [Flags]
    internal enum APRTStreamableDataFlags { None = 0, SkyIrradiance = 1, SkyBounce = 2, Scenario = 4 }

    [Serializable]
    internal sealed class APRTStreamableAsset
    {
        [SerializeField] internal string projectRelativePath;
        [SerializeField] internal string streamingRelativePath;
        [SerializeField] internal long byteCount;
        [SerializeField] internal uint checksum;
        [SerializeField] internal int cellIndex, probeCount, layoutHash;
        [SerializeField] internal APRTStreamableDataFlags flags;

        internal bool IsValid(int expectedCell, int expectedProbes, int expectedLayout, APRTStreamableDataFlags expectedFlags) =>
            cellIndex == expectedCell && probeCount == expectedProbes && probeCount > 0 && layoutHash == expectedLayout &&
            flags == expectedFlags && byteCount == APRTDiskData.ExpectedByteCount(probeCount, flags) &&
            SafeRelativePath(projectRelativePath) && SafeRelativePath(streamingRelativePath);

        static bool SafeRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) return false;
            foreach (var part in path.Replace('\\', '/').Split('/'))
                if (part == ".." || part == "." || part.Length == 0) return false;
            return true;
        }

        // Preserve the original external paths when loading legacy bakes.
        internal string ResolvePath()
        {
#if UNITY_EDITOR
            string path = Path.GetFullPath(projectRelativePath);
#else
            string path = Path.Combine(Application.streamingAssetsPath, streamingRelativePath);
#endif
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            // Unity's Windows Mono FileStream can reject an otherwise valid
            // deeply installed player path at MAX_PATH. Keep URI paths intact.
            if (path.Length >= 260 && path.IndexOf("://", StringComparison.Ordinal) < 0 &&
                !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                path = path.Replace('/', '\\');
                return path.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
            }
#endif
            return path;
        }

        internal APRTStreamableAsset Clone() => (APRTStreamableAsset)MemberwiseClone();
    }

    internal sealed class APRTScenarioCellData
    {
        internal Vector4[] directionalSH, skyIrradianceSH;
    }

    // Synchronous compatibility codec for external payloads from older bakes.
    // New bakes store transport and scenario arrays inline in the Baking Set.
    internal static class APRTDiskData
    {
        internal const string streamingAssetsDirectory = "APRTStreamingAssets";
        const int k_Magic = 0x41505254;
        const int k_Version = 1;
        const int k_HeaderSize = 6 * sizeof(int);

        internal static APRTStreamableDataFlags SharedFlags(bool sky, bool bounce) =>
            (sky ? APRTStreamableDataFlags.SkyIrradiance : 0) | (bounce ? APRTStreamableDataFlags.SkyBounce : 0);

        internal static long ExpectedByteCount(int probes, APRTStreamableDataFlags flags)
        {
            if (probes <= 0 || (flags & ~(APRTStreamableDataFlags.SkyIrradiance | APRTStreamableDataFlags.SkyBounce | APRTStreamableDataFlags.Scenario)) != 0 ||
                ((flags & APRTStreamableDataFlags.SkyBounce) != 0 && (flags & APRTStreamableDataFlags.SkyIrradiance) == 0)) return -1;
            if ((flags & APRTStreamableDataFlags.Scenario) != 0)
                return flags == APRTStreamableDataFlags.Scenario ? k_HeaderSize + (long)probes * 8 * 16 : -1;
            return k_HeaderSize + (long)probes * (6 * 32 + 4 +
                ((flags & APRTStreamableDataFlags.SkyIrradiance) != 0 ? 36 * 4 : 0) +
                ((flags & APRTStreamableDataFlags.SkyBounce) != 0 ? 36 * 12 : 0));
        }

        internal static APRTBakedCell ReadSharedSync(APRTBakedCell cell, int layoutHash, bool hasSky, bool hasBounce)
        {
            var descriptor = Validate(cell?.streamableAsset, cell?.index ?? -1, cell?.probeCount ?? 0, layoutHash, SharedFlags(hasSky, hasBounce));
            return DecodeShared(ReadSync(descriptor.ResolvePath(), descriptor), cell, descriptor);
        }

        internal static APRTScenarioCellData ReadScenarioSync(APRTLightingScenario scenario, int cellIndex, int probeCount, int layoutHash)
        {
            var descriptor = FindScenario(scenario, cellIndex, probeCount, layoutHash);
            return DecodeScenario(ReadSync(descriptor.ResolvePath(), descriptor), descriptor);
        }

        static APRTStreamableAsset FindScenario(APRTLightingScenario scenario, int cellIndex, int probeCount, int layoutHash)
        {
            if (scenario?.streamableAssets != null) foreach (var descriptor in scenario.streamableAssets)
                if (descriptor?.cellIndex == cellIndex)
                    return Validate(descriptor, cellIndex, probeCount, layoutHash, APRTStreamableDataFlags.Scenario);
            throw new InvalidDataException($"APRT scenario '{scenario?.name}' does not contain cell {cellIndex}.");
        }

        static APRTStreamableAsset Validate(APRTStreamableAsset descriptor, int cellIndex, int probeCount, int layoutHash, APRTStreamableDataFlags flags)
        {
            if (descriptor == null || !descriptor.IsValid(cellIndex, probeCount, layoutHash, flags) || descriptor.byteCount > int.MaxValue)
                throw new InvalidDataException("Invalid APRT streaming descriptor; bake the affected data again.");
            return descriptor.Clone();
        }

        static byte[] ReadSync(string path, APRTStreamableAsset descriptor)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != descriptor.byteCount) throw new InvalidDataException("APRT streaming file length does not match its bake.");
            var bytes = new byte[(int)descriptor.byteCount];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new EndOfStreamException("APRT streaming file was truncated.");
                offset += read;
            }
            ValidateChecksum(bytes, descriptor);
            return bytes;
        }

        internal static void ValidateFileSync(APRTStreamableAsset descriptor)
        {
            if (descriptor == null || !descriptor.IsValid(descriptor.cellIndex, descriptor.probeCount, descriptor.layoutHash, descriptor.flags))
                throw new InvalidDataException("Invalid APRT streaming descriptor.");
            var bytes = ReadSync(descriptor.ResolvePath(), descriptor);
            if (descriptor.flags == APRTStreamableDataFlags.Scenario)
                DecodeScenario(bytes, descriptor);
            else
                DecodeShared(bytes, new APRTBakedCell { index = descriptor.cellIndex, probeCount = descriptor.probeCount },
                    descriptor);
        }

        internal static uint ComputeChecksum(byte[] bytes)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < bytes.Length; ++i)
                    hash = (hash ^ bytes[i]) * 16777619;
                return hash;
            }
        }

        static void ValidateChecksum(byte[] bytes, APRTStreamableAsset descriptor)
        {
            if (ComputeChecksum(bytes) != descriptor.checksum)
                throw new InvalidDataException("APRT streaming file checksum does not match its bake.");
        }

        static BinaryReader Reader(byte[] bytes, APRTStreamableAsset descriptor)
        {
            var reader = new BinaryReader(new MemoryStream(bytes, false));
            if (reader.ReadInt32() != k_Magic || reader.ReadInt32() != k_Version ||
                reader.ReadInt32() != (int)descriptor.flags || reader.ReadInt32() != descriptor.layoutHash ||
                reader.ReadInt32() != descriptor.cellIndex || reader.ReadInt32() != descriptor.probeCount)
            { reader.Dispose(); throw new InvalidDataException("APRT streaming header does not match its cell or scenario."); }
            return reader;
        }

        static Vector4[] ReadVectors(BinaryReader reader, int count)
        {
            var values = new Vector4[count];
            for (int i = 0; i < count; ++i)
                values[i] = new Vector4(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));
            return values;
        }

        static float ReadFinite(BinaryReader reader)
        {
            float value = reader.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("APRT streaming payload contains a non-finite coefficient.");
            return value;
        }

        static APRTBakedCell DecodeShared(byte[] bytes, APRTBakedCell metadata, APRTStreamableAsset descriptor)
        {
            using var reader = Reader(bytes, descriptor);
            var cell = new APRTBakedCell
            {
                index = metadata.index, position = metadata.position, probeCount = metadata.probeCount,
                probePositions = metadata.probePositions, brickPositions = metadata.brickPositions,
                brickSubdivisions = metadata.brickSubdivisions, brickProbeIndices = metadata.brickProbeIndices,
                streamableAsset = descriptor,
                axisData = ReadVectors(reader, descriptor.probeCount * 6),
                visibilityMoments = ReadVectors(reader, descriptor.probeCount * 6),
                probeValidity = new float[descriptor.probeCount]
            };
            for (int i = 0; i < cell.probeValidity.Length; ++i)
            {
                float value = ReadFinite(reader);
                if (value < 0 || value > 1) throw new InvalidDataException("Invalid APRT streamed probe validity.");
                cell.probeValidity[i] = value;
            }
            for (int i = 0; i < cell.axisData.Length; ++i)
            {
                var axis = cell.axisData[i]; var moment = cell.visibilityMoments[i];
                if (axis.x < 0 || axis.y < 0 || axis.z < 0 || axis.w < 0 || axis.w > 1 || moment.x < 0 || moment.y < 0)
                    throw new InvalidDataException("Invalid APRT streamed visibility or surface transfer.");
            }
            if ((descriptor.flags & APRTStreamableDataFlags.SkyIrradiance) != 0)
            {
                cell.skyIrradianceTransfer = new float[descriptor.probeCount * 36];
                for (int i = 0; i < cell.skyIrradianceTransfer.Length; ++i)
                    cell.skyIrradianceTransfer[i] = ReadFinite(reader);
            }
            if ((descriptor.flags & APRTStreamableDataFlags.SkyBounce) != 0)
            {
                cell.skyIrradianceTransferRGB = new Vector3[descriptor.probeCount * 36];
                for (int i = 0; i < cell.skyIrradianceTransferRGB.Length; ++i)
                    cell.skyIrradianceTransferRGB[i] = new Vector3(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));
            }
            return cell;
        }

        static APRTScenarioCellData DecodeScenario(byte[] bytes, APRTStreamableAsset descriptor)
        {
            using var reader = Reader(bytes, descriptor);
            return new APRTScenarioCellData
            {
                directionalSH = ReadVectors(reader, descriptor.probeCount * 4),
                skyIrradianceSH = ReadVectors(reader, descriptor.probeCount * 4)
            };
        }

#if UNITY_EDITOR
        static void Header(BinaryWriter writer, int cellIndex, int probeCount, int layoutHash, APRTStreamableDataFlags flags)
        {
            writer.Write(k_Magic); writer.Write(k_Version); writer.Write((int)flags);
            writer.Write(layoutHash); writer.Write(cellIndex); writer.Write(probeCount);
        }

        static void WriteVectors(BinaryWriter writer, Vector4[] values, int offset, int count)
        {
            for (int i = offset; i < offset + count; ++i)
            { writer.Write(values[i].x); writer.Write(values[i].y); writer.Write(values[i].z); writer.Write(values[i].w); }
        }

        internal static byte[] EncodeShared(APRTBakedCell cell, int layoutHash, bool sky, bool bounce)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            Header(writer, cell.index, cell.probeCount, layoutHash, SharedFlags(sky, bounce));
            WriteVectors(writer, cell.axisData, 0, cell.probeCount * 6);
            WriteVectors(writer, cell.visibilityMoments, 0, cell.probeCount * 6);
            foreach (float value in cell.probeValidity) writer.Write(value);
            if (sky) foreach (float value in cell.skyIrradianceTransfer) writer.Write(value);
            if (bounce) foreach (var value in cell.skyIrradianceTransferRGB)
            { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
            writer.Flush(); return stream.ToArray();
        }

        internal static byte[] EncodeScenario(APRTLightingScenario scenario, APRTBakedCell cell, int probeOffset, int layoutHash)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            Header(writer, cell.index, cell.probeCount, layoutHash, APRTStreamableDataFlags.Scenario);
            WriteVectors(writer, scenario.bakedDirectionalSH, probeOffset * 4, cell.probeCount * 4);
            WriteVectors(writer, scenario.bakedSkyIrradianceSH, probeOffset * 4, cell.probeCount * 4);
            writer.Flush(); return stream.ToArray();
        }
#endif
    }
}
