using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace SamplePlugin.Repair;

/// <summary>
/// Thin wrapper around vnavmesh's IPC surface (https://github.com/awgil/ffxiv_navmesh).
/// Dalamud itself has no built-in pathfinding, so auto-walking to a repair NPC depends on the
/// vnavmesh plugin being installed. The exact IPC method names below match vnavmesh's published
/// IPC.md at the time this was written; if a future vnavmesh release renames them, only this file
/// needs updating.
/// </summary>
public sealed class VNavmeshIpc
{
    private readonly ICallGateSubscriber<bool> navIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;

    public VNavmeshIpc()
    {
        navIsReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathfindAndMoveTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathIsRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    /// <summary>Whether the vnavmesh plugin is installed and responding at all.</summary>
    public bool IsAvailable()
    {
        try
        {
            navIsReady.InvokeFunc();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether the navmesh for the current zone has finished building.</summary>
    public bool IsNavReady()
    {
        try
        {
            return navIsReady.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Kicks off (or replaces) a path toward the destination. Returns false if vnavmesh refused.</summary>
    public bool MoveTo(Vector3 destination, bool fly = false)
    {
        try
        {
            return pathfindAndMoveTo.InvokeFunc(destination, fly);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "vnavmesh MoveTo call failed");
            return false;
        }
    }

    public bool IsPathRunning()
    {
        try
        {
            return pathIsRunning.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            pathStop.InvokeAction();
        }
        catch
        {
            // vnavmesh not present, nothing to stop
        }
    }
}
