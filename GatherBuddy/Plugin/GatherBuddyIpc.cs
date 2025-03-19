﻿using ECommons.EzIpcManager;
using System;
using System.Diagnostics.CodeAnalysis;

namespace GatherBuddy.Plugin;

public class GatherBuddyIpc : IDisposable
{
    public const int IpcVersion = 2;

    private readonly GatherBuddy _plugin;

    public GatherBuddyIpc(GatherBuddy plugin)
    {
        _plugin = plugin;
        EzIPC.Init(this, GatherBuddy.InternalName);
    }

#pragma warning disable CA1822 // Mark members as static
    [EzIPC]
    public int Version()
        => IpcVersion;

    [EzIPC]
    public uint Identify(string text)
        => _plugin.Executor.Identificator.IdentifyGatherable(text)?.ItemId
         ?? _plugin.Executor.Identificator.IdentifyFish(text)?.ItemId ?? 0;

    [EzIPC]
    public bool IsAutoGatherEnabled()
        => GatherBuddy.AutoGather.Enabled;

    [EzIPC]
    public string GetAutoGatherStatusText()
        => GatherBuddy.AutoGather.AutoStatus;

    [EzIPC]
    public void SetAutoGatherEnabled(bool enabled)
        => GatherBuddy.AutoGather.Enabled = enabled;

    [EzIPC]
    public bool IsAutoGatherWaiting()
        => GatherBuddy.AutoGather.Waiting;

    [EzIPCEvent]
    [AllowNull]
    public Action AutoGatherWaiting;

    [EzIPCEvent]
    [AllowNull]
    public Action<bool> AutoGatherEnabledChanged;

#pragma warning restore CA1822 // Mark members as static

    public void Dispose()
    {
        // EzIPC will handle disposal automatically through ECommonsMain.Dispose()
    }
}
