// Author: Codex
// Created: 2026.10.20 16:00:00
// Description: 独立项目副本使用正式 YooAsset 参数执行分片，仅输出临时包和可回传的编译报告。
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using YooAsset.Editor;

namespace ShaderFarm.Editor
{
    /// <summary>分布式 worker 的 Unity 批处理入口。</summary>
    public static class ShaderShardBuild
    {
        /// <summary>工程在 InitializeOnLoad 中注册，与正式资源包构建共用参数来源。</summary>
        public static Func<BuildTarget, ScriptableBuildParameters> ParametersFactory { get; set; }

        /// <summary>取得工程参数；未接入的工程必须失败，不能使用不完整的默认收集规则。</summary>
        public static ScriptableBuildParameters CreateParameters(BuildTarget target)
        {
            if (ParametersFactory == null)
            {
                throw new BuildFailedException("Register ShaderShardBuild.ParametersFactory in the project's Editor assembly.");
            }
            var parameters = ParametersFactory(target);
            if (parameters == null || parameters.BuildTarget != target)
            {
                throw new BuildFailedException("ShaderFarm build parameters are missing or target a different platform.");
            }
            return parameters;
        }

        /// <summary>构建 DefaultPackage 的一个变体分片；分片包永不进入正式发布目录。</summary>
        public static void Run()
        {
            int index = int.Parse(Argument("-rgameShaderShardIndex"));
            int count = int.Parse(Argument("-rgameShaderShardCount"));
            string reportPath = Argument("-rgameShaderReport");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                throw new BuildFailedException("Shader distribution requires StandaloneWindows64.");
            }
            var parameters = CreateParameters(BuildTarget.StandaloneWindows64);
            parameters.BuildOutputRoot = Path.GetFullPath("Build/ShaderShard");
            parameters.BuildinFileRoot = Path.GetFullPath("Build/ShaderShardBuiltin");
            parameters.BuildinFileCopyOption = EBuildinFileCopyOption.None;
            parameters.PackageVersion = "shard-" + Guid.NewGuid().ToString("N");
            var validationPackage = AddValidationPackage(parameters);
            ShaderFarmShaderTools.PrepareRecompile();
            ShaderVariantPartition.Begin(index, count);
            try
            {
                long logStart = new FileInfo(Application.consoleLogPath).Length;
                var result = new ScriptableBuildPipeline().Run(parameters, true);
                ShaderFarmShaderTools.ThrowIfCompilerErrors(Application.consoleLogPath, logStart);
                if (!result.Success)
                {
                    throw new BuildFailedException($"Shader shard failed: {result.FailedTask}: {result.ErrorInfo}");
                }
                if (ShaderVariantPartition.InputVariants == 0)
                {
                    throw new BuildFailedException("Shader partition received no compiler inputs.");
                }
                var report = new ShardReport
                {
                    success = true,
                    shardIndex = index,
                    shardCount = count,
                    inputDigest = ShaderVariantPartition.InputDigest,
                    inputVariants = ShaderVariantPartition.InputVariants,
                    assignedVariants = ShaderVariantPartition.AssignedVariants,
                    supportVariants = ShaderVariantPartition.SupportVariants,
                    shaders = ShaderFarmShaderTools.Results()
                };
                File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
                Debug.Log($"SHARD_SUCCESS {index}/{count}: " +
                    $"{report.assignedVariants}/{report.inputVariants} variants, input={report.inputDigest}");
            }
            finally
            {
                ShaderVariantPartition.End();
                if (validationPackage != null)
                {
                    AssetBundleCollectorSettingData.RemovePackage(validationPackage);
                }
            }
        }

        private static AssetBundleCollectorPackage AddValidationPackage(ScriptableBuildParameters parameters)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, "-rgameShaderValidation") < 0)
            {
                return null;
            }
            string path = Argument("-rgameShaderValidation");
            if (AssetDatabase.LoadAssetAtPath<Shader>(path) == null)
            {
                throw new BuildFailedException("Validation shader does not exist: " + path);
            }
            // 验收使用同一条 YooAsset/SBP 管线，仅将收集范围限制到指定 Shader；不写收集配置资产。
            parameters.PackageName = "ShaderDistributionValidation";
            var package = AssetBundleCollectorSettingData.CreatePackage(parameters.PackageName);
            var group = AssetBundleCollectorSettingData.CreateGroup(package, "Validation");
            AssetBundleCollectorSettingData.CreateCollector(group, new AssetBundleCollector
            {
                CollectPath = path,
                CollectorGUID = AssetDatabase.AssetPathToGUID(path)
            });
            return package;
        }

        internal static string Argument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name);
            if (index < 0 || index + 1 >= arguments.Length)
            {
                throw new BuildFailedException("Missing argument: " + name);
            }
            return arguments[index + 1];
        }

        [Serializable]
        private sealed class ShardReport
        {
            public ShaderFarmShaderTools.ShaderRecord[] shaders;
            public bool success;
            public int shardIndex;
            public int shardCount;
            public string inputDigest;
            public long inputVariants;
            public long assignedVariants;
            public long supportVariants;
        }
    }
}
