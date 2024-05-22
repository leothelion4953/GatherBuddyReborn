﻿using ClickLib.Structures;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Classes;
using GatherBuddy.Interfaces;
using GatherBuddy.Utility;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets2;
using OtterGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJIFarmManagement;

namespace GatherBuddy.Plugin
{
    public class AutoGather
    {
        public enum AutoStateType
        {
            Idle,
            WaitingForTeleport,
            Pathing,
            WaitingForNavmesh,
            GatheringNode,
            MovingToNode,
            Mounting,
            Dismounting,
            Error,
            Finish,
        }
        private readonly GatherBuddy _plugin;
        public string AutoStatus { get; set; } = "Not Running";
        public AutoStateType AutoState { get; set; } = AutoStateType.Idle;
        private AutoStateType _lastAutoState = AutoStateType.Idle;
        public AutoGather(GatherBuddy plugin)
        {
            _plugin = plugin;
        }

        private DateTime _teleportInitiated = DateTime.MinValue;

        public IEnumerable<GameObject> ValidGatherables = new List<GameObject>();

        public Gatherable? DesiredItem => _plugin.GatherWindowManager.ActiveItems.FirstOrDefault() as Gatherable;
        public bool IsPathing => VNavmesh_IPCSubscriber.Path_IsRunning();
        public bool NavReady => VNavmesh_IPCSubscriber.Nav_IsReady();

        private void UpdateObjects()
        {
            ValidGatherables = Dalamud.ObjectTable.Where(g => g.ObjectKind == ObjectKind.GatheringPoint)
                        .Where(g => g.IsTargetable)
                        .Where(IsDesiredNode)
                        .OrderBy(g => Vector3.Distance(g.Position, Dalamud.ClientState.LocalPlayer.Position));

        }
        public void DoAutoGather()
        {
            if (!GatherBuddy.Config.AutoGather) return;
            NavmeshStuckCheck();
            InventoryCheck();

            UpdateObjects();
            DetermineAutoState();
        }
        private unsafe static Vector2? GetFlagPosition()
        {
            var map = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
            if (map == null || map->IsFlagMarkerSet == 0)
                return null;
            var marker = map->FlagMapMarker;
            return new(marker.XFloat, marker.YFloat);
        }
        private unsafe void PathfindToFlag()
        {
            var flagPosition = GetFlagPosition();
            if (flagPosition == null)
                return;
            var vector3 = new Vector3(flagPosition.Value.X, 1024, flagPosition.Value.Y);
            var nearestPoint = VNavmesh_IPCSubscriber.Query_Mesh_NearestPoint(vector3, 0, 0);
            if (nearestPoint == null)
                return;
            if (Vector3.Distance(nearestPoint, Dalamud.ClientState.LocalPlayer.Position) < 100)
            {
                AutoStatus = "We're close to the area but no nodes are available.";
                return;
            }
            else
            {
                AutoStatus = "Pathing to flag...";
                PathfindToNode(nearestPoint);
            }
        }
        private void PathfindToFarNode(Gatherable desiredItem)
        {
            if (desiredItem == null)
                return;

            var nodeList = desiredItem.NodeList;
            if (nodeList == null)
                return;

            var currentPosition = Dalamud.ClientState.LocalPlayer.Position;
            var coordList = nodeList.Where(n => n.Territory.Id == Dalamud.ClientState.TerritoryType)
                                    .SelectMany(n => n.WorldCoords)
                                    .SelectMany(w => w.Value)
                                    .OrderBy(n => Vector3.Distance(n, currentPosition))
                                    .ToList();

            var closestKnownNode = coordList.FirstOrDefault();
            if (closestKnownNode == null)
                return;

            // If the closest node is too close, filter out close nodes and select a random node from the rest
            if (Vector3.Distance(closestKnownNode, currentPosition) < 30)
            {
                var farNodes = coordList.Where(n => Vector3.Distance(n, currentPosition) >= 150).ToList();

                if (farNodes.Any())
                {
                    var random = new Random();
                    var randomNode = farNodes[random.Next(farNodes.Count)];
                    closestKnownNode = randomNode;
                    AutoStatus = "Pathing to a farther node...";
                    PathfindToNode(closestKnownNode);
                }
                else
                {
                    VNavmesh_IPCSubscriber.Path_Stop();
                    AutoStatus = "No suitable nodes found to path.";
                    // You can add additional logic here if needed when no suitable nodes are found
                    return;
                }
            }
            else
            {
                AutoStatus = "Pathing to the closest known node...";
                PathfindToNode(closestKnownNode);
            }
        }
        private void PathfindToNode(Vector3 position)
        {
            if (IsPathing)
                return;
            VNavmesh_IPCSubscriber.SimpleMove_PathfindAndMoveTo(position, true);
        }

        private void DetermineAutoState()
        {
            if (!NavReady)
            {
                AutoState = AutoStateType.WaitingForNavmesh;
                AutoStatus = "Waiting for Navmesh...";
                return;
            }

            if (IsPlayerBusy())
            {
                AutoState = AutoStateType.Idle;
                AutoStatus = "Player is busy...";
                return;
            }

            if (DesiredItem == null)
            {
                AutoState = AutoStateType.Finish;
                AutoStatus = "No active items in shopping list...";
                return;
            }

            var currentTerritory = Dalamud.ClientState.TerritoryType;

            if (!ValidGatherables.Any())
            {
                var location = _plugin.Executor.FindClosestLocation(DesiredItem);
                if (location == null)
                {
                    AutoState = AutoStateType.Error;
                    AutoStatus = "No locations for item " + DesiredItem.Name[GatherBuddy.Language] + ".";
                    return;
                }

                if (location.Territory.Id != currentTerritory)
                {
                    if (_teleportInitiated < DateTime.Now)
                    {
                        AutoState = AutoStateType.WaitingForTeleport;
                        AutoStatus = "Teleporting to " + location.Territory.Name + "...";
                        if (IsPathing)
                            VNavmesh_IPCSubscriber.Path_Stop();
                        else
                        {
                            _teleportInitiated = DateTime.Now.AddSeconds(15);
                            _plugin.Executor.GatherItem(DesiredItem);
                        }
                        return;
                    }
                    else
                    {
                        AutoState = AutoStateType.WaitingForTeleport;
                        AutoStatus = "Waiting for teleport...";
                        return;
                    }
                }

                if (!Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    AutoState = AutoStateType.Mounting;
                    AutoStatus = "Mounting for travel...";
                    MountUp();
                    return;
                }

                AutoState = AutoStateType.Pathing;
                PathfindToFarNode(DesiredItem);
                return;
            }

            if (ValidGatherables.Any())
            {
                var targetGatherable = ValidGatherables.First();
                var distance = Vector3.Distance(targetGatherable.Position, Dalamud.ClientState.LocalPlayer.Position);

                if (distance < 2.5)
                {
                    if (Dalamud.Conditions[ConditionFlag.Mounted])
                    {
                        AutoState = AutoStateType.Dismounting;
                        AutoStatus = "Dismounting...";
                        Dismount();
                        return;
                    }
                    else if (Dalamud.Conditions[ConditionFlag.Gathering])
                    {
                        // This is where you can handle additional logic when close to the node without being mounted.
                        AutoState = AutoStateType.GatheringNode;
                        AutoStatus = $"Gathering {targetGatherable.Name}...";
                        GatherNode();
                        return;
                    }
                    else
                    {
                        AutoState = AutoStateType.GatheringNode;
                        AutoStatus = $"Targeting {targetGatherable.Name}...";
                        InteractNode(targetGatherable);
                        return;
                    }
                }
                else
                {
                    if (!Dalamud.Conditions[ConditionFlag.Mounted])
                    {
                        AutoState = AutoStateType.Mounting;
                        AutoStatus = "Mounting for travel...";
                        MountUp();
                        return;
                    }

                    if (AutoState != AutoStateType.MovingToNode)
                    {
                        _hiddenRevealed = false;
                        AutoState = AutoStateType.MovingToNode;
                        AutoStatus = $"Moving to node {targetGatherable.Name} at {targetGatherable.Position}";
                        PathfindToNode(targetGatherable.Position);
                        return;
                    }
                }
            }

            AutoState = AutoStateType.Error;
            //AutoStatus = "Nothing to do...";
        }

        private unsafe void GatherNode()
        {
            var gatheringWindow = (AddonGathering*)Dalamud.GameGui.GetAddonByName("Gathering", 1);
            if (gatheringWindow == null) return;

            var ids = new List<uint>()
                    {
                    gatheringWindow->GatheredItemId1,
                    gatheringWindow->GatheredItemId2,
                    gatheringWindow->GatheredItemId3,
                    gatheringWindow->GatheredItemId4,
                    gatheringWindow->GatheredItemId5,
                    gatheringWindow->GatheredItemId6,
                    gatheringWindow->GatheredItemId7,
                    gatheringWindow->GatheredItemId8
                    };

            UseActions(ids);

            var itemIndex = ids.IndexOf(DesiredItem?.ItemId ?? 0);
            if (itemIndex < 0) itemIndex = ids.IndexOf(ids.FirstOrDefault(i => i > 0));

            var receiveEventAddress = new nint(gatheringWindow->AtkUnitBase.AtkEventListener.vfunc[2]);
            var eventDelegate = Marshal.GetDelegateForFunctionPointer<ReceiveEventDelegate>(receiveEventAddress);

            var target = AtkStage.GetSingleton();
            var eventData = EventData.ForNormalTarget(target, &gatheringWindow->AtkUnitBase);
            var inputData = InputData.Empty();

            eventDelegate.Invoke(&gatheringWindow->AtkUnitBase.AtkEventListener, ClickLib.Enums.EventType.CHANGE, (uint)itemIndex, eventData.Data, inputData.Data);
        }

        private void UseActions(List<uint> itemIds)
        {
            UseLuck(itemIds);
            Use100GPAction(itemIds);
        }

        private bool _hiddenRevealed = false;
        private unsafe void UseLuck(List<uint> itemIds)
        {
            if (!DesiredItem?.GatheringData.IsHidden ?? false)
                return;
            if (itemIds.Count > 0 && itemIds.Any(i => i == DesiredItem?.ItemId))
            {
                return;
            }
            //if (Dalamud.ClientState.LocalPlayer.CurrentGp < 500) return;
            if (_hiddenRevealed) return;
            var actionManager = ActionManager.Instance();
            switch (Svc.ClientState.LocalPlayer.ClassJob.Id)
            {
                case 17: //BTN
                    if (actionManager->GetActionStatus(ActionType.Action, 4095) == 0)
                    {
                        actionManager->UseAction(ActionType.Action, 4095);
                        _hiddenRevealed = true;
                    }
                    break;
                case 16: //MIN
                    if (actionManager->GetActionStatus(ActionType.Action, 4081) == 0)
                    {
                        actionManager->UseAction(ActionType.Action, 4081);
                        _hiddenRevealed = true;
                    }
                    break;
            }
        }


        private unsafe void Use100GPAction(List<uint> itemIds)
        {
            if (itemIds.Count > 0 && !itemIds.Any(i => i == DesiredItem?.ItemId))
            {
                return;
            }
            if (Dalamud.ClientState.LocalPlayer.StatusList.Any(s => s.StatusId == 1286 || s.StatusId == 756))
                return;
            if ((Dalamud.ClientState.LocalPlayer?.CurrentGp ?? 0) < 100)
                return;

            var actionManager = ActionManager.Instance();
            switch (Svc.ClientState.LocalPlayer.ClassJob.Id)
            {
                case 17:
                    if (actionManager->GetActionStatus(ActionType.Action, 273) == 0)
                    {
                        actionManager->UseAction(ActionType.Action, 273);
                    }
                    else if (actionManager->GetActionStatus(ActionType.Action, 4087) == 0)
                    {
                        actionManager->UseAction(ActionType.Action, 4087);
                    }
                    break;
                case 16:
                    if (actionManager->GetActionStatus(ActionType.Action, 272) == 0)
                    {
                        actionManager->UseAction(ActionType.Action, 272);
                    }
                    else if (actionManager->GetActionStatus(ActionType.Action, 4073) == 0)
                    {
                        actionManager->UseAction(ActionType.Action, 4073);
                    }
                    break;
            }
        }

        private unsafe delegate nint ReceiveEventDelegate(AtkEventListener* eventListener, ClickLib.Enums.EventType eventType, uint eventParam, void* eventData, void* inputData);

        private static unsafe ReceiveEventDelegate GetReceiveEvent(AtkEventListener* listener)
        {
            var receiveEventAddress = new IntPtr(listener->vfunc[2]);
            return Marshal.GetDelegateForFunctionPointer<ReceiveEventDelegate>(receiveEventAddress)!;
        }

        private bool _isInteracting = false;
        private TaskManager _taskManager = new TaskManager();
        private unsafe void InteractNode(GameObject targetGatherable)
        {
            if (Dalamud.Conditions[ConditionFlag.Jumping])
                return;
            if (IsPlayerBusy()) return;
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return;
            if (_isInteracting) return;
            _isInteracting = true;
            _taskManager.EnqueueDelay(1000);
            _taskManager.Enqueue(() =>
            {
                targetSystem->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)targetGatherable.Address);
                _isInteracting = false;
            });
        }

        private bool IsPlayerBusy()
        {
            var player = Dalamud.ClientState.LocalPlayer;
            if (player == null)
                return true;
            if (player.IsCasting)
                return true;
            if (player.IsDead)
                return true;

            return false;
        }

        private unsafe void InventoryCheck()
        {
            var presets = _plugin.GatherWindowManager.Presets;
            if (presets == null)
                return;

            var inventory = InventoryManager.Instance();
            if (inventory == null) return;

            foreach (var preset in presets)
            {
                var items = preset.Items;
                if (items == null)
                    continue;

                var indicesToRemove = new List<int>();

                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var itemCount = inventory->GetInventoryItemCount(item.ItemId);
                    if (itemCount >= item.Quantity)
                    {
                        _plugin.GatherWindowManager.RemoveItem(preset, i);
                    }
                }
            }
        }
        private Vector3 _lastKnownPosition = Vector3.Zero;
        private DateTime _lastPositionCheckTime = DateTime.Now;
        private TimeSpan _stuckDurationThreshold = TimeSpan.FromSeconds(5);

        private void NavmeshStuckCheck()
        {
            var currentPosition = Dalamud.ClientState.LocalPlayer.Position;
            var currentTime = DateTime.Now;

            // Check if enough time has passed since the last position check
            if (currentTime - _lastPositionCheckTime >= _stuckDurationThreshold)
            {
                var distance = Vector3.Distance(currentPosition, _lastKnownPosition);

                // If the player has not moved a significant distance, consider them stuck
                if (distance < 3)
                {
                    VNavmesh_IPCSubscriber.Nav_Reload();
                }

                // Update the last known position and time for the next check
                _lastKnownPosition = currentPosition;
                _lastPositionCheckTime = currentTime;
            }
        }


        private bool IsDesiredNode(GameObject gameObject)
        {
            return DesiredItem?.NodeList.Any(n => n.WorldCoords.Keys.Any(k => k == gameObject.DataId)) ?? false;
        }


        private unsafe void Dismount()
        {
            var am = ActionManager.Instance();
            am->UseAction(ActionType.Mount, 0);
        }

        private unsafe void MountUp()
        {
            var am = ActionManager.Instance();
            var mount = GatherBuddy.Config.AutoGatherMountId;
            if (am->GetActionStatus(ActionType.Mount, mount) != 0) return;
            am->UseAction(ActionType.Mount, mount);
        }

    }
}
