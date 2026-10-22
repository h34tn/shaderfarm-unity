# Changelog

## 0.2.0

- UPM 包与配套桌面端使用相同版本号。

- 将 Unity IPC、缓存校验、Shader 分片与统计、AOT 辅助作用域收敛到独立 Editor UPM 包。
- 通过构建参数工厂接入工程的 YooAsset 构建流程，移除对游戏程序集的依赖。
- 保留原生 Shader 错误阻断检查。
