// Author: Codex
// Created: 2026.10.21 09:10:00
// Description: 导出 Shader 资产身份与分片前统计，供桌面端定向重编与统计列表使用。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace ShaderFarm.Editor
{
    /// <summary>ShaderFarm 的目录导出与每 Shader 构建统计。</summary>
    public static class ShaderFarmShaderTools
    {
        [Serializable]
        public sealed class ShaderRecord
        {
            public string Id = "", Name = "", AssetPath = "", Guid = "", CacheGroup = "", InputDigest = "";
            public long InputVariants = -1, CompiledVariants = -1;
            public double CompileSeconds = -1;
            public bool CanRecompile;
            public string Reason = "", State = "", Error = "", UpdatedUtc = "";
        }

        [Serializable]
        private sealed class Catalog
        {
            public ShaderRecord[] Shaders;
        }

        private static readonly Dictionary<string, ShaderRecord> Records = new Dictionary<string, ShaderRecord>();
        private static readonly Dictionary<string, List<string>> Digests = new Dictionary<string, List<string>>();

        /// <summary>SBP 可能返回成功却留下原生 Shader 编译错误；只检查本次构建追加的日志。</summary>
        public static void ThrowIfCompilerErrors(string logPath, long startOffset)
        {
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (startOffset < 0 || startOffset > stream.Length)
                {
                    throw new BuildFailedException("Shader compiler log was truncated during the build.");
                }
                stream.Seek(startOffset, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.TrimStart().StartsWith("Shader error in ", StringComparison.Ordinal))
                        {
                            throw new BuildFailedException("Shader shard compilation failed: " + line.Trim());
                        }
                    }
                }
            }
        }

        /// <summary>批处理读取资产，不执行构建。</summary>
        public static void ExportCatalog()
        {
            var rows = GetCatalog();
            File.WriteAllText(ShaderShardBuild.Argument("-rgameShaderCatalog"),
                JsonUtility.ToJson(new Catalog { Shaders = rows }, true));
        }

        private static ShaderRecord Describe(Shader shader)
        {
            string path = AssetDatabase.GetAssetPath(shader);
            string guid = AssetDatabase.AssetPathToGUID(path);
            var record = new ShaderRecord
            {
                Id = guid + "/" + shader.name,
                Name = shader.name,
                AssetPath = path,
                Guid = guid,
                Reason = "该 Shader 没有可独立清理的资产缓存"
            };
            string extension = Path.GetExtension(path);
            if ((extension != ".shader" && extension != ".shadergraph") ||
                !Regex.IsMatch(guid, "\\A[a-f0-9]{32}\\z"))
            {
                return record;
            }
            string group = (extension == ".shader" ? "shader/" : "shadergraph/") +
                Path.GetFileNameWithoutExtension(path) + guid.Substring(0, 4);
            if (Regex.IsMatch(group, "\\A(?:shader|shadergraph)/[A-Za-z0-9_ -]{1,127}\\z"))
            {
                record.CacheGroup = group;
                record.CanRecompile = Directory.Exists(Path.Combine("Library/ShaderCache", group));
                record.Reason = record.CanRecompile ? "" : "尚无可确认的缓存映射，请构建一次后刷新列表";
            }
            return record;
        }

        private static ShaderRecord[] GetCatalog()
        {
            var shaders = new Dictionary<string, ShaderRecord>();
            foreach (string guid in AssetDatabase.FindAssets("t:Shader").Concat(
                AssetDatabase.FindAssets("t:ShaderGraph")).Distinct())
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (Shader shader in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Shader>())
                {
                    var record = Describe(shader);
                    shaders[record.Id] = record;
                }
            }
            foreach (var group in shaders.Values.Where(r => r.CacheGroup.Length > 0).GroupBy(r => r.CacheGroup))
            {
                if (group.Count() > 1)
                {
                    foreach (var record in group)
                    {
                        record.CanRecompile = false;
                        record.Reason = "多个 Shader 共用缓存组，不能单独重编";
                    }
                }
            }
            foreach (var group in shaders.Values.GroupBy(r => r.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                foreach (var record in group)
                {
                    record.CanRecompile = false;
                    record.Reason = "日志不能区分同名 Shader 资产";
                }
            }
            return shaders.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();
        }

        /// <summary>开始本轮统计，清除内存中的历史记录。</summary>
        public static void Begin()
        {
            Records.Clear();
            Digests.Clear();
        }

        /// <summary>记录已有剥离完成后、分片之前的输入，保留重复回调次数。</summary>
        public static void Record(Shader shader, string identity, IList<string> keys)
        {
            string id = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(shader)) + "/" + shader.name;
            if (!Records.TryGetValue(id, out var record))
            {
                record = Describe(shader);
                record.InputVariants = 0;
                Records.Add(id, record);
                Digests.Add(id, new List<string>());
            }
            record.InputVariants += keys.Count;
            Digests[id].Add(Hash(identity + string.Join("\n", keys.OrderBy(k => k, StringComparer.Ordinal))));
        }

        /// <summary>结束构建后导出统计及已确认的缓存映射。</summary>
        public static ShaderRecord[] Results()
        {
            var catalog = GetCatalog().ToDictionary(r => r.Id);
            foreach (var record in Records.Values)
            {
                record.InputDigest = Hash(string.Join("\n", Digests[record.Id].OrderBy(k => k, StringComparer.Ordinal)));
                if (catalog.TryGetValue(record.Id, out var mapped))
                {
                    record.CacheGroup = mapped.CacheGroup;
                    record.CanRecompile = mapped.CanRecompile;
                    record.Reason = mapped.Reason;
                }
            }
            return Records.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        }

        /// <summary>磁盘缓存已在启动 Unity 前失效；重新导入目标，避免复用旧的序列化 Shader 数据。</summary>
        public static void PrepareRecompile()
        {
            if (!Environment.GetCommandLineArgs().Contains("-rgameShaderRecompile"))
            {
                return;
            }
            string path = ShaderShardBuild.Argument("-rgameShaderRecompile");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null || shader.name != ShaderShardBuild.Argument("-rgameShaderName"))
            {
                throw new InvalidDataException("Shader 资产身份改变，请刷新列表。");
            }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
