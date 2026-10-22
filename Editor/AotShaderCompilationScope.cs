// Author: Codex
// Created: 2026.10.20 17:52:00
// Description: 仅在 HybridCLR 临时 AOT 构建内跳过不交付的 Shader 字节码，结束后恢复正常编译。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;

namespace ShaderFarm.Editor
{
    /// <summary>
    /// AOT 辅助构建保留场景、资源依赖与托管代码裁剪，只省略临时包的 Shader 字节码。
    /// 正式 Player 必须使用 CleanBuildCache，不能复用该临时构建的序列化 Shader 产物。
    /// </summary>
    public sealed class AotShaderCompilationScope : IPreprocessBuildWithReport, IPostprocessBuildWithReport,
        IPreprocessShaders, IPreprocessComputeShaders
    {
        private static string _expectedOutputDirectory;
        private static bool _isProcessingAotBuild;
        private static long _shaderVariants;
        private static long _computeVariants;

        public int callbackOrder => int.MaxValue;

        public static IDisposable Begin(string outputDirectory)
        {
            if (_expectedOutputDirectory != null || string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("Invalid or nested AOT shader compilation scope.");
            }

            _expectedOutputDirectory = Path.GetFullPath(outputDirectory).TrimEnd('\\', '/') +
                Path.DirectorySeparatorChar;
            _shaderVariants = 0;
            _computeVariants = 0;
            _isProcessingAotBuild = false;
            Debug.Log("[AotShaderCompilationScope] BEGIN: metadata-only auxiliary build; " +
                "shader bytecode deferred to distributed resource compilation.");
            return new Scope();
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (_expectedOutputDirectory == null)
            {
                return;
            }

            string output = Path.GetFullPath(report.summary.outputPath);
            if (!output.StartsWith(_expectedOutputDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException("AOT shader scope refuses unexpected Player output: " + output);
            }

            _isProcessingAotBuild = true;
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            _isProcessingAotBuild = false;
        }

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (!_isProcessingAotBuild)
            {
                return;
            }

            _shaderVariants += data.Count;
            data.Clear();
        }

        public void OnProcessComputeShader(ComputeShader shader, string kernelName, IList<ShaderCompilerData> data)
        {
            if (!_isProcessingAotBuild)
            {
                return;
            }

            _computeVariants += data.Count;
            data.Clear();
        }

        private sealed class Scope : IDisposable
        {
            private bool _isDisposed;

            public void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                _isProcessingAotBuild = false;
                _expectedOutputDirectory = null;
                Debug.Log($"[AotShaderCompilationScope] END: omitted {_shaderVariants} shader variants, " +
                    $"{_computeVariants} compute variants from the auxiliary build; normal shader compilation restored.");
            }
        }
    }
}
