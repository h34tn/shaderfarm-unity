// Author: Codex
// Created: 2026.10.20 16:00:00
// Description: Unity 构建通过本机命名管道请求 ShaderFarm，续约任务并接收已验证结果。
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ShaderFarm.Editor
{
    internal static class ShaderFarmIpcClient
    {
        private const int MaxFrame = 1048576;
        internal static JobData Prepare(string pipeName, string project, string unity, int timeout)
        {
            var capabilities = Call(pipeName, new Envelope
            {
                Kind = "GetCapabilities", RequestId = Guid.NewGuid().ToString("N")
            });
            if (capabilities.ProjectCapability != "project-sbp-cache-v2-upm-v1")
            {
                throw new InvalidOperationException("ShaderFarm UPM requires Master and Workers 0.2.0 or later.");
            }
            string requestId = Guid.NewGuid().ToString("N");
            string[] arguments = Environment.GetCommandLineArgs();
            int validation = Array.IndexOf(arguments, "-rgameShaderValidation");
            var request = new Envelope
            {
                Kind = "SubmitPreparation",
                RequestId = requestId,
                Job = new JobData
                {
                    ProjectPath = project,
                    UnityPath = unity,
                    Platform = "StandaloneWindows64",
                    TimeoutSeconds = timeout,
                    ValidationShader = validation >= 0 && validation + 1 < arguments.Length ?
                        arguments[validation + 1] : ""
                }
            };
            // 固定 requestId 使提交响应丢失后的重试不会重复创建任务。
            JobData job;
            try
            {
                job = Call(pipeName, request).Job;
            }
            catch (IOException)
            {
                job = Call(pipeName, request).Job;
            }
            if (job == null || string.IsNullOrEmpty(job.Id))
            {
                throw new InvalidDataException("Master did not return a task.");
            }
            var clock = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    if (job.State == "Succeeded")
                    {
                        return job;
                    }
                    if (job.State == "Failed" || job.State == "Cancelled" || job.State == "Interrupted")
                    {
                        throw new InvalidOperationException(job.Error + "; log: " + job.LogPath);
                    }
                    if (clock.Elapsed.TotalSeconds > timeout)
                    {
                        throw new TimeoutException("ShaderFarm preparation timed out.");
                    }
                    Thread.Sleep(1000);
                    job = Call(pipeName, new Envelope
                    {
                        Kind = "GetJob", RequestId = Guid.NewGuid().ToString("N"), JobId = job.Id
                    }).Job;
                }
            }
            catch
            {
                try
                {
                    Call(pipeName, new Envelope
                    {
                        Kind = "CancelJob", RequestId = Guid.NewGuid().ToString("N"), JobId = job.Id
                    });
                }
                catch (Exception)
                {
                    // 主服务失联时由租约过期回收任务，不掩盖原始失败。
                }
                throw;
            }
        }

        private static Envelope Call(string name, Envelope request)
        {
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(5000);
                byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(request));
                if (payload.Length > MaxFrame)
                {
                    throw new InvalidDataException("IPC request too large.");
                }
                byte[] prefix =
                {
                    (byte)(payload.Length >> 24), (byte)(payload.Length >> 16),
                    (byte)(payload.Length >> 8), (byte)payload.Length
                };
                Write(pipe, prefix);
                Write(pipe, payload);
                prefix = Read(pipe, 4);
                uint length = ((uint)prefix[0] << 24) | ((uint)prefix[1] << 16) |
                    ((uint)prefix[2] << 8) | prefix[3];
                if (length == 0 || length > MaxFrame)
                {
                    throw new InvalidDataException("Invalid IPC response length.");
                }
                var result = JsonUtility.FromJson<Envelope>(new UTF8Encoding(false, true).GetString(Read(pipe, (int)length)));
                if (result == null || result.ProtocolVersion != 1 || result.RequestId != request.RequestId)
                {
                    throw new InvalidDataException("IPC response version or request mismatch.");
                }
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Error);
                }
                return result;
            }
        }

        private static void Write(Stream stream, byte[] bytes)
        {
            var pending = stream.WriteAsync(bytes, 0, bytes.Length);
            if (!pending.Wait(10000))
            {
                stream.Dispose();
                throw new TimeoutException("IPC write timed out.");
            }
            pending.GetAwaiter().GetResult();
        }

        private static byte[] Read(Stream stream, int size)
        {
            byte[] buffer = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                var pending = stream.ReadAsync(buffer, offset, size - offset);
                if (!pending.Wait(15000))
                {
                    stream.Dispose();
                    throw new TimeoutException("IPC read timed out.");
                }
                int read = pending.GetAwaiter().GetResult();
                if (read == 0)
                {
                    throw new EndOfStreamException("Master disconnected.");
                }
                offset += read;
            }
            return buffer;
        }

        [Serializable]
        private sealed class Envelope
        {
            public int ProtocolVersion = 1;
            public string Kind = "";
            public string RequestId = "";
            public string JobId = "";
            public bool Success = true;
            public string Error = "";
            public string ProjectCapability = "";
            public JobData Job;
        }

        [Serializable]
        internal sealed class JobData
        {
            public string Id = "";
            public string AttemptId = "";
            public string ProjectPath = "";
            public string UnityPath = "";
            public string Platform = "StandaloneWindows64";
            public string State = "";
            public string Error = "";
            public string LogPath = "";
            public string ResultPath = "";
            public int TimeoutSeconds = 43200;
            public int WorkerCount = 0;
            public string ValidationShader = "";
        }
    }
}
