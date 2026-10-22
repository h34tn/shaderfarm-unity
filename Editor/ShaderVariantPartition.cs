// Author: Codex
// Created: 2026.10.20 16:00:00
// Description: 在既有剥离完成后按稳定散列分配变体，并记录分片前完整输入的摘要。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

namespace ShaderFarm.Editor
{
    /// <summary>只在 worker 显式作用域内生效，正常出包保留全部通过现有规则的变体。</summary>
    public sealed class ShaderVariantPartition : IPreprocessShaders
    {
        private static readonly List<string> InputDigests = new List<string>();
        private static bool _isActive;
        private static int _shardIndex;
        private static int _shardCount;

        public int callbackOrder => int.MaxValue;
        internal static long InputVariants { get; private set; }
        internal static long AssignedVariants { get; private set; }
        internal static long SupportVariants { get; private set; }
        internal static string InputDigest => Hash(string.Join("\n", InputDigests.OrderBy(s => s, StringComparer.Ordinal)));

        internal static void Begin(int index, int count)
        {
            if (_isActive || count < 1 || index < 0 || index >= count)
            {
                throw new InvalidOperationException("Invalid shader partition scope.");
            }
            InputDigests.Clear();
            ShaderFarmShaderTools.Begin();
            InputVariants = 0;
            AssignedVariants = 0;
            SupportVariants = 0;
            _shardIndex = index;
            _shardCount = count;
            _isActive = true;
        }

        internal static void End()
        {
            _isActive = false;
        }

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (!_isActive)
            {
                return;
            }
            string shaderPath = AssetDatabase.GetAssetPath(shader);
            string identity = AssetDatabase.AssetPathToGUID(shaderPath) + "/" + shader.name + "/" +
                snippet.passName + "/" + snippet.passType + "/" + snippet.shaderType + "/";
            var keys = new List<string>(data.Count);
            var representatives = new Dictionary<string, ShaderCompilerData>();
            var retainedGroups = new HashSet<string>(StringComparer.Ordinal);
            InputVariants += data.Count;
            for (int i = data.Count - 1; i >= 0; --i)
            {
                var item = data[i];
                string group = item.shaderCompilerPlatform + "/" + item.graphicsTier;
                representatives[group] = item;
                string keywords = string.Join(",", item.shaderKeywordSet.GetShaderKeywords()
                    .Select(keyword => keyword.name).OrderBy(name => name, StringComparer.Ordinal));
                string key = identity + item.shaderCompilerPlatform + "/" + item.graphicsTier + "/" +
                    item.shaderRequirements + "/" + keywords;
                keys.Add(key);
                uint hash = 2166136261;
                foreach (char character in key)
                {
                    hash = unchecked((hash ^ character) * 16777619);
                }
                if (hash % _shardCount == _shardIndex)
                {
                    ++AssignedVariants;
                    retainedGroups.Add(group);
                }
                else
                {
                    data.RemoveAt(i);
                }
            }
            foreach (var representative in representatives)
            {
                if (!retainedGroups.Contains(representative.Key))
                {
                    // Unity 在顶点阶段无变体时跳过后续片元回调。每平台/层级保留一个支撑变体，
                    // 使各节点都能看到完整后续输入；该少量重复编译不计入分片所有权。
                    data.Add(representative.Value);
                    ++SupportVariants;
                }
            }
            // 按回调排序保留重复次数；节点若漏跑/复用错误的 SBP 缓存，摘要或总数会不一致。
            ShaderFarmShaderTools.Record(shader, identity, keys);
            InputDigests.Add(Hash(identity + string.Join("\n", keys.OrderBy(s => s, StringComparer.Ordinal))));
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
