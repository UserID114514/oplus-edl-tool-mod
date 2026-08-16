using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OplusEdlTool.Services
{
    /// <summary>
    /// Builds the flash tasks for writing the super partition WITHOUT merging into a single super.img:
    /// flashes the pre-built metadata (super_meta.raw) to the metadata slots, then each logical
    /// partition image to its extent offset. Mirrors OplusEdlTool v3 "sub-partition write" mode.
    /// </summary>
    public class SuperSubPartitionService
    {
        private readonly Action<string>? _onLog;
        private readonly LpMetadataParser _parser = new();

        public SuperSubPartitionService(Action<string>? onLog = null)
        {
            _onLog = onLog;
        }

        private void Log(string message) => _onLog?.Invoke(message);

        /// <summary>
        /// Checks whether this firmware supports sub-partition write: a super_def.*.json exists
        /// AND a real (non-stub) super_meta.*.raw exists. Prefers the 10010111 NV variant.
        /// </summary>
        public bool CheckSupport(string romBaseDir, out string? superDefJsonPath, out string? superMetaRawPath)
        {
            superDefJsonPath = null;
            superMetaRawPath = null;
            try
            {
                var merge = new SuperMergeService(_onLog);
                var metaDir = Path.Combine(romBaseDir, "META");
                if (!Directory.Exists(metaDir)) return false;

                var jsons = Directory.GetFiles(metaDir, "super_def.*.json");
                if (jsons.Length == 0) return false;

                // Prefer the 10010111 NV variant (user confirmed both boot; 10010111 is the default).
                var jsonPath = jsons.FirstOrDefault(f => f.Contains("10010111", StringComparison.OrdinalIgnoreCase))
                               ?? jsons[0];

                var config = merge.ParseSuperDefJson(jsonPath);
                if (config?.SuperMeta == null || string.IsNullOrEmpty(config.SuperMeta.Path)) return false;

                var metaPath = ResolveSuperMetaRaw(romBaseDir, config.SuperMeta.Path);
                if (metaPath == null) return false;

                superDefJsonPath = jsonPath;
                superMetaRawPath = metaPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves the real super_meta raw file. A stub (23-byte) file contains the target filename
        /// as text; a real file starts with the geometry magic 0x616c4467.
        /// </summary>
        private string? ResolveSuperMetaRaw(string romBaseDir, string superMetaPath)
        {
            var imagesDir = Path.Combine(romBaseDir, "IMAGES");
            var p = Path.Combine(romBaseDir, superMetaPath.Replace("/", Path.DirectorySeparatorChar.ToString()));

            if (File.Exists(p))
            {
                var bytes = File.ReadAllBytes(p);
                if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == LpMetadataParser.GEOMETRY_MAGIC)
                    return p;

                // redirect stub: content is a relative filename
                if (bytes.Length > 0 && bytes.Length < 128)
                {
                    var target = Encoding.ASCII.GetString(bytes).Trim().Trim('\0', '\r', '\n');
                    var resolved = Path.Combine(Path.GetDirectoryName(p) ?? imagesDir, target);
                    if (File.Exists(resolved))
                    {
                        var rb = File.ReadAllBytes(resolved);
                        if (rb.Length >= 4 && BitConverter.ToUInt32(rb, 0) == LpMetadataParser.GEOMETRY_MAGIC)
                            return resolved;
                    }
                }
            }

            // fallback: any real super_meta.*.raw, prefer 10010111
            if (Directory.Exists(imagesDir))
            {
                var candidates = Directory.GetFiles(imagesDir, "super_meta.*.raw")
                    .OrderByDescending(f => f.Contains("10010111", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
                foreach (var c in candidates)
                {
                    try
                    {
                        var cb = File.ReadAllBytes(c);
                        if (cb.Length >= 4 && BitConverter.ToUInt32(cb, 0) == LpMetadataParser.GEOMETRY_MAGIC)
                            return c;
                    }
                    catch { }
                }
            }
            return null;
        }

        public SuperMetadata? ParseSuperMeta(string path) => _parser.Parse(path);

        /// <summary>
        /// Expands the compact super_meta.raw into the full on-device metadata region:
        /// [reserved zeros 4096][geometry @4096][geometry backup @8192][metadata slot copies @12288+].
        /// </summary>
        public byte[] BuildMetadataBuffer(SuperMetadata meta)
        {
            int regionSize = LpMetadataParser.GetMetadataRegionSize(meta.Geometry);
            var buf = new byte[regionSize];
            int mms = (int)meta.Geometry.MetadataMaxSize;
            long C = meta.Geometry.MetadataSlotCount;
            long metaBase = LpMetadataParser.RESERVED_SIZE + 2L * LpMetadataParser.GEOMETRY_SIZE; // 12288

            // geometry (reserved prefix 0..4096 stays zero)
            Array.Copy(meta.GeometryBytes, 0, buf, LpMetadataParser.GEOMETRY_SIZE, LpMetadataParser.GEOMETRY_SIZE);
            Array.Copy(meta.GeometryBytes, 0, buf, 2 * LpMetadataParser.GEOMETRY_SIZE, LpMetadataParser.GEOMETRY_SIZE);

            for (long k = 0; k < C; k++)
            {
                CopyMeta(buf, metaBase + k * mms, meta.MetadataBytes, mms);           // primary
                CopyMeta(buf, metaBase + C * mms + k * mms, meta.MetadataBytes, mms); // backup
            }
            return buf;
        }

        private void CopyMeta(byte[] buf, long dest, byte[] meta, int mms)
        {
            int len = Math.Min(meta.Length, mms);
            Array.Copy(meta, 0, buf, (int)dest, len);
        }

        /// <summary>
        /// Maps each logical partition (from super_def.json) to a flash task at its physical extent.
        /// </summary>
        public List<EdlService.FlashPartitionInfo> BuildSubPartitionTasks(
            SuperMergeService.SuperConfig config,
            SuperMetadata meta,
            string romBaseDir,
            ulong superStartSector,
            int lun,
            int sectorSize)
        {
            var tasks = new List<EdlService.FlashPartitionInfo>();
            var byName = new Dictionary<string, LogicalPartitionMeta>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in meta.Partitions) byName[p.Name] = p;

            int scale = sectorSize / LpMetadataParser.LP_SECTOR_SIZE; // 4096/512 = 8

            foreach (var part in config.Partitions)
            {
                if (string.IsNullOrEmpty(part.Path)) continue; // empty slot (_b)
                if (!byName.TryGetValue(part.Name, out var lp)) continue;
                var ext = lp.Extents.FirstOrDefault();
                if (ext == null || ext.TargetType != 0) continue; // LINEAR only
                var imgPath = Path.Combine(romBaseDir, part.Path!.Replace("/", Path.DirectorySeparatorChar.ToString()));
                if (!File.Exists(imgPath))
                {
                    Log($"Sub-partition image not found, skipping: {part.Name}");
                    continue;
                }
                ulong start = superStartSector + ext.TargetData512 / (ulong)scale;
                ulong num = ext.NumSectors512 / (ulong)scale;
                if (num == 0) continue;
                tasks.Add(new EdlService.FlashPartitionInfo
                {
                    Name = part.Name,
                    FilePath = imgPath,
                    Lun = lun.ToString(),
                    StartSector = start,
                    NumSectors = num,
                    SectorSize = sectorSize
                });
            }
            return tasks;
        }
    }
}
