// Author: Codex
// Created: 2026.10.20 16:00:00
// Description: 通过本机 IPC 调用 ShaderFarm，校验任务结果并在准备进程中导入 ShaderCache。
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ShaderFarm.Editor
{
    /// <summary>正式构建的分布式预编译入口；保持准备与正式构建两个 Unity 进程。</summary>
    public static class ShaderDistributionBuild
    {
        private const string ConfigPath = "Build/ShaderDistribution/config.json";
        public static bool IsPreparation => Environment.GetCommandLineArgs().Contains("-rgameShaderPrepareOnly");
        public static string PreparedResultPath { get; private set; }

        public static bool Prepare(BuildTarget target)
        {
            if (!File.Exists(ConfigPath))
            {
                return true;
            }
            var config = JsonUtility.FromJson<DistributionConfig>(File.ReadAllText(ConfigPath));
            if (config == null || !config.enabled)
            {
                return true;
            }
            try
            {
                if (target != BuildTarget.StandaloneWindows64 || config.transport != "ShaderFarm" ||
                    string.IsNullOrWhiteSpace(config.pipeName) || config.timeoutSeconds < 60 ||
                    config.timeoutSeconds > 43200)
                {
                    throw new InvalidDataException("请通过 ShaderFarm Master 重新接入工程；仅支持 Windows64。");
                }
                string[] arguments = Environment.GetCommandLineArgs();
                int prepared = Array.IndexOf(arguments, "-rgameShaderPreparedResult");
                if (prepared >= 0)
                {
                    if (prepared + 1 >= arguments.Length || !Application.isBatchMode || IsPreparation)
                    {
                        throw new InvalidDataException("Invalid prepared result argument.");
                    }
                    string path = Path.GetFullPath(arguments[prepared + 1]);
                    ValidateResult(path, null);
                    var receipt = JsonUtility.FromJson<ImportReceipt>(File.ReadAllText(path + ".imported.json"));
                    if (receipt == null || receipt.resultHash != HashText(path) ||
                        !string.Equals(receipt.projectPath, Path.GetFullPath("."), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Prepared cache was not imported for this project.");
                    }
                    Debug.Log("使用上一 Unity 进程已验证并导入的 ShaderFarm 编译结果");
                    return true;
                }
                if (!Application.isBatchMode || !IsPreparation)
                {
                    throw new InvalidOperationException("请关闭本工程 Editor，通过 Tools/Build/build.ps1 -Mode batch " +
                        "执行准备及正式构建两个 Unity 进程阶段。");
                }
                var job = ShaderFarmIpcClient.Prepare(config.pipeName, Path.GetFullPath("."),
                    EditorApplication.applicationPath, config.timeoutSeconds);
                var report = ValidateResult(job.ResultPath, job);
                int imported = ImportCache(Path.GetFullPath(report.cacheDirectory));
                var importedReceipt = new ImportReceipt
                {
                    projectPath = Path.GetFullPath("."), resultHash = HashText(job.ResultPath)
                };
                File.WriteAllText(job.ResultPath + ".imported.json", JsonUtility.ToJson(importedReceipt));
                PreparedResultPath = job.ResultPath;
                Debug.Log($"Shader 分布式预编译成功：{report.inputVariants} 变体，" +
                    $"{report.cacheFiles} 个缓存文件，新增 {imported} 个，结果={job.ResultPath}");
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("ShaderFarm 预编译失败，构建中止: " + exception.Message);
                return false;
            }
        }

        private static DistributionResult ValidateResult(string path, ShaderFarmIpcClient.JobData job)
        {
            var report = JsonUtility.FromJson<DistributionResult>(File.ReadAllText(path));
            if (report == null || !report.success || report.taskType != "project-sbp" ||
                report.platform != "StandaloneWindows64" || report.cacheFiles < 1 || report.cacheLayoutVersion != 2 ||
                report.workers < 1 || report.workers > 64 || report.inputVariants <= 0 ||
                string.IsNullOrEmpty(report.cacheDirectory) || !Directory.Exists(report.cacheDirectory) ||
                !string.Equals(report.projectPath, Path.GetFullPath("."), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Incomplete or unrelated ShaderFarm result.");
            }
            if (job != null && (report.jobId != job.Id || report.attemptId != job.AttemptId ||
                report.workers != job.WorkerCount))
            {
                throw new InvalidDataException("ShaderFarm task identity mismatch.");
            }
            string root = Path.GetDirectoryName(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
            string cache = Path.GetFullPath(report.cacheDirectory);
            if (!cache.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                Directory.GetFiles(cache, "*.bin", SearchOption.AllDirectories).Length != report.cacheFiles)
            {
                throw new InvalidDataException("ShaderFarm cache path or file count mismatch.");
            }
            return report;
        }

        public static int ImportCache(string source)
        {
            string destination = Path.GetFullPath("Library/ShaderCache");
            string[] files = Directory.GetFiles(source, "*.bin", SearchOption.AllDirectories);
            int imported = 0;
            foreach (string file in files)
            {
                string relative = file.Substring(source.Length + 1).Replace('\\', '/');
                if (!Regex.IsMatch(relative, "\\A(?:shader|shadergraph|builtin|compute)/" +
                    "[A-Za-z0-9_-](?:[A-Za-z0-9_ -]{0,126}[A-Za-z0-9_-])?/" +
                    "[a-fA-F0-9]{2}/[a-fA-F0-9]{32}\\.bin\\z"))
                {
                    throw new InvalidDataException("Invalid ShaderCache entry: " + relative);
                }
                string path = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path))
                {
                    if (!Hash(file).SequenceEqual(Hash(path)))
                    {
                        throw new InvalidDataException("Conflicting ShaderCache key: " + relative);
                    }
                    continue;
                }
                string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(file, pending);
                    File.Move(pending, path);
                    ++imported;
                }
                finally
                {
                    if (File.Exists(pending))
                    {
                        File.Delete(pending);
                    }
                }
            }
            return imported;
        }

        private static byte[] Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(path))
            {
                return sha.ComputeHash(input);
            }
        }

        private static string HashText(string path)
        {
            return BitConverter.ToString(Hash(path)).Replace("-", "").ToLowerInvariant();
        }

        [Serializable]
        private sealed class DistributionConfig
        {
            public bool enabled = false;
            public string transport = "";
            public string pipeName = "";
            public int timeoutSeconds = 43200;
        }

        [Serializable]
        private sealed class DistributionResult
        {
            public bool success = false;
            public string taskType = "";
            public string cacheDirectory = "";
            public string projectPath = "";
            public string platform = "";
            public string jobId = "";
            public string attemptId = "";
            public int cacheFiles = 0;
            public int cacheLayoutVersion = 0;
            public int workers = 0;
            public long inputVariants = 0;
        }

        [Serializable]
        private sealed class ImportReceipt
        {
            public string projectPath = "";
            public string resultHash = "";
        }
    }
}
