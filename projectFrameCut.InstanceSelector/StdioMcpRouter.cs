using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace projectFrameCut.InstanceSelector
{
    internal static class StdioMcpRouter
    {
        private const int RuntimeErrorExitCode = 1;
        private const int UsageErrorExitCode = 2;
        private const string PerRequestMetadataProtocolVersion = "2026-07-28";
        private const string ToolsChangedNotification = "notifications/tools/list_changed";
        private const string SubscriptionIdMetaKey =
            "io.modelcontextprotocol/subscriptionId";

        public static async Task<int> RunAsync(IReadOnlyList<string> args)
        {
            if (args.Any(static argument =>
                    string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)))
            {
                WriteHelp(Console.Error);
                return 0;
            }

            if (args.Count > 0)
            {
                Console.Error.WriteLine("stdio_mcp 不接受额外参数。");
                return UsageErrorExitCode;
            }

            using CancellationTokenSource cancellation = new();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                await using Stream rawInput = Console.OpenStandardInput();
                await using Stream rawOutput = Console.OpenStandardOutput();
                using BufferedStdioInputStream bufferedInput = new(rawInput);
                SelectorClientSession? clientSession = null;
                bool notifySelectorToolsChanged = false;

            while (!cancellation.IsCancellationRequested)
            {
                SelectorRunResult selectorResult = await RunSelectorAsync(
                        bufferedInput,
                        rawOutput,
                        clientSession,
                        notifySelectorToolsChanged,
                        cancellation.Token);
                    if (selectorResult.Handoff is null)
                    {
                        return 0;
                    }

                    clientSession = selectorResult.Handoff.ClientSession;
                    notifySelectorToolsChanged = false;
                    await using RawMcpSession targetSession = selectorResult.Handoff.TargetSession;

                    RouteEndReason routeEnd = await targetSession.RouteAsync(
                        bufferedInput,
                        rawOutput,
                        cancellation.Token);
                    if (routeEnd is RouteEndReason.ExternalDisconnected or RouteEndReason.Cancelled)
                    {
                        return 0;
                    }

                    notifySelectorToolsChanged = true;
                }

                return 0;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"stdio_mcp 服务器失败：{ex.Message}");
                return RuntimeErrorExitCode;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        private static async Task<SelectorRunResult> RunSelectorAsync(
            BufferedStdioInputStream rawInput,
            Stream rawOutput,
            SelectorClientSession? knownClientSession,
            bool notifyToolsChanged,
            CancellationToken cancellationToken)
        {
            using SelectorMcpHandoff handoff = new(
                knownClientSession?.ToolsSubscriptionIdJson,
                knownClientSession?.ToolsSubscriptionRequest);
            await using HandoffInputStream input = new(rawInput, handoff);
            await using HandoffOutputStream output = new(rawOutput, handoff);
            using ServiceProvider services = new ServiceCollection()
                .AddSingleton(handoff)
                .BuildServiceProvider();

            McpServerPrimitiveCollection<McpServerTool> tools =
                new(StringComparer.Ordinal);
            tools.Add(CreateSelectorTool(
                "list_projectframecut_instances",
                "列出当前用户已安装且可由 projectFrameCut CLI 启动的实例。",
                services,
                (Func<CancellationToken, ValueTask<CallToolResult>>)ListInstancesToolAsync));
            tools.Add(CreateSelectorTool(
                "start_projectframecut_instance",
                "启动指定的 projectFrameCut 实例，并将后续 MCP 请求切换到该实例。",
                services,
                (Func<string, string?, bool, bool, SelectorMcpHandoff, McpServer, CancellationToken, ValueTask<CallToolResult>>)StartInstanceToolAsync));

            McpServerOptions options = new()
            {
                ServerInfo = new Implementation
                {
                    Name = "projectFrameCut.InstanceSelector",
                    Version = "1.0.0"
                },
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability
                    {
                        ListChanged = true
                    }
                },
                ToolCollection = tools
            };
            if (knownClientSession is not null)
            {
                options.ProtocolVersion = knownClientSession.ProtocolVersion;
                options.KnownClientInfo = knownClientSession.ClientInfo;
                options.KnownClientCapabilities = knownClientSession.ClientCapabilities;
            }

            HandoffResult? result = null;
            try
            {
                using CancellationTokenSource selectorCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        handoff.HandoffCancellation);
                await using StreamServerTransport transport = new(
                    input,
                    output,
                    "projectFrameCut.InstanceSelector.Stdio",
                    loggerFactory: null);
#pragma warning disable MCPEXP002
                await using McpServer server = McpServer.Create(
                    transport,
                    options,
                    loggerFactory: null,
                    services);
#pragma warning restore MCPEXP002

                Task serverTask = server.RunAsync(selectorCancellation.Token);
                if (notifyToolsChanged)
                {
                    await SendToolsChangedAsync(
                        rawOutput,
                        knownClientSession,
                        cancellationToken);
                }

                try
                {
                    await serverTask;
                }
                catch (OperationCanceledException) when (handoff.IsCommitted)
                {
                }

                result = handoff.Result;
                if (result is not null && handoff.IsCommitted)
                {
                    return new SelectorRunResult(result);
                }

                return new SelectorRunResult(null);
            }
            finally
            {
                result ??= handoff.Result;
                if (result is not null && !handoff.IsCommitted)
                {
                    await result.TargetSession.DisposeAsync();
                }
            }
        }

        private static McpServerTool CreateSelectorTool(
            string name,
            string description,
            IServiceProvider services,
            Delegate handler) =>
            McpServerTool.Create(
                handler,
                new McpServerToolCreateOptions
                {
                    Services = services,
                    Name = name,
                    Description = description,
                    ReadOnly = string.Equals(
                        name,
                        "list_projectframecut_instances",
                        StringComparison.Ordinal)
                });

        private static async ValueTask<CallToolResult> ListInstancesToolAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                IReadOnlyList<ProjectFrameCutInstance> instances =
                    await new ProjectLaunchService().DiscoverInstancesAsync();
                string? defaultIdentifier = new SelectorSettingsStore().GetDefaultInstance();
                return ToolSuccess(SerializeInstances(instances, defaultIdentifier));
            }
            catch (Exception ex)
            {
                return ToolFailure($"无法查询已安装的 projectFrameCut 实例：{ex.Message}");
            }
        }

        private static string SerializeInstances(
            IReadOnlyList<ProjectFrameCutInstance> instances,
            string? defaultIdentifier)
        {
            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("instances");
                foreach (ProjectFrameCutInstance instance in instances)
                {
                    writer.WriteStartObject();
                    writer.WriteString("displayName", instance.DisplayName);
                    writer.WriteString("version", instance.Version);
                    writer.WriteString("packageIdentifier", instance.PackageIdentifier);
                    writer.WriteBoolean(
                        "isDefault",
                        string.Equals(
                            instance.PackageIdentifier,
                            defaultIdentifier,
                            StringComparison.OrdinalIgnoreCase));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static async ValueTask<CallToolResult> StartInstanceToolAsync(
            string packageIdentifier,
            string? projectRoot = null,
            bool headless = false,
            bool showConsoleForDebugging = false,
            SelectorMcpHandoff handoff = null!,
            McpServer server = null!,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(packageIdentifier))
            {
                return ToolFailure("packageIdentifier 必须是非空字符串。");
            }

            if (!handoff.TryBeginStart())
            {
                return ToolFailure("另一个 projectFrameCut 实例正在启动或切换中。");
            }

            RawMcpSession? targetSession = null;
            try
            {
                IReadOnlyList<ProjectFrameCutInstance> instances =
                    await new ProjectLaunchService().DiscoverInstancesAsync();
                ProjectFrameCutInstance? target = instances.FirstOrDefault(instance =>
                    string.Equals(
                        instance.PackageIdentifier,
                        packageIdentifier.Trim(),
                        StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    handoff.ResetStart();
                    return ToolFailure($"找不到指定的 projectFrameCut 实例“{packageIdentifier}”。");
                }

                SelectorClientSession clientSession = SelectorClientSession.FromServer(
                    server,
                    handoff.ToolsSubscriptionIdJson,
                    handoff.ToolsSubscriptionRequest);
                targetSession = await RawMcpSession.StartAsync(
                    target,
                    projectRoot,
                    headless,
                    showConsoleForDebugging,
                    clientSession,
                    cancellationToken);
                handoff.SetPending(new HandoffResult(targetSession, clientSession));
                targetSession = null;
                handoff.CommitWhenResponseIsFlushed();
                return ToolSuccess(
                    $"已启动 projectFrameCut 实例“{target.PackageIdentifier}”（版本 {target.Version}）。");
            }
            catch (Exception ex)
            {
                if (targetSession is not null)
                {
                    await targetSession.DisposeAsync();
                }

                handoff.ResetStart();
                return ToolFailure($"启动 projectFrameCut 实例失败：{ex.Message}");
            }
        }

        private static CallToolResult ToolSuccess(string text) => new()
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = false
        };

        private static CallToolResult ToolFailure(string text) => new()
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = true
        };

        private static async Task SendToolsChangedAsync(
            Stream rawOutput,
            SelectorClientSession? clientSession,
            CancellationToken cancellationToken)
        {
            ArrayBufferWriter<byte> buffer = new();
            string? toolsSubscriptionIdJson =
                clientSession?.ToolsSubscriptionIdJson;
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", ToolsChangedNotification);
                if (!string.IsNullOrWhiteSpace(toolsSubscriptionIdJson))
                {
                    writer.WriteStartObject("params");
                    writer.WriteStartObject("_meta");
                    writer.WritePropertyName(SubscriptionIdMetaKey);
                    using JsonDocument subscriptionId = JsonDocument.Parse(
                        toolsSubscriptionIdJson);
                    subscriptionId.RootElement.WriteTo(writer);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.Flush();
            }

            buffer.GetSpan(1)[0] = (byte)'\n';
            buffer.Advance(1);
            await rawOutput.WriteAsync(buffer.WrittenMemory, cancellationToken);
            await rawOutput.FlushAsync(cancellationToken);
        }

        private static void WriteHelp(TextWriter writer)
        {
            writer.WriteLine("stdio_mcp：通过 stdio 提供 projectFrameCut 实例选择和 MCP 转发服务。");
            writer.WriteLine("启动方式：pjfc stdio_mcp");
            writer.WriteLine("先调用 list_projectframecut_instances，再调用 start_projectframecut_instance。");
        }

        private sealed class SelectorMcpHandoff : IDisposable
        {
            private const int MaxObservedMessageBytes = 64 * 1024;
            private readonly CancellationTokenSource _handoffCancellation = new();
            private readonly MemoryStream _observedClientMessage = new();
            private readonly MemoryStream _observedServerMessage = new();
            private readonly object _clientObservationLock = new();
            private readonly object _serverObservationLock = new();
            private HandoffResult? _result;
            private string? _toolsSubscriptionIdJson;
            private byte[]? _toolsSubscriptionRequest;
            private string? _latestStartRequestIdJson;
            private string? _activeStartRequestIdJson;
            private bool _subscriptionObservedInThisPhase;
            private int _startState;
            private int _commitRequested;
            private int _responseObserved;
            private int _committed;

            public SelectorMcpHandoff(
                string? knownToolsSubscriptionIdJson,
                byte[]? knownToolsSubscriptionRequest)
            {
                _toolsSubscriptionIdJson = knownToolsSubscriptionIdJson;
                _toolsSubscriptionRequest = knownToolsSubscriptionRequest;
            }

            public HandoffResult? Result => Volatile.Read(ref _result);

            public bool IsCommitted => Volatile.Read(ref _committed) != 0;

            public CancellationToken HandoffCancellation => _handoffCancellation.Token;

            public string? ToolsSubscriptionIdJson =>
                Volatile.Read(ref _toolsSubscriptionIdJson);

            public byte[]? ToolsSubscriptionRequest =>
                Volatile.Read(ref _toolsSubscriptionRequest);

            public bool TryBeginStart()
            {
                if (Interlocked.CompareExchange(ref _startState, 1, 0) != 0)
                {
                    return false;
                }

                Volatile.Write(
                    ref _activeStartRequestIdJson,
                    Volatile.Read(ref _latestStartRequestIdJson));
                return true;
            }

            public void SetPending(HandoffResult result)
            {
                ArgumentNullException.ThrowIfNull(result);
                Volatile.Write(ref _result, result);
            }

            public void ResetStart()
            {
                Volatile.Write(ref _result, null);
                Volatile.Write(ref _activeStartRequestIdJson, null);
                Volatile.Write(ref _commitRequested, 0);
                Volatile.Write(ref _responseObserved, 0);
                Volatile.Write(ref _startState, 0);
            }

            public void CommitWhenResponseIsFlushed() =>
                Volatile.Write(ref _commitRequested, 1);

            public void ObserveClientInput(ReadOnlySpan<byte> bytes)
            {
                lock (_clientObservationLock)
                {
                    foreach (byte value in bytes)
                    {
                        if (value == (byte)'\n')
                        {
                            TryCaptureClientMessage();
                            _observedClientMessage.SetLength(0);
                            continue;
                        }

                        if (_observedClientMessage.Length < MaxObservedMessageBytes)
                        {
                            _observedClientMessage.WriteByte(value);
                        }
                        else
                        {
                            _observedClientMessage.SetLength(0);
                        }
                    }
                }
            }

            private void TryCaptureClientMessage()
            {
                if (_observedClientMessage.Length == 0)
                {
                    return;
                }

                try
                {
                    using JsonDocument message = JsonDocument.Parse(
                        _observedClientMessage.GetBuffer().AsMemory(
                            0,
                            checked((int)_observedClientMessage.Length)));
                    JsonElement root = message.RootElement;
                    if (!root.TryGetProperty("method", out JsonElement method))
                    {
                        return;
                    }

                    string? methodName = method.GetString();
                    if (!_subscriptionObservedInThisPhase &&
                        string.Equals(
                            methodName,
                            "subscriptions/listen",
                            StringComparison.Ordinal) &&
                        root.TryGetProperty("params", out JsonElement listenParameters) &&
                        listenParameters.TryGetProperty(
                            "notifications",
                            out JsonElement notifications) &&
                        notifications.TryGetProperty(
                            "toolsListChanged",
                            out JsonElement toolsListChanged) &&
                        toolsListChanged.ValueKind == JsonValueKind.True &&
                        root.TryGetProperty("id", out JsonElement subscriptionId))
                    {
                        Volatile.Write(
                            ref _toolsSubscriptionIdJson,
                            subscriptionId.GetRawText());
                        Volatile.Write(
                            ref _toolsSubscriptionRequest,
                            _observedClientMessage.ToArray());
                        _subscriptionObservedInThisPhase = true;
                    }

                    if (string.Equals(methodName, "tools/call", StringComparison.Ordinal) &&
                        root.TryGetProperty("params", out JsonElement callParameters) &&
                        callParameters.TryGetProperty("name", out JsonElement toolName) &&
                        string.Equals(
                            toolName.GetString(),
                            "start_projectframecut_instance",
                            StringComparison.Ordinal) &&
                        root.TryGetProperty("id", out JsonElement requestId))
                    {
                        Volatile.Write(
                            ref _latestStartRequestIdJson,
                            requestId.GetRawText());
                    }
                }
                catch (JsonException)
                {
                }
            }

            public bool ObserveServerOutput(ReadOnlySpan<byte> bytes)
            {
                lock (_serverObservationLock)
                {
                    foreach (byte value in bytes)
                    {
                        if (value == (byte)'\n')
                        {
                            if (IsActiveStartResponse())
                            {
                                Volatile.Write(ref _responseObserved, 1);
                            }

                            _observedServerMessage.SetLength(0);
                            continue;
                        }

                        if (_observedServerMessage.Length < MaxObservedMessageBytes)
                        {
                            _observedServerMessage.WriteByte(value);
                        }
                        else
                        {
                            _observedServerMessage.SetLength(0);
                        }
                    }
                }

                return Volatile.Read(ref _responseObserved) != 0;
            }

            private bool IsActiveStartResponse()
            {
                string? activeRequestIdJson =
                    Volatile.Read(ref _activeStartRequestIdJson);
                if (string.IsNullOrWhiteSpace(activeRequestIdJson) ||
                    _observedServerMessage.Length == 0)
                {
                    return false;
                }

                try
                {
                    using JsonDocument message = JsonDocument.Parse(
                        _observedServerMessage.GetBuffer().AsMemory(
                            0,
                            checked((int)_observedServerMessage.Length)));
                    JsonElement root = message.RootElement;
                    return root.TryGetProperty("id", out JsonElement responseId) &&
                        string.Equals(
                            responseId.GetRawText(),
                            activeRequestIdJson,
                            StringComparison.Ordinal) &&
                        (root.TryGetProperty("result", out _) ||
                            root.TryGetProperty("error", out _));
                }
                catch (JsonException)
                {
                    return false;
                }
            }

            public bool TryCommit()
            {
                if (Result is null ||
                    Volatile.Read(ref _commitRequested) == 0 ||
                    Volatile.Read(ref _responseObserved) == 0 ||
                    Interlocked.CompareExchange(ref _committed, 1, 0) != 0)
                {
                    return false;
                }

                _handoffCancellation.Cancel();
                return true;
            }

            public void Dispose()
            {
                _observedClientMessage.Dispose();
                _observedServerMessage.Dispose();
                _handoffCancellation.Dispose();
            }
        }

        private sealed class BufferedStdioInputStream : Stream
        {
            private const int BufferSize = 81920;
            private const int BufferedChunkLimit = 16;
            private readonly Stream _inner;
            private readonly Channel<InputChunk> _chunks;
            private readonly CancellationTokenSource _pumpCancellation = new();
            private readonly Stack<byte> _pushedBackBytes = new();
            private readonly object _pushbackLock = new();
            private byte[]? _currentBuffer;
            private int _currentOffset;
            private int _currentCount;
            private int _disposed;

            public BufferedStdioInputStream(Stream inner)
            {
                _inner = inner;
                _chunks = Channel.CreateBounded<InputChunk>(
                    new BoundedChannelOptions(BufferedChunkLimit)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = BoundedChannelFullMode.Wait
                    });
                _ = PumpAsync();
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count))
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);
                if (buffer.IsEmpty)
                {
                    return 0;
                }

                int pushedBackCount = ReadPushedBackBytes(buffer.Span);
                if (pushedBackCount > 0)
                {
                    return pushedBackCount;
                }

                while (true)
                {
                    if (_currentBuffer is not null &&
                        _currentOffset < _currentCount)
                    {
                        int count = Math.Min(
                            buffer.Length,
                            _currentCount - _currentOffset);
                        _currentBuffer.AsSpan(_currentOffset, count).CopyTo(buffer.Span);
                        _currentOffset += count;
                        if (_currentOffset == _currentCount)
                        {
                            _currentBuffer = null;
                            _currentOffset = 0;
                            _currentCount = 0;
                        }

                        return count;
                    }

                    try
                    {
                        if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                        {
                            return 0;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new IOException("读取标准输入失败。", ex);
                    }

                    if (_chunks.Reader.TryRead(out InputChunk chunk))
                    {
                        _currentBuffer = chunk.Buffer;
                        _currentOffset = 0;
                        _currentCount = chunk.Count;
                    }
                }
            }

            public void PushBack(ReadOnlySpan<byte> bytes)
            {
                lock (_pushbackLock)
                {
                    for (int index = bytes.Length - 1; index >= 0; index--)
                    {
                        _pushedBackBytes.Push(bytes[index]);
                    }
                }
            }

            private int ReadPushedBackBytes(Span<byte> destination)
            {
                lock (_pushbackLock)
                {
                    int count = 0;
                    while (count < destination.Length &&
                        _pushedBackBytes.TryPop(out byte value))
                    {
                        destination[count++] = value;
                    }

                    return count;
                }
            }

            private async Task PumpAsync()
            {
                try
                {
                    while (true)
                    {
                        byte[] buffer = new byte[BufferSize];
                        int read = await _inner.ReadAsync(
                            buffer,
                            _pumpCancellation.Token);
                        if (read == 0)
                        {
                            _chunks.Writer.TryComplete();
                            return;
                        }

                        await _chunks.Writer.WriteAsync(
                            new InputChunk(buffer, read),
                            _pumpCancellation.Token);
                    }
                }
                catch (OperationCanceledException)
                    when (_pumpCancellation.IsCancellationRequested)
                {
                    _chunks.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    _chunks.Writer.TryComplete(ex);
                }
            }

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _pumpCancellation.Cancel();
                    _chunks.Writer.TryComplete();
                }

                base.Dispose(disposing);
            }

            private readonly record struct InputChunk(byte[] Buffer, int Count);
        }

        private sealed class HandoffInputStream : Stream
        {
            private readonly BufferedStdioInputStream _inner;
            private readonly SelectorMcpHandoff _handoff;

            public HandoffInputStream(
                BufferedStdioInputStream inner,
                SelectorMcpHandoff handoff)
            {
                _inner = inner;
                _handoff = handoff;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count))
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (_handoff.IsCommitted)
                {
                    return 0;
                }

                using CancellationTokenSource linkedCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        _handoff.HandoffCancellation);
                try
                {
                    int read = await _inner.ReadAsync(
                        buffer.Slice(0, Math.Min(buffer.Length, 1)),
                        linkedCancellation.Token);
                    if (_handoff.IsCommitted)
                    {
                        _inner.PushBack(buffer.Span[..read]);
                        return 0;
                    }

                    _handoff.ObserveClientInput(buffer.Span[..read]);
                    if (_handoff.IsCommitted)
                    {
                        _inner.PushBack(buffer.Span[..read]);
                        return 0;
                    }

                    return read;
                }
                catch (OperationCanceledException) when (_handoff.IsCommitted)
                {
                    return 0;
                }
            }

            protected override void Dispose(bool disposing)
            {
            }
        }

        private sealed class HandoffOutputStream : Stream
        {
            private readonly Stream _inner;
            private readonly SelectorMcpHandoff _handoff;

            public HandoffOutputStream(Stream inner, SelectorMcpHandoff handoff)
            {
                _inner = inner;
                _handoff = handoff;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
                _inner.Flush();
                _handoff.TryCommit();
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                _handoff.ObserveServerOutput(buffer.AsSpan(offset, count));
            }

            public override async Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                await _inner.WriteAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken);
                _handoff.ObserveServerOutput(buffer.AsSpan(offset, count));
            }

            public override async ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                await _inner.WriteAsync(buffer, cancellationToken);
                _handoff.ObserveServerOutput(buffer.Span);
            }

            public override async Task FlushAsync(CancellationToken cancellationToken)
            {
                await _inner.FlushAsync(cancellationToken);
                _handoff.TryCommit();
            }

            protected override void Dispose(bool disposing)
            {
            }
        }

        private sealed class RawMcpSession : IAsyncDisposable
        {
            private const int CopyBufferSize = 81920;
            private const int MaxHandshakeFrameBytes = 4 * 1024 * 1024;
            private static readonly byte[] Newline = [(byte)'\n'];
            private readonly NamedPipeClientStream _pipe;
            private readonly Process _process;
            private readonly SelectorClientSession _clientSession;
            private readonly List<byte[]> _pendingTargetFrames = [];

            private RawMcpSession(
                NamedPipeClientStream pipe,
                Process process,
                SelectorClientSession clientSession)
            {
                _pipe = pipe;
                _process = process;
                _clientSession = clientSession;
            }

            public static async Task<RawMcpSession> StartAsync(
                ProjectFrameCutInstance instance,
                string? projectRoot,
                bool headless,
                bool showConsoleForDebugging,
                SelectorClientSession clientSession,
                CancellationToken cancellationToken)
            {
                string pipeName = $"projectFrameCut-selector-mcp-{Guid.NewGuid():N}";
                NamedPipeClientStream pipe = new(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                Process? process = null;
                try
                {
                    ProcessStartInfo startInfo = new(instance.CliExecutablePath)
                    {
                        UseShellExecute = showConsoleForDebugging,
                        CreateNoWindow = !showConsoleForDebugging,
                        WorkingDirectory = Environment.CurrentDirectory
                    };
                    startInfo.ArgumentList.Add("mcp");
                    startInfo.ArgumentList.Add("--transport=raw_pipe");
                    startInfo.ArgumentList.Add($"--pipe={pipeName}");
                    startInfo.ArgumentList.Add($"--parentPid={Process.GetCurrentProcess().Id}");
                    if (!string.IsNullOrWhiteSpace(projectRoot))
                    {
                        startInfo.ArgumentList.Add($"--projectRoot={projectRoot}");
                    }

                    if (headless)
                    {
                        startInfo.ArgumentList.Add("--headless");
                    }

                    if (showConsoleForDebugging)
                    {
                        startInfo.ArgumentList.Add("--consoleLog");
                    }

                    process = Process.Start(startInfo) ??
                        throw new InvalidOperationException("目标 CLI 进程未能启动。");

                    Task connectionTask = pipe.ConnectAsync(cancellationToken);
                    Task exitTask = process.WaitForExitAsync(cancellationToken);
                    Task completed = await Task.WhenAny(connectionTask, exitTask);
                    if (ReferenceEquals(completed, exitTask) && !pipe.IsConnected)
                    {
                        await exitTask;
                        throw new InvalidOperationException(
                            $"目标 CLI 进程在建立 MCP 管道连接前退出，退出码为 {process.ExitCode}。");
                    }

                    await connectionTask;
                    RawMcpSession session = new(pipe, process, clientSession);
                    await session.InitializeTargetAsync(clientSession, cancellationToken);
                    return session;
                }
                catch
                {
                    try { pipe.Dispose(); } catch { }
                    if (process is not null)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill(entireProcessTree: true);
                            }
                        }
                        catch { }

                        process.Dispose();
                    }

                    throw;
                }
            }

            private async Task InitializeTargetAsync(
                SelectorClientSession clientSession,
                CancellationToken cancellationToken)
            {
                string requestId = $"projectFrameCut-selector-{Guid.NewGuid():N}";
                bool usesPerRequestMetadata =
                    string.Compare(
                        clientSession.ProtocolVersion,
                        PerRequestMetadataProtocolVersion,
                        StringComparison.Ordinal) >= 0;
                if (usesPerRequestMetadata)
                {
                    await RestoreTargetSubscriptionAsync(
                        clientSession,
                        cancellationToken);
                    return;
                }

                await WriteTargetFrameAsync(
                    SerializeInitializeRequest(requestId, clientSession),
                    cancellationToken);

                while (true)
                {
                    byte[] frame = await ReadTargetFrameAsync(cancellationToken);
                    using JsonDocument message = JsonDocument.Parse(frame);
                    JsonElement root = message.RootElement;
                    if (!root.TryGetProperty("id", out JsonElement responseId) ||
                        responseId.ValueKind != JsonValueKind.String ||
                        !string.Equals(
                            responseId.GetString(),
                            requestId,
                            StringComparison.Ordinal))
                    {
                        _pendingTargetFrames.Add(frame);
                        continue;
                    }

                    if (root.TryGetProperty("error", out JsonElement error))
                    {
                        string errorMessage =
                            error.TryGetProperty("message", out JsonElement messageText)
                                ? messageText.GetString() ?? "未知 MCP 错误"
                                : "未知 MCP 错误";
                        throw new InvalidOperationException(
                            $"目标 MCP 握手失败：{errorMessage}");
                    }

                    if (!root.TryGetProperty("result", out JsonElement result))
                    {
                        throw new InvalidDataException("目标 MCP 握手响应缺少 result。");
                    }

                    if (!result.TryGetProperty(
                            "protocolVersion",
                            out JsonElement protocolVersion) ||
                        !string.Equals(
                            protocolVersion.GetString(),
                            clientSession.ProtocolVersion,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"目标 MCP 未接受协议版本 {clientSession.ProtocolVersion}。");
                    }

                    await WriteTargetFrameAsync(
                        SerializeInitializedNotification(),
                        cancellationToken);

                    return;
                }
            }

            private static byte[] SerializeInitializeRequest(
                string requestId,
                SelectorClientSession clientSession)
            {
                ArrayBufferWriter<byte> buffer = new();
                using (Utf8JsonWriter writer = new(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WriteString("id", requestId);
                    writer.WriteString("method", "initialize");
                    writer.WritePropertyName("params");
                    JsonSerializer.Serialize(
                        writer,
                        new InitializeRequestParams
                        {
                            ProtocolVersion = clientSession.ProtocolVersion,
                            Capabilities = clientSession.ClientCapabilities,
                            ClientInfo = clientSession.ClientInfo
                        },
                        McpJsonUtilities.GetTypeInfo<InitializeRequestParams>(
                            McpJsonUtilities.DefaultOptions));
                    writer.WriteEndObject();
                }

                return buffer.WrittenMemory.ToArray();
            }

            private static byte[] SerializeInitializedNotification()
            {
                ArrayBufferWriter<byte> buffer = new();
                using (Utf8JsonWriter writer = new(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WriteString("method", "notifications/initialized");
                    writer.WriteStartObject("params");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                return buffer.WrittenMemory.ToArray();
            }

            private async Task RestoreTargetSubscriptionAsync(
                SelectorClientSession clientSession,
                CancellationToken cancellationToken)
            {
                byte[]? request = clientSession.ToolsSubscriptionRequest;
                string? subscriptionIdJson =
                    clientSession.ToolsSubscriptionIdJson;
                if (request is null || string.IsNullOrWhiteSpace(subscriptionIdJson))
                {
                    return;
                }

                await WriteTargetFrameAsync(request, cancellationToken);
                while (true)
                {
                    byte[] frame = await ReadTargetFrameAsync(cancellationToken);
                    using JsonDocument message = JsonDocument.Parse(frame);
                    JsonElement root = message.RootElement;
                    if (TryReadSubscriptionAcknowledgement(
                            root,
                            subscriptionIdJson,
                            out bool acceptsToolsListChanged))
                    {
                        if (!acceptsToolsListChanged)
                        {
                            throw new InvalidOperationException(
                                "目标 MCP 未接受工具列表更新订阅。");
                        }

                        return;
                    }

                    if (root.TryGetProperty("id", out JsonElement responseId) &&
                        JsonValueEquals(responseId, subscriptionIdJson))
                    {
                        string errorMessage = root.TryGetProperty(
                                "error",
                                out JsonElement error) &&
                            error.TryGetProperty(
                                "message",
                                out JsonElement messageText)
                                ? messageText.GetString() ?? "未知 MCP 错误"
                                : "订阅请求提前结束";
                        throw new InvalidOperationException(
                            $"目标 MCP 工具更新订阅失败：{errorMessage}");
                    }

                    _pendingTargetFrames.Add(frame);
                }
            }

            private static bool TryReadSubscriptionAcknowledgement(
                JsonElement root,
                string subscriptionIdJson,
                out bool acceptsToolsListChanged)
            {
                acceptsToolsListChanged = false;
                if (!root.TryGetProperty("method", out JsonElement method) ||
                    !string.Equals(
                        method.GetString(),
                        "notifications/subscriptions/acknowledged",
                        StringComparison.Ordinal) ||
                    !root.TryGetProperty("params", out JsonElement parameters) ||
                    !parameters.TryGetProperty("_meta", out JsonElement metadata) ||
                    !metadata.TryGetProperty(
                        SubscriptionIdMetaKey,
                        out JsonElement subscriptionId) ||
                    !JsonValueEquals(subscriptionId, subscriptionIdJson))
                {
                    return false;
                }

                acceptsToolsListChanged =
                    parameters.TryGetProperty(
                        "notifications",
                        out JsonElement notifications) &&
                    notifications.TryGetProperty(
                        "toolsListChanged",
                        out JsonElement toolsListChanged) &&
                    toolsListChanged.ValueKind == JsonValueKind.True;
                return true;
            }

            private static bool JsonValueEquals(
                JsonElement value,
                string expectedJson)
            {
                using JsonDocument expected = JsonDocument.Parse(expectedJson);
                return JsonElement.DeepEquals(value, expected.RootElement);
            }

            private async Task WriteTargetFrameAsync(
                ReadOnlyMemory<byte> frame,
                CancellationToken cancellationToken)
            {
                await _pipe.WriteAsync(frame, cancellationToken);
                await _pipe.WriteAsync(Newline, cancellationToken);
                await _pipe.FlushAsync(cancellationToken);
            }

            private async Task<byte[]> ReadTargetFrameAsync(
                CancellationToken cancellationToken)
            {
                ArrayBufferWriter<byte> frame = new();
                byte[] singleByte = new byte[1];
                while (true)
                {
                    int read = await _pipe.ReadAsync(singleByte, cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "目标 MCP 在握手完成前关闭了命名管道。");
                    }

                    if (frame.WrittenCount >= MaxHandshakeFrameBytes)
                    {
                        throw new InvalidDataException("目标 MCP 握手响应过大。");
                    }

                    frame.GetSpan(1)[0] = singleByte[0];
                    frame.Advance(1);
                    if (singleByte[0] == (byte)'\n')
                    {
                        if (frame.WrittenCount == 1)
                        {
                            frame = new ArrayBufferWriter<byte>();
                            continue;
                        }

                        return frame.WrittenMemory.ToArray();
                    }
                }
            }

            public async Task<RouteEndReason> RouteAsync(
                Stream clientInput,
                Stream clientOutput,
                CancellationToken cancellationToken)
            {
                using CancellationTokenSource routeCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<RouteEndReason> clientToTarget = RouteClientToTargetAsync(
                    clientInput,
                    routeCancellation.Token);
                Task<RouteEndReason> targetExit = WaitForTargetExitAsync(
                    routeCancellation.Token);
                Task<RouteEndReason> targetToClient = RouteTargetToClientAsync(
                    clientOutput,
                    routeCancellation.Token);

                Task<RouteEndReason> completed = await Task.WhenAny(
                    clientToTarget,
                    targetToClient,
                    targetExit);
                RouteEndReason reason = await completed;
                routeCancellation.Cancel();
                try { _pipe.Dispose(); } catch { }

                await IgnoreExpectedRouteCompletionAsync(clientToTarget);
                await IgnoreExpectedRouteCompletionAsync(targetToClient);
                await IgnoreExpectedRouteCompletionAsync(targetExit);
                return cancellationToken.IsCancellationRequested
                    ? RouteEndReason.Cancelled
                    : reason;
            }

            private async Task<RouteEndReason> RouteClientToTargetAsync(
                Stream clientInput,
                CancellationToken cancellationToken)
            {
                byte[] buffer = new byte[CopyBufferSize];
                while (true)
                {
                    int read;
                    try
                    {
                        read = await clientInput.ReadAsync(buffer, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return RouteEndReason.Cancelled;
                    }
                    catch (IOException)
                    {
                        return RouteEndReason.ExternalDisconnected;
                    }
                    catch (ObjectDisposedException)
                    {
                        return cancellationToken.IsCancellationRequested
                            ? RouteEndReason.Cancelled
                            : RouteEndReason.ExternalDisconnected;
                    }

                    if (read == 0)
                    {
                        return RouteEndReason.ExternalDisconnected;
                    }

                    try
                    {
                        await _pipe.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        await _pipe.FlushAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return RouteEndReason.Cancelled;
                    }
                    catch (IOException)
                    {
                        return RouteEndReason.TargetDisconnected;
                    }
                    catch (ObjectDisposedException)
                    {
                        return cancellationToken.IsCancellationRequested
                            ? RouteEndReason.Cancelled
                            : RouteEndReason.TargetDisconnected;
                    }
                }
            }

            private async Task<RouteEndReason> RouteTargetToClientAsync(
                Stream clientOutput,
                CancellationToken cancellationToken)
            {
                try
                {
                    await SendToolsChangedAsync(
                        clientOutput,
                        _clientSession,
                        cancellationToken);
                    foreach (byte[] frame in _pendingTargetFrames)
                    {
                        await clientOutput.WriteAsync(frame, cancellationToken);
                    }

                    if (_pendingTargetFrames.Count > 0)
                    {
                        await clientOutput.FlushAsync(cancellationToken);
                        _pendingTargetFrames.Clear();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return RouteEndReason.Cancelled;
                }
                catch (IOException)
                {
                    return RouteEndReason.ExternalDisconnected;
                }
                catch (ObjectDisposedException)
                {
                    return cancellationToken.IsCancellationRequested
                        ? RouteEndReason.Cancelled
                        : RouteEndReason.ExternalDisconnected;
                }

                byte[] buffer = new byte[CopyBufferSize];
                while (true)
                {
                    int read;
                    try
                    {
                        read = await _pipe.ReadAsync(buffer, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return RouteEndReason.Cancelled;
                    }
                    catch (IOException)
                    {
                        return RouteEndReason.TargetDisconnected;
                    }
                    catch (ObjectDisposedException)
                    {
                        return cancellationToken.IsCancellationRequested
                            ? RouteEndReason.Cancelled
                            : RouteEndReason.TargetDisconnected;
                    }

                    if (read == 0)
                    {
                        return RouteEndReason.TargetDisconnected;
                    }

                    try
                    {
                        await clientOutput.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        await clientOutput.FlushAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return RouteEndReason.Cancelled;
                    }
                    catch (IOException)
                    {
                        return RouteEndReason.ExternalDisconnected;
                    }
                    catch (ObjectDisposedException)
                    {
                        return cancellationToken.IsCancellationRequested
                            ? RouteEndReason.Cancelled
                            : RouteEndReason.ExternalDisconnected;
                    }
                }
            }

            private async Task<RouteEndReason> WaitForTargetExitAsync(
                CancellationToken cancellationToken)
            {
                try
                {
                    await _process.WaitForExitAsync(cancellationToken);
                    return RouteEndReason.TargetDisconnected;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return RouteEndReason.Cancelled;
                }
            }

            private static async Task IgnoreExpectedRouteCompletionAsync(
                Task<RouteEndReason> task)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public async ValueTask DisposeAsync()
            {
                try { _pipe.Dispose(); } catch { }
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch { }

                try { await _process.WaitForExitAsync(); } catch { }
                _process.Dispose();
            }
        }

        private sealed record SelectorClientSession(
            Implementation ClientInfo,
            ClientCapabilities ClientCapabilities,
            string ProtocolVersion,
            string? ToolsSubscriptionIdJson,
            byte[]? ToolsSubscriptionRequest)
        {
            public static SelectorClientSession FromServer(
                McpServer server,
                string? toolsSubscriptionIdJson,
                byte[]? toolsSubscriptionRequest) => new(
                server.ClientInfo ??
                    throw new InvalidOperationException("外部 MCP 客户端尚未完成初始化。"),
                server.ClientCapabilities ??
                    throw new InvalidOperationException("外部 MCP 客户端能力尚不可用。"),
                server.NegotiatedProtocolVersion ??
                    throw new InvalidOperationException("外部 MCP 协议版本尚未协商。"),
                toolsSubscriptionIdJson,
                toolsSubscriptionRequest);
        }

        private sealed record HandoffResult(
            RawMcpSession TargetSession,
            SelectorClientSession ClientSession);

        private sealed record SelectorRunResult(HandoffResult? Handoff);

        private enum RouteEndReason
        {
            ExternalDisconnected,
            TargetDisconnected,
            Cancelled
        }
    }
}
