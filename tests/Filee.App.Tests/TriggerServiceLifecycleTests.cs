// Verifies that retrying gestures waits for the previous native hook to release its shared callback.

using Avalonia.Headless.XUnit;
using Filee.App.Services.Triggers;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using SharpHook;
using SharpHook.Data;
using SharpHook.Providers;

namespace Filee.App.Tests;

public sealed class TriggerServiceLifecycleTests
{
    [AvaloniaFact]
    public async Task Retry_waits_for_old_native_cleanup_and_keeps_new_callback()
    {
        var provider = new DelayedProvider();
        var keys = 0;
        using var service = Create(provider, () => Interlocked.Increment(ref keys));
        service.Start();
        Assert.True(SpinWait.SpinUntil(() => provider.Runs == 1, 2000));
        var cancellation = TestContext.Current.CancellationToken;
        var stopping = Task.Factory.StartNew(service.Dispose, cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(provider.StopRequested.Wait(2000, cancellation));
            Assert.NotSame(stopping, await Task.WhenAny(stopping, Task.Delay(75, cancellation)));
        }
        finally
        {
            provider.AllowExit.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2), cancellation);
        }
        Assert.False(provider.HasCallback);
        service.Start();
        Assert.True(SpinWait.SpinUntil(() => provider.Runs == 2, 2000));
        provider.PressKey();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref keys) == 1, 2000));
        Assert.True(service.IsRunning);
        Assert.True(provider.HasCallback);
    }

    [AvaloniaFact]
    public void Repeated_retries_keep_delivering_input()
    {
        var provider = new DelayedProvider();
        provider.AllowExit.Set();
        var keys = 0;
        using var service = Create(provider, () => Interlocked.Increment(ref keys));
        for (var i = 1; i <= 30; i++)
        {
            service.Start();
            Assert.True(SpinWait.SpinUntil(() => provider.Runs == i, 2000));
            provider.PressKey();
            var expected = i;
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref keys) == expected, 2000));
            service.Dispose();
        }
        Assert.Equal(30, keys);
    }

    [AvaloniaFact]
    public void Failed_stop_does_not_start_a_second_hook()
    {
        var provider = new DelayedProvider { FailStop = true };
        provider.AllowExit.Set();
        using var service = Create(provider, () => { });
        service.Start();
        Assert.True(SpinWait.SpinUntil(() => provider.Runs == 1, 2000));
        try
        {
            service.Dispose();
            Assert.NotNull(service.Error);
            service.Start();
            Assert.Equal(1, provider.Runs);
        }
        finally
        {
            provider.StopRequested.Set();
            service.Dispose();
        }
    }

    private static TriggerService Create(DelayedProvider provider, Action key) =>
        new(new NullPlatformServicesForHooks(), NullLogger<TriggerService>.Instance, () =>
        {
            var hook = new EventLoopGlobalHook(provider, useBackgroundThreadForEventLoop: true);
            hook.KeyPressed += (_, _) => key();
            return hook;
        });

    // Models libuiohook's process-wide callback and asynchronous native shutdown. No OS input is hooked.
    private sealed class DelayedProvider : IGlobalHookProvider
    {
        private readonly object _gate = new();
        private DispatchProc? _callback;
        private nint _data;
        private int _runs;
        public readonly ManualResetEventSlim StopRequested = new();
        public readonly ManualResetEventSlim AllowExit = new();
        public bool FailStop;
        public bool KeyTypedEnabled { get; set; }
        public int Runs => Volatile.Read(ref _runs);
        public bool HasCallback { get { lock (_gate) return _callback is not null; } }

        public void SetDispatchProc(DispatchProc? callback, nint data)
        {
            lock (_gate) { _callback = callback; _data = data; }
        }
        public UioHookResult Run()
        {
            StopRequested.Reset();
            Interlocked.Increment(ref _runs);
            StopRequested.Wait();
            AllowExit.Wait();
            return UioHookResult.Success;
        }
        public UioHookResult RunKeyboard() => Run();
        public UioHookResult RunMouse() => Run();
        public UioHookResult Stop()
        {
            if (FailStop) return UioHookResult.Failure;
            StopRequested.Set();
            return UioHookResult.Success;
        }
        public void PressKey()
        {
            lock (_gate)
            {
                var e = new UioHookEvent { Type = EventType.KeyPressed, Keyboard = new KeyboardEventData { KeyCode = KeyCode.VcA } };
                _callback?.Invoke(ref e, _data);
            }
        }
    }

    private sealed class NullPlatformServicesForHooks : IPlatformServices
    {
        public bool IsFileManagerAt(int x, int y) => true;
        public string? ProcessNameAt(int x, int y) => "explorer";
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) => Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
    }
}
