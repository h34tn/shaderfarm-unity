# ShaderFarm for Unity

ShaderFarm 为 Unity 2022.3 / Windows64 的 YooAsset 2.3.19 + SBP 构建提供分布式 Shader 编译、缓存校验、统计和定向重编。包仅包含 Editor 程序集，不进入 Player，也不依赖任何游戏工程程序集。

本包需要 ShaderFarm Master 与 Worker 0.2.0 或更新版本；任务提交和节点注册时均检查 UPM 接入能力。

## 安装

包发布到 OpenUPM 后，在工程 `Packages/manifest.json` 的 scoped registry 中添加 `com.intelliorigins.shaderfarm` scope（已有的 YooAsset scope 保留），并添加固定版本依赖：

```json
{
  "dependencies": {
    "com.intelliorigins.shaderfarm": "0.2.0"
  },
  "scopedRegistries": [{
    "name": "OpenUPM",
    "url": "https://package.openupm.com",
    "scopes": ["com.intelliorigins.shaderfarm", "com.tuyoogame.yooasset"]
  }]
}
```

公开仓库及版本标签发布后，也可在 Unity Package Manager 中选择 **Add package from git URL**，输入 `https://github.com/h34tn/shaderfarm-unity.git#upm/0.2.0`。本仓库根目录即 UPM 包；YooAsset 依赖仍需配置上面的 OpenUPM registry。离线验收可使用维护者提供的 `.tgz` 文件，通过 **Add package from tarball** 导入。ShaderFarm Master 与 Worker 桌面端由维护者另行提供，安装 Unity 包不会安装桌面端。

## 接入工程构建

工程的 Editor asmdef 引用 `ShaderFarm.Editor`。通过 `[InitializeOnLoad]` 注册 `ShaderShardBuild.ParametersFactory`，返回与正式 YooAsset 构建共用的 `ScriptableBuildParameters`。工厂在每个 Unity 进程启动时都必须注册，包括远端 Worker 的工程副本；不要在包中加入游戏程序集引用。缺少工厂会明确失败。

```csharp
using ShaderFarm.Editor;
using UnityEditor;

[InitializeOnLoad]
internal static class ProjectShaderFarmBuild
{
    static ProjectShaderFarmBuild()
    {
        ShaderShardBuild.ParametersFactory = target =>
            ProjectBundleBuilder.CreateParameters(target, clean: true);
    }
}
```

其中 `ProjectBundleBuilder` 是工程自己的构建工具。正式构建前调用 `ShaderDistributionBuild.Prepare(target)`；为 false 时中止。分布式构建分为两个独立 Unity 进程：

1. 准备进程带 `-rgameShaderPrepareOnly`，提交 Master 任务、校验结果、导入缓存；记录 `ShaderDistributionBuild.PreparedResultPath` 后退出。
2. 正式进程带 `-rgameShaderPreparedResult <结果路径>`，验证导入凭据后执行完整资源包与 Player 构建。

原有 `-rgameShader*` 参数是与桌面端通信的兼容契约。Worker 的入口为 `ShaderFarm.Editor.ShaderShardBuild.Run`；目录导出入口为 `ShaderFarm.Editor.ShaderFarmShaderTools.ExportCatalog`。

临时 AOT 元数据包可用 `AotShaderCompilationScope.Begin(expectedOutputDirectory)` 限定跳过 Shader 字节码的范围；正式 Player 必须使用 `BuildOptions.CleanBuildCache`，不能复用临时包的序列化 Shader 产物。

Master 的“项目接入 → 启用/停用”写入工程本地 `Build/ShaderDistribution/config.json`，该配置应被工程 Git 忽略。未启用时按本地 Unity 流程构建；启用后服务或任务失败会中止，不自动降级。

## 源码与验证

本公开仓库是 Unity 包源码的唯一维护仓库，修改直接通过本仓库的 Issue 或 PR 协作。ShaderFarm 桌面端主仓通过 Git submodule 固定引用本仓库的已发布提交，不再维护或导出包源码副本。升级通过 UPM 完成；Master 不复制、安装或覆盖 `Assets` 中的 C# 文件。旧的 Assets 适配器须先移除，避免同时加载两套 Shader 回调。

程序集需要 YooAsset 与 SBP 构建环境。Unity Test Framework 测试位于 `Tests/Editor`，在工程 manifest 的 `testables` 中加入 `com.intelliorigins.shaderfarm` 后可运行。完整双节点冷编译、结果导入与新 Unity 进程缓存复用验收由维护者在主仓执行。

License: Apache-2.0，详见 LICENSE.md。
