using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OplusEdlTool.Services
{
    public class LpMetadataGeometry
    {
        public uint Magic;
        public uint StructSize;
        public uint MetadataMaxSize;
        public uint MetadataSlotCount;
        public uint LogicalBlockSize;
    }

    public class LpMetadataExtent
    {
        public ulong NumSectors512;
        public uint TargetType;      // 0 = LINEAR, 1 = ZERO
        public ulong TargetData512;  // physical 512-byte sector within the super block device (LINEAR)
        public uint TargetSource;    // block device index
    }

    public class LogicalPartitionMeta
    {
        public string Name = "";
        public uint Attributes;
        public uint FirstExtentIndex;
        public uint NumExtents;
        public uint GroupIndex;
        public List<LpMetadataExtent> Extents = new();
    }

    public class SuperMetadata
    {
        public LpMetadataGeometry Geometry = new();
        public List<LogicalPartitionMeta> Partitions = new();
        public ulong FirstLogicalSector512;
        public ulong SuperSizeBytes;
        public byte[] GeometryBytes = Array.Empty<byte>();  // 4096 bytes
        public byte[] MetadataBytes = Array.Empty<byte>(); // header + tables
    }

    /// <summary>
    /// Parser for the Android liblp super-partition metadata (LpMetadataGeometry / LpMetadataHeader
    /// + partition/extent tables). Layout and field offsets match AOSP fs_mgr/liblp metadata_format.h.
    /// Extent offsets and lengths are in 512-byte sectors (LP_SECTOR_SIZE).
    /// </summary>
    public class LpMetadataParser
    {
        public const uint GEOMETRY_MAGIC = 0x616c4467;
        public const uint HEADER_MAGIC = 0x414C5030;
        public const int GEOMETRY_SIZE = 4096;
        public const int RESERVED_SIZE = 4096;
        public const int HEADER_SIZE = 256;
        public const int LP_SECTOR_SIZE = 512;
        public const int MAJOR_VERSION = 10;

        public SuperMetadata? Parse(string path) => Parse(File.ReadAllBytes(path));

        public SuperMetadata? Parse(byte[] data)
        {
            if (data.Length < GEOMETRY_SIZE + HEADER_SIZE) return null;
            try
            {
                using var ms = new MemoryStream(data);
                using var r = new BinaryReader(ms);
                var meta = new SuperMetadata();

                meta.Geometry.Magic = r.ReadUInt32();
                meta.Geometry.StructSize = r.ReadUInt32();
                r.ReadBytes(32); // checksum
                meta.Geometry.MetadataMaxSize = r.ReadUInt32();
                meta.Geometry.MetadataSlotCount = r.ReadUInt32();
                meta.Geometry.LogicalBlockSize = r.ReadUInt32();
                if (meta.Geometry.Magic != GEOMETRY_MAGIC) return null;

                meta.GeometryBytes = new byte[GEOMETRY_SIZE];
                Array.Copy(data, 0, meta.GeometryBytes, 0, Math.Min(GEOMETRY_SIZE, data.Length));

                ms.Position = GEOMETRY_SIZE;
                if (r.ReadUInt32() != HEADER_MAGIC) return null;
                ushort major = r.ReadUInt16();
                r.ReadUInt16(); // minor_version (not validated)
                uint headerSize = r.ReadUInt32();
                r.ReadBytes(32); // header checksum
                uint tablesSize = r.ReadUInt32();
                r.ReadBytes(32); // tables checksum
                var partDesc = ReadDesc(r);
                var extDesc = ReadDesc(r);
                ReadDesc(r); // groups (not needed)
                var devDesc = ReadDesc(r);
                if (major != MAJOR_VERSION) return null;

                long tablesBase = GEOMETRY_SIZE + headerSize;

                // block devices (first one is the super device)
                for (int i = 0; i < devDesc.NumEntries; i++)
                {
                    ms.Position = tablesBase + devDesc.Offset + (long)i * devDesc.EntrySize;
                    ulong firstLogicalSector = r.ReadUInt64();
                    r.ReadUInt32(); // alignment
                    r.ReadUInt32(); // alignment_offset
                    ulong size = r.ReadUInt64();
                    if (i == 0)
                    {
                        meta.FirstLogicalSector512 = firstLogicalSector;
                        meta.SuperSizeBytes = size;
                    }
                }

                // partitions
                for (int i = 0; i < partDesc.NumEntries; i++)
                {
                    ms.Position = tablesBase + partDesc.Offset + (long)i * partDesc.EntrySize;
                    string name = Encoding.ASCII.GetString(r.ReadBytes(36)).TrimEnd('\0');
                    uint attr = r.ReadUInt32();
                    uint firstExtent = r.ReadUInt32();
                    uint numExtents = r.ReadUInt32();
                    uint groupIndex = r.ReadUInt32();
                    meta.Partitions.Add(new LogicalPartitionMeta
                    {
                        Name = name,
                        Attributes = attr,
                        FirstExtentIndex = firstExtent,
                        NumExtents = numExtents,
                        GroupIndex = groupIndex
                    });
                }

                // extents
                var extents = new List<LpMetadataExtent>();
                for (int i = 0; i < extDesc.NumEntries; i++)
                {
                    ms.Position = tablesBase + extDesc.Offset + (long)i * extDesc.EntrySize;
                    ulong numSectors = r.ReadUInt64();
                    uint targetType = r.ReadUInt32();
                    ulong targetData = r.ReadUInt64();
                    uint targetSource = r.ReadUInt32();
                    extents.Add(new LpMetadataExtent
                    {
                        NumSectors512 = numSectors,
                        TargetType = targetType,
                        TargetData512 = targetData,
                        TargetSource = targetSource
                    });
                }

                // attach extents to partitions
                foreach (var p in meta.Partitions)
                {
                    for (uint e = 0; e < p.NumExtents; e++)
                    {
                        int idx = (int)(p.FirstExtentIndex + e);
                        if (idx >= 0 && idx < extents.Count) p.Extents.Add(extents[idx]);
                    }
                }

                int metaLen = (int)(headerSize + tablesSize);
                meta.MetadataBytes = new byte[metaLen];
                int srcOff = GEOMETRY_SIZE;
                Array.Copy(data, srcOff, meta.MetadataBytes, 0, Math.Min(metaLen, data.Length - srcOff));

                return meta;
            }
            catch
            {
                return null;
            }
        }

        private (uint Offset, uint NumEntries, uint EntrySize) ReadDesc(BinaryReader r)
            => (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());

        /// <summary>Absolute byte offsets (relative to super partition start) of every metadata copy.</summary>
        public static List<long> GetMetadataSlotOffsets(LpMetadataGeometry g)
        {
            var list = new List<long>();
            long metaBase = RESERVED_SIZE + 2L * GEOMETRY_SIZE; // 12288
            long mms = g.MetadataMaxSize;
            long C = g.MetadataSlotCount;
            for (long k = 0; k < C; k++) list.Add(metaBase + k * mms);            // primary
            for (long k = 0; k < C; k++) list.Add(metaBase + C * mms + k * mms);  // backup
            return list;
        }

        /// <summary>Total byte size of the metadata region that precedes the logical partitions.</summary>
        public static int GetMetadataRegionSize(LpMetadataGeometry g)
            => (int)(RESERVED_SIZE + 2L * GEOMETRY_SIZE + 2L * g.MetadataSlotCount * g.MetadataMaxSize);
    }
}
