// Author: Codex
// Created: 2026.10.22 12:00:00
// Description: 验证工程构建参数边界和原生 Shader 错误阻断，不依赖游戏工程。
using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using YooAsset.Editor;

namespace ShaderFarm.Editor.Tests
{
    public class BuildContractTests
    {
        [Test]
        public void Parameters_MissingFactoryOrWrongTarget_IsRejected()
        {
            Func<BuildTarget, ScriptableBuildParameters> saved = ShaderShardBuild.ParametersFactory;
            try
            {
                ShaderShardBuild.ParametersFactory = null;
                Assert.Throws<BuildFailedException>(() => ShaderShardBuild.CreateParameters(BuildTarget.StandaloneWindows64));
                ShaderShardBuild.ParametersFactory = target => null;
                Assert.Throws<BuildFailedException>(() => ShaderShardBuild.CreateParameters(BuildTarget.StandaloneWindows64));
                ShaderShardBuild.ParametersFactory = target => new ScriptableBuildParameters { BuildTarget = BuildTarget.Android };
                Assert.Throws<BuildFailedException>(() => ShaderShardBuild.CreateParameters(BuildTarget.StandaloneWindows64));
            }
            finally
            {
                ShaderShardBuild.ParametersFactory = saved;
            }
        }

        [Test]
        public void Parameters_UsesProjectCollectionAndBuildSettings()
        {
            Func<BuildTarget, ScriptableBuildParameters> saved = ShaderShardBuild.ParametersFactory;
            try
            {
                var expected = new ScriptableBuildParameters
                {
                    BuildTarget = BuildTarget.StandaloneWindows64,
                    PackageName = "ProjectSpecificPackage",
                    TrackSpriteAtlasDependencies = true
                };
                ShaderShardBuild.ParametersFactory = target => expected;
                var actual = ShaderShardBuild.CreateParameters(BuildTarget.StandaloneWindows64);
                Assert.That(actual.PackageName, Is.EqualTo("ProjectSpecificPackage"));
                Assert.That(actual.TrackSpriteAtlasDependencies, Is.True);
            }
            finally
            {
                ShaderShardBuild.ParametersFactory = saved;
            }
        }

        [Test]
        public void WorkerSuccess_WithNativeShaderError_IsRejected()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "Build successful\nShader error in 'Shader': sampler limit exceeded\n", new UTF8Encoding(false));
                Assert.Throws<BuildFailedException>(() => ShaderFarmShaderTools.ThrowIfCompilerErrors(path, 0));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void WorkerLog_PreviousFailureDoesNotPoisonNewBuild_AndTruncationFails()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "Shader error in 'OldShader': old failure\n", new UTF8Encoding(false));
                long start = new FileInfo(path).Length;
                File.AppendAllText(path, "Shader warning in 'Shader': warning\nBuild successful\n");
                Assert.DoesNotThrow(() => ShaderFarmShaderTools.ThrowIfCompilerErrors(path, start));
                Assert.Throws<BuildFailedException>(() =>
                    ShaderFarmShaderTools.ThrowIfCompilerErrors(path, new FileInfo(path).Length + 1));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
