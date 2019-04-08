using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Experimental.Input.LowLevel;
using UnityEngine.Experimental.Input.Utilities;
using UnityEngine.Profiling;

////TODO: remove direct references to InputManager

////TODO: make sure controls in per-action and per-map control arrays are unique (the internal arrays are probably okay to have duplicates)

////REVIEW: allow setup where state monitor is enabled but action is disabled?

namespace UnityEngine.Experimental.Input
{
    using InputActionListener = Action<InputAction.CallbackContext>;

    /// <summary>
    /// Dynamic execution state of one or more <see cref="InputActionMap">action maps</see> and
    /// all the actions they contain.
    /// </summary>
    /// <remarks>
    /// The aim of this class is to both put all the dynamic execution state into one place as well
    /// as to organize state in tight, GC-optimized arrays. Also, by moving state out of individual
    /// <see cref="InputActionMap">action maps</see>, we can combine the state of several maps
    /// into one single object with a single set of arrays. Ideally, if you have a single action
    /// asset in the game, you get a single InputActionState that contains the entire dynamic
    /// execution state for your game's actions.
    ///
    /// Note that this class allocates unmanaged memory. It has to be disposed of or it will leak
    /// memory!
    ///
    /// An instance of this class is also used for singleton actions by means of the hidden action
    /// map we create for those actions. In that case, there will be both a hidden map instance
    /// as well as an action state for every separate singleton action. This makes singleton actions
    /// relatively expensive.
    /// </remarks>
    internal unsafe class InputActionState : IInputStateChangeMonitor, ICloneable, IDisposable
    {
        public const int kInvalidIndex = -1;

        /// <summary>
        /// Array of all maps added to the state.
        /// </summary>
        public InputActionMap[] maps;

        /// <summary>
        /// List of all resolved controls.
        /// </summary>
        /// <remarks>
        /// As we don't know in advance how many controls a binding may match (if any), we bump the size of
        /// this array in increments during resolution. This means it may be end up being larger than the total
        /// number of used controls and have empty entries at the end. Use <see cref="UnmanagedMemory.controlCount"/> and not
        /// <c>.Length</c> to find the actual number of controls.
        ///
        /// All bound controls are included in the array regardless of whether only a partial set of actions
        /// is currently enabled. What ultimately decides whether controls get triggered or not is whether we
        /// have installed state monitors for them or not.
        /// </remarks>
        public InputControl[] controls;

        /// <summary>
        /// Array of instantiated interaction objects.
        /// </summary>
        /// <remarks>
        /// Every binding that has interactions corresponds to a slice of this array.
        ///
        /// Indices match between this and interaction states in <see cref="memory"/>.
        /// </remarks>
        public IInputInteraction[] interactions;

        /// <summary>
        /// Processor objects instantiated for the bindings in the state.
        /// </summary>
        public InputProcessor[] processors;

        /// <summary>
        /// Array of instantiated composite objects.
        /// </summary>
        public InputBindingComposite[] composites;

        public int totalProcessorCount;
        public int totalCompositeCount => memory.compositeCount;
        public int totalMapCount => memory.mapCount;
        public int totalActionCount => memory.actionCount;
        public int totalBindingCount => memory.bindingCount;
        public int totalInteractionCount => memory.interactionCount;
        public int totalControlCount => memory.controlCount;

        /// <summary>
        /// Block of unmanaged memory that holds the dynamic execution state of the actions and their controls.
        /// </summary>
        /// <remarks>
        /// We keep several arrays of structured data in a single block of unmanaged memory.
        /// </remarks>
        public UnmanagedMemory memory;

        public ActionMapIndices* mapIndices => memory.mapIndices;
        public TriggerState* actionStates => memory.actionStates;
        public BindingState* bindingStates => memory.bindingStates;
        public InteractionState* interactionStates => memory.interactionStates;
        public int* controlIndexToBindingIndex => memory.controlIndexToBindingIndex;

        private Action<InputUpdateType> m_OnBeforeUpdateDelegate;
        private Action<InputUpdateType> m_OnAfterUpdateDelegate;
        private bool m_OnBeforeUpdateHooked;
        private bool m_OnAfterUpdateHooked;

        private int m_ContinuousActionCount;
        private int m_ContinuousActionCountFromPreviousUpdate;
        private int[] m_ContinuousActions;

        /// <summary>
        /// Initialize execution state with given resolved binding information.
        /// </summary>
        /// <param name="resolver"></param>
        public void Initialize(InputBindingResolver resolver)
        {
            ClaimDataFrom(resolver);
            AddToGlobaList();
        }

        internal void ClaimDataFrom(InputBindingResolver resolver)
        {
            totalProcessorCount = resolver.totalProcessorCount;

            maps = resolver.maps;
            interactions = resolver.interactions;
            processors = resolver.processors;
            composites = resolver.composites;
            controls = resolver.controls;

            memory = resolver.memory;
            resolver.memory = new UnmanagedMemory();
        }

        ~InputActionState()
        {
            Destroy(isFinalizing: true);
        }

        public void Dispose()
        {
            Destroy();
        }

        private void Destroy(bool isFinalizing = false)
        {
            if (!isFinalizing)
            {
                for (var i = 0; i < totalMapCount; ++i)
                {
                    var map = maps[i];

                    if (map.m_Asset != null)
                        map.m_Asset.m_SharedStateForAllMaps = null;

                    map.m_State = null;
                    map.m_MapIndexInState = kInvalidIndex;
                    map.m_EnabledActionsCount = 0;

                    // Reset action indices on the map's actions.
                    var actions = map.m_Actions;
                    if (actions != null)
                    {
                        for (var n = 0; n < actions.Length; ++n)
                            actions[n].m_ActionIndex = kInvalidIndex;
                    }
                }

                RemoveMapFromGlobalList();
            }
            memory.Dispose();
        }

        /// <summary>
        /// Create a copy of the state.
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// The copy is non-functional in so far as it cannot be used to keep track of changes made to
        /// any associated actions. However, it can be used to freeze the binding resolution state of
        /// a particular set of enabled actions. This is used by <see cref="InputActionTrace"/>.
        /// </remarks>
        public InputActionState Clone()
        {
            return new InputActionState
            {
                maps = ArrayHelpers.Copy(maps),
                controls = ArrayHelpers.Copy(controls),
                interactions = ArrayHelpers.Copy(interactions),
                processors = ArrayHelpers.Copy(processors),
                composites = ArrayHelpers.Copy(composites),
                totalProcessorCount = totalProcessorCount,
                memory = memory.Clone(),
            };
        }

        object ICloneable.Clone()
        {
            return Clone();
        }

        /// <summary>
        /// Check if the state is currently using a control from the given device.
        /// </summary>
        /// <param name="device">Any input device.</param>
        /// <returns>True if any of the maps in the state has the device in its <see cref="InputActionMap.devices"/>
        /// list or if any of the device's controls are contained in <see cref="controls"/>.</returns>
        public bool IsUsingDevice(InputDevice device)
        {
            Debug.Assert(device != null, "Device is null");

            // If all maps have device restrictions, the device must be in it
            // or we're not using it.
            var haveMapsWithoutDeviceRestrictions = false;
            for (var i = 0; i < totalMapCount; ++i)
            {
                var map = maps[i];
                var devicesForMap = map.devices;

                if (devicesForMap == null)
                    haveMapsWithoutDeviceRestrictions = true;
                else if (devicesForMap.Value.Contains(device))
                    return true;
            }

            if (!haveMapsWithoutDeviceRestrictions)
                return false;

            // Check all our controls one by one.
            for (var i = 0; i < totalControlCount; ++i)
                if (controls[i].device == device)
                    return true;

            return false;
        }

        /// <summary>
        /// Check if the state would use a control from the given device.
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        public bool CanUseDevice(InputDevice device)
        {
            Debug.Assert(device != null, "Device is null");

            // If all maps have device restrictions and the device isn't in them, we can't use
            // the device.
            var haveMapWithoutDeviceRestrictions = false;
            for (var i = 0; i < totalMapCount; ++i)
            {
                var map = maps[i];
                var devicesForMap = map.devices;

                if (devicesForMap == null)
                    haveMapWithoutDeviceRestrictions = true;
                else if (devicesForMap.Value.Contains(device))
                    return true;
            }

            if (!haveMapWithoutDeviceRestrictions)
                return false;

            for (var i = 0; i < totalMapCount; ++i)
            {
                var map = maps[i];
                var bindings = map.m_Bindings;
                if (bindings == null)
                    continue;

                var bindingCount = bindings.Length;
                for (var n = 0; n < bindingCount; ++n)
                {
                    if (InputControlPath.TryFindControl(device, bindings[n].effectivePath) != null)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Check whether the state has any actions that are currently enabled.
        /// </summary>
        /// <returns></returns>
        public bool HasEnabledActions()
        {
            for (var i = 0; i < totalMapCount; ++i)
            {
                var map = maps[i];
                if (map.enabled)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Synchronize the current action states based on what they were before.
        /// </summary>
        /// <param name="oldState"></param>
        /// <remarks>
        /// We do this when we have to temporarily disable actions in order to re-resolve bindings.
        ///
        /// Note that we do NOT restore action states perfectly. I.e. will we will not preserve trigger
        /// and interaction states exactly to what they were before. Given that the bound controls may change,
        /// it would be non-trivial to reliably correlate the old and the new state. Instead, we simply
        /// reenable all the actions and controls that were enabled before and then let the next update
        /// take it from there.
        /// </remarks>
        public void RestoreActionStates(UnmanagedMemory oldState)
        {
            Debug.Assert(oldState.isAllocated, "Old state contains no memory");
            Debug.Assert(oldState.actionCount == memory.actionCount, "Action count in old and new state must be the same");
            Debug.Assert(oldState.bindingCount == memory.bindingCount, "Binding count in old and new state must be the same");
            Debug.Assert(oldState.mapCount == memory.mapCount, "Map count in old and new state must be the same");

            // Go through the state map by map and in each map, binding by binding. Enable
            // all bound controls for which the respective action isn't disabled.
            for (var i = 0; i < memory.bindingCount; ++i)
            {
                var bindingState = &memory.bindingStates[i];
                if (bindingState->isPartOfComposite)
                {
                    // Bindings that are part of composites get enabled through the composite itself.
                    continue;
                }

                var actionIndex = bindingState->actionIndex;
                if (actionIndex == kInvalidIndex)
                {
                    // Binding is not targeting an action.
                    continue;
                }

                // Skip any binding for which the action was disabled.
                // NOTE: We check the OLD STATE here. The phase in the new state will change immediately
                //       on the first binding to an action but there may be multiple bindings leading to the
                //       same action.
                if (oldState.actionStates[actionIndex].phase == InputActionPhase.Disabled)
                    continue;

                // Mark the action as enabled, if not already done.
                var actionState = &memory.actionStates[actionIndex];
                if (actionState->phase == InputActionPhase.Disabled)
                {
                    actionState->phase = InputActionPhase.Waiting;

                    // Keep track of actions we enable in each map.
                    var mapIndex = actionState->mapIndex;
                    var map = maps[mapIndex];
                    ++map.m_EnabledActionsCount;

                    ////REVIEW: Ideally, we'd know when an action map had *ALL* actions enabled and do not send notifications one by one here
                    var action = map.m_Actions[actionIndex - mapIndices[mapIndex].actionStartIndex];
                    NotifyListenersOfActionChange(InputActionChange.ActionEnabled, action);
                }

                // Enable all controls on the binding.
                // NOTE: We force an initial state check on actions here regardless of whether the action has
                //       it enabled or not. The reason is that we use this path to temporarily disable actions
                //       and re-enabling them should have the actions resume where they left off (where applicable).
                EnableControls(actionState->mapIndex, bindingState->controlStartIndex, bindingState->controlCount,
                    forceStateCheck: true);
            }

            // Make sure we get an initial state check.
            HookOnBeforeUpdate();
        }

        /// <summary>
        /// Reset the trigger state of the given action such that the action has no record of being triggered.
        /// </summary>
        /// <param name="actionIndex">Action whose state to reset.</param>
        /// <param name="toPhase">Phase to reset the action to. Must be either <see cref="InputActionPhase.Waiting"/>
        /// or <see cref="InputActionPhase.Disabled"/>. Other phases cannot be transitioned to through resets.</param>
        public void ResetActionState(int actionIndex, InputActionPhase toPhase = InputActionPhase.Waiting)
        {
            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount, "Action index out of range when resetting action");
            Debug.Assert(toPhase == InputActionPhase.Waiting || toPhase == InputActionPhase.Disabled,
                "Phase must be Waiting or Disabled");

            // If the action in started or performed phase, cancel it first.
            var actionState = &actionStates[actionIndex];
            if (actionState->phase != InputActionPhase.Waiting)
            {
                // Cancellation calls should receive current time.
                actionState->time = InputRuntime.s_Instance.currentTime;

                // If the action got triggered from an interaction, go and reset all interactions on the binding
                // that got triggered.
                if (actionState->interactionIndex != kInvalidIndex)
                {
                    var bindingIndex = actionState->bindingIndex;
                    if (bindingIndex != kInvalidIndex)
                    {
                        var mapIndex = actionState->mapIndex;
                        var interactionCount = bindingStates[bindingIndex].interactionCount;
                        var interactionStartIndex = bindingStates[bindingIndex].interactionStartIndex;

                        for (var i = 0; i < interactionCount; ++i)
                        {
                            var interactionIndex = interactionStartIndex + i;
                            ResetInteractionStateAndCancelIfNecessary(mapIndex, bindingIndex, interactionIndex);
                        }
                    }
                }
                else
                {
                    // No interactions. Cancel the action directly.

                    Debug.Assert(actionState->bindingIndex != kInvalidIndex, "Binding index on trigger state is invalid");
                    Debug.Assert(bindingStates[actionState->bindingIndex].interactionCount == 0,
                        "Action has been triggered but apparently not from an interaction yet there's interactions on the binding that got triggered?!?");

                    ChangePhaseOfAction(InputActionPhase.Cancelled, ref actionStates[actionIndex]);
                }
            }

            // Wipe state.
            var state = *actionState;
            state.phase = toPhase;
            state.controlIndex = kInvalidIndex;
            state.bindingIndex = 0;
            state.interactionIndex = kInvalidIndex;
            state.startTime = 0;
            state.time = 0;
            state.hasMultipleConcurrentActuations = false;
            *actionState = state;

            // Remove if currently on the list of continuous actions.
            if (state.continuous)
            {
                var continuousIndex = ArrayHelpers.IndexOf(m_ContinuousActions, actionIndex, count: m_ContinuousActionCount);
                if (continuousIndex != -1)
                    ArrayHelpers.EraseAtByMovingTail(m_ContinuousActions, ref m_ContinuousActionCount, continuousIndex);
            }
        }

        public ref TriggerState FetchActionState(InputAction action)
        {
            Debug.Assert(action != null, "Action must not be null");
            Debug.Assert(action.m_ActionMap != null, "Action must have an action map");
            Debug.Assert(action.m_ActionMap.m_MapIndexInState != kInvalidIndex, "Action must have index set");
            Debug.Assert(maps.Contains(action.m_ActionMap), "Action map must be contained in state");
            Debug.Assert(action.m_ActionIndex >= 0 && action.m_ActionIndex < totalActionCount, "Action index is out of range");

            return ref actionStates[action.m_ActionIndex];
        }

        public ActionMapIndices FetchMapIndices(InputActionMap map)
        {
            Debug.Assert(map != null, "Must must not be null");
            Debug.Assert(maps.Contains(map), "Map must be contained in state");
            return mapIndices[map.m_MapIndexInState];
        }

        public void EnableAllActions(InputActionMap map)
        {
            Debug.Assert(map != null, "Map must not be null");
            Debug.Assert(map.m_Actions != null, "Map must have actions");
            Debug.Assert(maps.Contains(map), "Map must be contained in state");

            EnableControls(map);

            // Put all actions into waiting state.
            var mapIndex = map.m_MapIndexInState;
            Debug.Assert(mapIndex >= 0 && mapIndex < totalMapCount);
            var actionCount = mapIndices[mapIndex].actionCount;
            var actionStartIndex = mapIndices[mapIndex].actionStartIndex;
            for (var i = 0; i < actionCount; ++i)
            {
                var actionIndex = actionStartIndex + i;
                actionStates[actionIndex].phase = InputActionPhase.Waiting;
            }
            map.m_EnabledActionsCount = actionCount;

            HookOnBeforeUpdate();

            // Make sure that if we happen to get here with one of the hidden action maps we create for singleton
            // action, we notify on the action, not the hidden map.
            if (map.m_SingletonAction != null)
                NotifyListenersOfActionChange(InputActionChange.ActionEnabled, map.m_SingletonAction);
            else
                NotifyListenersOfActionChange(InputActionChange.ActionMapEnabled, map);
        }

        private void EnableControls(InputActionMap map)
        {
            Debug.Assert(map != null, "Map must not be null");
            Debug.Assert(map.m_Actions != null, "Map must have actions");
            Debug.Assert(maps.Contains(map), "Map must be contained in state");

            var mapIndex = map.m_MapIndexInState;
            Debug.Assert(mapIndex >= 0 && mapIndex < totalMapCount);

            // Install state monitors for all controls.
            var controlCount = mapIndices[mapIndex].controlCount;
            var controlStartIndex = mapIndices[mapIndex].controlStartIndex;
            if (controlCount > 0)
                EnableControls(mapIndex, controlStartIndex, controlCount);
        }

        public void EnableSingleAction(InputAction action)
        {
            Debug.Assert(action != null, "Action must not be null");
            Debug.Assert(action.m_ActionMap != null, "Action must have action map");
            Debug.Assert(maps.Contains(action.m_ActionMap), "Action map must be contained in state");

            EnableControls(action);

            // Put action into waiting state.
            var actionIndex = action.m_ActionIndex;
            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount,
                "Action index out of range when enabling single action");
            actionStates[actionIndex].phase = InputActionPhase.Waiting;
            ++action.m_ActionMap.m_EnabledActionsCount;

            HookOnBeforeUpdate();
            NotifyListenersOfActionChange(InputActionChange.ActionEnabled, action);
        }

        private void EnableControls(InputAction action)
        {
            Debug.Assert(action != null, "Action must not be null");
            Debug.Assert(action.m_ActionMap != null, "Action must have action map");
            Debug.Assert(maps.Contains(action.m_ActionMap), "Map must be contained in state");

            var actionIndex = action.m_ActionIndex;
            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount,
                "Action index out of range when enabling controls");

            var map = action.m_ActionMap;
            var mapIndex = map.m_MapIndexInState;
            Debug.Assert(mapIndex >= 0 && mapIndex < totalMapCount, "Map index out of range");

            // Go through all bindings in the map and for all that belong to the given action,
            // enable the associated controls.
            var bindingStartIndex = mapIndices[mapIndex].bindingStartIndex;
            var bindingCount = mapIndices[mapIndex].bindingCount;
            var bindingStatesPtr = memory.bindingStates;
            for (var i = 0; i < bindingCount; ++i)
            {
                var bindingIndex = bindingStartIndex + i;
                var bindingState = &bindingStatesPtr[bindingIndex];
                if (bindingState->actionIndex != actionIndex)
                    continue;

                // Composites enable en-bloc through the composite binding itself.
                if (bindingState->isPartOfComposite)
                    continue;

                var controlCount = bindingState->controlCount;
                if (controlCount == 0)
                    continue;

                EnableControls(mapIndex, bindingState->controlStartIndex, controlCount);
            }
        }

        public void DisableAllActions(InputActionMap map)
        {
            Debug.Assert(map != null, "Map must not be null");
            Debug.Assert(map.m_Actions != null, "Map must have actions");
            Debug.Assert(maps.Contains(map), "Map must be contained in state");

            DisableControls(map);

            // Mark all actions as disabled.
            var mapIndex = map.m_MapIndexInState;
            Debug.Assert(mapIndex >= 0 && mapIndex < totalMapCount, "Map index out of range");
            var actionStartIndex = mapIndices[mapIndex].actionStartIndex;
            var actionCount = mapIndices[mapIndex].actionCount;
            for (var i = 0; i < actionCount; ++i)
            {
                var actionIndex = actionStartIndex + i;
                if (actionStates[actionIndex].phase != InputActionPhase.Disabled)
                    ResetActionState(actionIndex, toPhase: InputActionPhase.Disabled);
            }
            map.m_EnabledActionsCount = 0;

            // Make sure that if we happen to get here with one of the hidden action maps we create for singleton
            // action, we notify on the action, not the hidden map.
            if (map.m_SingletonAction != null)
                NotifyListenersOfActionChange(InputActionChange.ActionDisabled, map.m_SingletonAction);
            else
                NotifyListenersOfActionChange(InputActionChange.ActionMapDisabled, map);
        }

        private void DisableControls(InputActionMap map)
        {
            Debug.Assert(map != null, "Map must not be null");
            Debug.Assert(map.m_Actions != null, "Map must have actions");
            Debug.Assert(maps.Contains(map), "Map must be contained in state");

            var mapIndex = map.m_MapIndexInState;
            Debug.Assert(mapIndex >= 0 && mapIndex < totalMapCount, "Map index out of range");

            // Remove state monitors from all controls.
            var controlCount = mapIndices[mapIndex].controlCount;
            var controlStartIndex = mapIndices[mapIndex].controlStartIndex;
            if (controlCount > 0)
                DisableControls(mapIndex, controlStartIndex, controlCount);
        }

        public void DisableSingleAction(InputAction action)
        {
            Debug.Assert(action != null, "Action must not be null");
            Debug.Assert(action.m_ActionMap != null, "Action must have action map");
            Debug.Assert(maps.Contains(action.m_ActionMap), "Action map must be contained in state");

            DisableControls(action);
            ResetActionState(action.m_ActionIndex, toPhase: InputActionPhase.Disabled);
            --action.m_ActionMap.m_EnabledActionsCount;

            NotifyListenersOfActionChange(InputActionChange.ActionDisabled, action);
        }

        private void DisableControls(InputAction action)
        {
            Debug.Assert(action != null, "Action must not be null");
            Debug.Assert(action.m_ActionMap != null, "Action must have action map");
            Debug.Assert(maps.Contains(action.m_ActionMap), "Action map must be contained in state");

            var actionIndex = action.m_ActionIndex;
            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount,
                "Action index out of range when disabling controls");

            var map = action.m_ActionMap;
            var mapIndex = map.m_MapIndexInState;
            Debug.Assert(mapIndex >= 0 && mapIndex < totalMapCount, "Map index out of range");

            // Go through all bindings in the map and for all that belong to the given action,
            // disable the associated controls.
            var bindingStartIndex = mapIndices[mapIndex].bindingStartIndex;
            var bindingCount = mapIndices[mapIndex].bindingCount;
            var bindingStatesPtr = memory.bindingStates;
            for (var i = 0; i < bindingCount; ++i)
            {
                var bindingIndex = bindingStartIndex + i;
                var bindingState = &bindingStatesPtr[bindingIndex];
                if (bindingState->actionIndex != actionIndex)
                    continue;

                // Composites enable en-bloc through the composite binding itself.
                if (bindingState->isPartOfComposite)
                    continue;

                var controlCount = bindingState->controlCount;
                if (controlCount == 0)
                    continue;

                DisableControls(mapIndex, bindingState->controlStartIndex, controlCount);
            }
        }

        ////REVIEW: can we have a method on InputManager doing this in bulk?

        private void EnableControls(int mapIndex, int controlStartIndex, int numControls, bool forceStateCheck = false)
        {
            Debug.Assert(controls != null, "State must have controls");
            Debug.Assert(controlStartIndex >= 0 && (controlStartIndex < totalControlCount || numControls == 0),
                "Control start index out of range");
            Debug.Assert(controlStartIndex + numControls <= totalControlCount, "Control range out of bounds");

            var manager = InputSystem.s_Manager;
            for (var i = 0; i < numControls; ++i)
            {
                var controlIndex = controlStartIndex + i;
                var bindingIndex = controlIndexToBindingIndex[controlIndex];
                var mapControlAndBindingIndex = ToCombinedMapAndControlAndBindingIndex(mapIndex, controlIndex, bindingIndex);

                if (forceStateCheck || bindingStates[bindingIndex].wantsInitialStateCheck)
                    bindingStates[bindingIndex].initialStateCheckPending = true;
                manager.AddStateChangeMonitor(controls[controlIndex], this, mapControlAndBindingIndex);
            }
        }

        private void DisableControls(int mapIndex, int controlStartIndex, int numControls)
        {
            Debug.Assert(controls != null, "State must have controls");
            Debug.Assert(controlStartIndex >= 0 && (controlStartIndex < totalControlCount || numControls == 0),
                "Control start index out of range");
            Debug.Assert(controlStartIndex + numControls <= totalControlCount, "Control range out of bounds");

            var manager = InputSystem.s_Manager;
            for (var i = 0; i < numControls; ++i)
            {
                var controlIndex = controlStartIndex + i;
                var bindingIndex = controlIndexToBindingIndex[controlIndex];
                var mapControlAndBindingIndex = ToCombinedMapAndControlAndBindingIndex(mapIndex, controlIndex, bindingIndex);

                if (bindingStates[bindingIndex].wantsInitialStateCheck)
                    bindingStates[bindingIndex].initialStateCheckPending = false;
                manager.RemoveStateChangeMonitor(controls[controlIndex], this, mapControlAndBindingIndex);
            }
        }

        private void HookOnBeforeUpdate()
        {
            if (m_OnBeforeUpdateHooked)
                return;

            if (m_OnBeforeUpdateDelegate == null)
                m_OnBeforeUpdateDelegate = OnBeforeInitialUpdate;
            InputSystem.s_Manager.onBeforeUpdate += m_OnBeforeUpdateDelegate;
            m_OnBeforeUpdateHooked = true;
        }

        private void UnhookOnBeforeUpdate()
        {
            if (!m_OnBeforeUpdateHooked)
                return;

            InputSystem.s_Manager.onBeforeUpdate -= m_OnBeforeUpdateDelegate;
            m_OnBeforeUpdateHooked = false;
        }

        // We hook this into InputManager.onBeforeUpdate every time actions are enabled and then take it off
        // the list after the first call. Inside here we check whether any actions we enabled already have
        // non-default state on bound controls.
        //
        // NOTE: We do this as a callback from onBeforeUpdate rather than directly when the action is enabled
        //       to ensure that the callbacks happen during input processing and not randomly from wherever
        //       an action happens to be enabled.
        private void OnBeforeInitialUpdate(InputUpdateType type)
        {
            ////TODO: deal with update type

            // Remove us from the callback as the processing we're doing here is a one-time thing.
            UnhookOnBeforeUpdate();

            Profiler.BeginSample("InitialActionStateCheck");

            // The composite logic relies on the event ID to determine whether a composite binding should trigger again
            // when already triggered. Make up a fake event with just an ID.
            var inputEvent = new InputEvent {eventId = 1234};
            var eventPtr = new InputEventPtr(&inputEvent);

            // Use current time as time of control state change.
            var time = InputRuntime.s_Instance.currentTime;

            ////REVIEW: should we store this data in a separate place rather than go through all bindingStates?

            // Go through all binding states and for every binding that needs an initial state check,
            // go through all bound controls and for each one that isn't in its default state, pretend
            // that the control just got actuated.
            for (var bindingIndex = 0; bindingIndex < totalBindingCount; ++bindingIndex)
            {
                if (!bindingStates[bindingIndex].initialStateCheckPending)
                    continue;

                bindingStates[bindingIndex].initialStateCheckPending = false;

                var mapIndex = bindingStates[bindingIndex].mapIndex;
                var controlStartIndex = bindingStates[bindingIndex].controlStartIndex;
                var controlCount = bindingStates[bindingIndex].controlCount;

                for (var n = 0; n < controlCount; ++n)
                {
                    var controlIndex = controlStartIndex + n;
                    var control = controls[controlIndex];

                    if (!control.CheckStateIsAtDefault())
                        ProcessControlStateChange(mapIndex, controlIndex, bindingIndex, time, eventPtr);
                }
            }

            Profiler.EndSample();
        }

        private void OnAfterUpdateProcessContinuousActions(InputUpdateType updateType)
        {
            ////TODO: handle update type

            // Everything that is still on the list of continuous actions at the end of a
            // frame either got there during the frame or is there still from the last frame
            // (meaning the action didn't get any input this frame). Continuous actions added
            // this update will all have been added to the end of the array so we know that
            // everything in between #0 and m_ContinuousActionCountFromPreviousUpdate is
            // continuous actions left from the previous update.

            var time = InputRuntime.s_Instance.currentTime;
            for (var i = 0; i < m_ContinuousActionCountFromPreviousUpdate; ++i)
            {
                var actionIndex = m_ContinuousActions[i];
                Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount,
                    "Action index out of range when updating continuous actions");

                var currentPhase = actionStates[actionIndex].phase;
                Debug.Assert(currentPhase == InputActionPhase.Started || currentPhase == InputActionPhase.Performed,
                    "Current phase must be Started or Performed");

                // Trigger the action and go back to its current phase (may be
                actionStates[actionIndex].time = time;
                ChangePhaseOfAction(InputActionPhase.Performed, ref actionStates[actionIndex],
                    phaseAfterPerformedOrCancelled: currentPhase);
            }

            // All actions that are currently in the list become the actions we update by default
            // on the next update. If events come in during the next update, the action will be
            // moved out of there using DontTriggerContinuousActionThisUpdate().
            m_ContinuousActionCountFromPreviousUpdate = m_ContinuousActionCount;
        }

        /// <summary>
        /// Add an action to the list of actions we trigger every frame.
        /// </summary>
        /// <param name="actionIndex">Index of the action in <see cref="actionStates"/>.</param>
        private void AddContinuousAction(int actionIndex)
        {
            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount,
                "Action index out of range when adding continuous action");
            Debug.Assert(!actionStates[actionIndex].onContinuousList, "Action is already in list");
            Debug.Assert(
                ArrayHelpers.IndexOfValue(m_ContinuousActions, actionIndex, startIndex: 0, count: m_ContinuousActionCount) == -1,
                "Action is already on list of continuous actions");

            ArrayHelpers.AppendWithCapacity(ref m_ContinuousActions, ref m_ContinuousActionCount, actionIndex);
            actionStates[actionIndex].onContinuousList = true;

            // Hook into `onAfterUpdate` if we haven't already.
            if (!m_OnAfterUpdateHooked)
            {
                if (m_OnAfterUpdateDelegate == null)
                    m_OnAfterUpdateDelegate = OnAfterUpdateProcessContinuousActions;
                InputSystem.s_Manager.onAfterUpdate += m_OnAfterUpdateDelegate;
                m_OnAfterUpdateHooked = true;
            }
        }

        /// <summary>
        /// Remove an action from the list of actions we trigger every frame.
        /// </summary>
        /// <param name="actionIndex"></param>
        private void RemoveContinuousAction(int actionIndex)
        {
            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount,
                "Action index out of range when removing continuous action");
            Debug.Assert(actionStates[actionIndex].onContinuousList, "Action not in list");
            Debug.Assert(
                ArrayHelpers.IndexOfValue(m_ContinuousActions, actionIndex, startIndex: 0, count: m_ContinuousActionCount) != -1,
                "Action is not currently in list of continuous actions");
            Debug.Assert(m_ContinuousActionCount > 0, "List of continuous actions is empty");

            var index = ArrayHelpers.IndexOfValue(m_ContinuousActions, actionIndex, startIndex: 0,
                count: m_ContinuousActionCount);
            Debug.Assert(index != -1, "Action not found in list of continuous actions");

            ArrayHelpers.EraseAtWithCapacity(ref m_ContinuousActions, ref m_ContinuousActionCount, index);
            actionStates[actionIndex].onContinuousList = false;

            // If the action was in the part of the list that continuous actions we have carried
            // over from the previous update, adjust for having removed a value there.
            if (index < m_ContinuousActionCountFromPreviousUpdate)
                --m_ContinuousActionCountFromPreviousUpdate;

            // Unhook from `onAfterUpdate` if we don't need it anymore.
            if (m_ContinuousActionCount == 0 && m_OnAfterUpdateHooked)
            {
                InputSystem.s_Manager.onAfterUpdate -= m_OnAfterUpdateDelegate;
                m_OnAfterUpdateHooked = false;
            }
        }

        private void DontTriggerContinuousActionThisUpdate(int actionIndex)
        {
            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount, "Index out of range");
            Debug.Assert(actionStates[actionIndex].onContinuousList, "Action not in list");
            Debug.Assert(
                ArrayHelpers.IndexOfValue(m_ContinuousActions, actionIndex, startIndex: 0, count: m_ContinuousActionCount) != -1,
                "Action is not currently in list of continuous actions");
            Debug.Assert(m_ContinuousActionCount > 0, "List of continuous actions is empty");

            // Check if the action is within the beginning section of the list of actions that we need to check at
            // the end of the current update. If so, move it out of there.
            var index = ArrayHelpers.IndexOfValue(m_ContinuousActions, actionIndex, startIndex: 0,
                count: m_ContinuousActionCount);
            if (index < m_ContinuousActionCountFromPreviousUpdate)
            {
                // Move to end of list.
                ArrayHelpers.EraseAtWithCapacity(ref m_ContinuousActions, ref m_ContinuousActionCount, index);
                --m_ContinuousActionCountFromPreviousUpdate;
                ArrayHelpers.AppendWithCapacity(ref m_ContinuousActions, ref m_ContinuousActionCount, actionIndex);
            }
        }

        // Called from InputManager when one of our state change monitors has fired.
        // Tells us the time of the change *according to the state events coming in*.
        // Also tells us which control of the controls we are binding to triggered the
        // change and relays the binding index we gave it when we called AddStateChangeMonitor.
        void IInputStateChangeMonitor.NotifyControlStateChanged(InputControl control, double time,
            InputEventPtr eventPtr, long mapControlAndBindingIndex)
        {
            SplitUpMapAndControlAndBindingIndex(mapControlAndBindingIndex, out var mapIndex, out var controlIndex, out var bindingIndex);
            ProcessControlStateChange(mapIndex, controlIndex, bindingIndex, time, eventPtr);
        }

        void IInputStateChangeMonitor.NotifyTimerExpired(InputControl control, double time,
            long mapControlAndBindingIndex, int interactionIndex)
        {
            SplitUpMapAndControlAndBindingIndex(mapControlAndBindingIndex, out var mapIndex, out var controlIndex, out var bindingIndex);
            ProcessTimeout(time, mapIndex, controlIndex, bindingIndex, interactionIndex);
        }

        // We mangle the various indices we use into a single long for association with state change
        // monitors. While we could look up map and binding indices from control indices, keeping
        // all the information together avoids having to unnecessarily jump around in memory to grab
        // the various pieces of data.

        private static long ToCombinedMapAndControlAndBindingIndex(int mapIndex, int controlIndex, int bindingIndex)
        {
            var result = (long)controlIndex;
            result |= (long)bindingIndex << 32;
            result |= (long)mapIndex << 48;
            return result;
        }

        private static void SplitUpMapAndControlAndBindingIndex(long mapControlAndBindingIndex, out int mapIndex,
            out int controlIndex, out int bindingIndex)
        {
            controlIndex = (int)(mapControlAndBindingIndex & 0xffffffff);
            bindingIndex = (int)((mapControlAndBindingIndex >> 32) & 0xffff);
            mapIndex = (int)(mapControlAndBindingIndex >> 48);
        }

        /// <summary>
        /// Process a state change that has happened in one of the controls attached
        /// to this action map state.
        /// </summary>
        /// <param name="mapIndex">Index of the action map to which the binding belongs.</param>
        /// <param name="controlIndex">Index of the control that changed state.</param>
        /// <param name="bindingIndex">Index of the binding associated with the given control.</param>
        /// <param name="time">The timestamp associated with the state change (comes from the state change event).</param>
        /// <param name="eventPtr">Event (if any) that triggered the state change.</param>
        /// <remarks>
        /// This is where we end up if one of the state monitors we've put in the system has triggered.
        /// From here we go back to the associated binding and then let it figure out what the state change
        /// means for it.
        ///
        /// Note that we get called for any change in state even if the change in state does not actually
        /// result in a change of value on the respective control.
        /// </remarks>
        private void ProcessControlStateChange(int mapIndex, int controlIndex, int bindingIndex, double time, InputEventPtr eventPtr)
        {
            Debug.Assert(mapIndex >= 0 && mapIndex < totalMapCount, "Map index out of range");
            Debug.Assert(controlIndex >= 0 && controlIndex < totalControlCount, "Control index out of range");
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");

            var bindingStatePtr = &bindingStates[bindingIndex];
            var actionIndex = bindingStatePtr->actionIndex;

            var trigger = new TriggerState
            {
                mapIndex = mapIndex,
                controlIndex = controlIndex,
                bindingIndex = bindingIndex,
                interactionIndex = kInvalidIndex,
                time = time,
                startTime = time,
                continuous = actionIndex != kInvalidIndex && actionStates[actionIndex].continuous,
                passThrough = actionIndex != kInvalidIndex && actionStates[actionIndex].passThrough,
            };

            // If the binding is part of a composite, check for interactions on the composite
            // itself and give them a first shot at processing the value change.
            var haveInteractionsOnComposite = false;
            if (bindingStatePtr->isPartOfComposite)
            {
                var compositeBindingIndex = bindingStatePtr->compositeOrCompositeBindingIndex;
                var compositeBindingPtr = &bindingStates[compositeBindingIndex];

                // If the composite has already been triggered from the very same event, ignore it.
                // Example: KeyboardState change that includes both A and W key state changes and we're looking
                //          at a WASD composite binding. There's a state change monitor on both the A and the W
                //          key and thus the manager will notify us individually of both changes. However, we
                //          want to perform the action only once.
                if (ShouldIgnoreControlStateChangeOnCompositeBinding(compositeBindingPtr, eventPtr))
                    return;

                // Common conflict resolution. We do this *after* the check above as it is more expensive.
                if (ShouldIgnoreControlStateChange(ref trigger, actionIndex))
                    return;

                // Run through interactions on composite.
                var interactionCountOnComposite = compositeBindingPtr->interactionCount;
                if (interactionCountOnComposite > 0)
                {
                    haveInteractionsOnComposite = true;
                    ProcessInteractions(ref trigger,
                        compositeBindingPtr->interactionStartIndex,
                        interactionCountOnComposite);
                }
            }
            else if (ShouldIgnoreControlStateChange(ref trigger, actionIndex))
            {
                return;
            }

            // If we have interactions, let them do all the processing. The presence of an interaction
            // essentially bypasses the default phase progression logic of an action.
            var interactionCount = bindingStatePtr->interactionCount;
            if (interactionCount > 0)
            {
                ProcessInteractions(ref trigger, bindingStatePtr->interactionStartIndex, interactionCount);
            }
            else if (!haveInteractionsOnComposite)
            {
                ProcessDefaultInteraction(ref trigger, actionIndex);
            }

            // If the associated action is continuous and is currently on the list to get triggered
            // this update, move it to the set of continuous actions we do NOT trigger this update.
            if (actionIndex != kInvalidIndex && actionStates[actionIndex].onContinuousList)
                DontTriggerContinuousActionThisUpdate(actionIndex);
        }

        /// <summary>
        /// Whether the given state change on a composite binding should be ignored.
        /// </summary>
        /// <param name="binding"></param>
        /// <param name="eventPtr"></param>
        /// <returns></returns>
        /// <remarks>
        /// Each state event may change the state of arbitrary many controls on a device and thus may trigger
        /// several bindings at once that are part of the same composite binding. We still want to trigger the
        /// composite binding only once for the event.
        ///
        /// To do so, we store the ID of the event on the binding and ignore events if they have the same
        /// ID as the one we've already recorded.
        /// </remarks>
        private static bool ShouldIgnoreControlStateChangeOnCompositeBinding(BindingState* binding, InputEvent* eventPtr)
        {
            if (eventPtr == null)
                return false;

            var eventId = eventPtr->eventId;
            if (binding->triggerEventIdForComposite == eventId)
                return true;

            binding->triggerEventIdForComposite = eventId;
            return false;
        }

        /// <summary>
        /// Whether the given control state should be ignored.
        /// </summary>
        /// <param name="trigger"></param>
        /// <param name="actionIndex"></param>
        /// <returns></returns>
        /// <remarks>
        /// If an action has multiple controls bound to it, control state changes on the action may conflict with each other.
        /// If that happens, we resolve the conflict by always sticking to the most actuated control.
        ///
        /// Pass-through actions (<see cref="InputAction.passThrough"/>) will always bypass conflict resolution and respond
        /// to every value change.
        ///
        /// Actions that are resolved to only a single control will early out of conflict resolution.
        ///
        /// Actions that are bound to multiple controls but have only one control actuated will early out of conflict
        /// resolution as well.
        ///
        /// Note that conflict resolution here is entirely tied to magnitude. This ignores other qualities that the value
        /// of a control may have. For example, one 2D vector may have a similar magnitude to another yet point in an
        /// entirely different direction.
        ///
        /// There are other conflict resolution mechanisms that could be used. For example, we could average the values
        /// from all controls. However, it would not necessarily result in more useful conflict resolution and would
        /// at the same time be much more expensive.
        /// </remarks>
        private bool ShouldIgnoreControlStateChange(ref TriggerState trigger, int actionIndex)
        {
            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount,
                "Action index out of range when checking for conflicting control input");

            // The goal of this method is to provide conflict resolution but do so ONLY if it is
            // really needed. In the vast majority of cases, this method should do almost nothing and
            // simply return straight away.

            // If conflict resolution is disabled on the action, early out. This is the case for pass-through
            // actions and for actions that cannot get into an ambiguous state based on the controls they
            // are bound to.
            var actionState = &actionStates[actionIndex];
            if (!actionState->mayNeedConflictResolution)
                return false;

            // Anything past here happens only for actions that may have conflicts.
            // Anything below here we want to avoid executing whenever we can.
            Debug.Assert(actionState->mayNeedConflictResolution);

            Profiler.BeginSample("InputActionResolveConflict");

            // Compute magnitude, if necessary.
            // NOTE: This will automatically take composites into account.
            if (!trigger.haveMagnitude)
                trigger.magnitude = ComputeMagnitude(trigger.bindingIndex, trigger.controlIndex);

            // Update magnitude stored in state.
            if (bindingStates[trigger.bindingIndex].isPartOfComposite)
            {
                // Control is part of a composite. Store magnitude in compositeMagnitudes.
                // NOTE: This path here implies that we never store magnitudes individually for controls
                //       that are part of composites.
                var compositeBindingIndex = bindingStates[trigger.bindingIndex].compositeOrCompositeBindingIndex;
                var compositeIndex = bindingStates[compositeBindingIndex].compositeOrCompositeBindingIndex;
                memory.compositeMagnitudes[compositeIndex] = trigger.magnitude;

                // For actions that need conflict resolution, we force TriggerState.controlIndex to the
                // first control in a composite. Otherwise it becomes much harder to tell if the we have
                // multiple concurrent actuations or not.
                // Since composites always evaluate as a whole instead of as single controls, having
                // trigger.controlIndex differ from the state monitor that fired should be fine.
                trigger.controlIndex = bindingStates[compositeBindingIndex].controlStartIndex;
                Debug.Assert(trigger.controlIndex >= 0 && trigger.controlIndex < totalControlCount,
                    "Control start index on composite binding out of range");
            }
            else
            {
                Debug.Assert(!bindingStates[trigger.bindingIndex].isComposite,
                    "Composite should not trigger directly from a control");

                // "Normal" control. Store magnitude in controlMagnitudes.
                memory.controlMagnitudes[trigger.controlIndex] = trigger.magnitude;
            }

            // If the control is actuated *more* than the current level of actuation we recorded for the
            // action, we process the state change normally. If this isn't the control that is already
            // driving the action, it will become the one now.
            //
            // NOTE: For composites, we're looking at the combined actuation of the entire binding here,
            //       not just at the actuation level of the individual control. ComputeMagnitude()
            //       automatically takes care of that for us.
            if (trigger.magnitude > actionState->magnitude)
            {
                // If this is not the control that is currently driving the action, we know
                // there are multiple controls that are concurrently actuated on the control.
                // Remember that so that when the controls are released again, we can more
                // efficiently determine whether we need to take multiple bound controls into
                // account or not.
                // NOTE: For composites, we have forced trigger.controlIndex to the first control
                //       in the composite. See above.
                if (trigger.magnitude > 0 && trigger.controlIndex != actionState->controlIndex && actionState->magnitude > 0)
                    actionState->hasMultipleConcurrentActuations = true;

                Profiler.EndSample();
                return false;
            }

            // If the control is actuated *less* then the current level of actuation we
            // recorded for the action *and* the control that changed is the one that is currently
            // driving the action, we have to check whether there is another actuation
            // that is now *higher* than what we're getting from the current control.
            if (trigger.magnitude < actionState->magnitude)
            {
                // If we're not currently driving the action, it's simple. Doesn't matter that we lowered
                // actuation as we didn't have the highest actuation anyway.
                if (trigger.controlIndex != actionState->controlIndex)
                {
                    Profiler.EndSample();
                    ////REVIEW: should we *count* actuations instead? (problem is that then we have to reliably determine when a control
                    ////        first actuates; the current solution will occasionally run conflict resolution when it doesn't have to
                    ////        but won't require the extra bookkepping)
                    // Do NOT let this control state change affect the action.
                    // NOTE: We do not update hasMultipleConcurrentActuations here which means that it may
                    //       temporarily be wrong. If that happens, we will end up eventually running the
                    //       conflict resolution code below even when we technically wouldn't need to but
                    //       it'll sync the actuation state.
                    return true;
                }

                // If we don't have multiple controls that are currently actuated, it's simple.
                if (!actionState->hasMultipleConcurrentActuations)
                {
                    Profiler.EndSample();
                    return false;
                }

                ////REVIEW: is there a simpler way we can do this???

                // So, now we know we are actually looking at a potential conflict. Multiple
                // controls bound to the action are actuated but we don't yet know whether
                // any of them is actuated *more* than the control that had just changed value.
                // Go through the bindings for the action and see what we've got.
                var bindingStartIndex = memory.actionBindingIndicesAndCounts[actionIndex * 2];
                var bindingCount = memory.actionBindingIndicesAndCounts[actionIndex * 2 + 1];
                var highestActuationLevel = trigger.magnitude;
                var controlWithHighestActuation = kInvalidIndex;
                var bindingWithHighestActuation = kInvalidIndex;
                var numActuations = 0;
                for (var i = 0; i < bindingCount; ++i)
                {
                    var bindingIndex = memory.actionBindingIndices[bindingStartIndex + i];
                    var binding = &memory.bindingStates[bindingIndex];

                    if (binding->isComposite)
                    {
                        // Composite bindings result in a single actuation value regardless of how
                        // many controls are bound through the parts of the composite.

                        var firstControlIndex = binding->controlStartIndex;
                        var compositeIndex = binding->compositeOrCompositeBindingIndex;

                        Debug.Assert(firstControlIndex >= 0 && firstControlIndex < totalControlCount,
                            "Control start index out of range on composite");
                        Debug.Assert(compositeIndex >= 0 && compositeIndex < totalCompositeCount,
                            "Composite index out of range on composite");

                        var magnitude = memory.compositeMagnitudes[compositeIndex];
                        if (magnitude > 0)
                            ++numActuations;
                        if (magnitude > highestActuationLevel)
                        {
                            controlWithHighestActuation = firstControlIndex;
                            bindingWithHighestActuation = controlIndexToBindingIndex[firstControlIndex];
                            highestActuationLevel = magnitude;
                        }
                    }
                    else if (!binding->isPartOfComposite)
                    {
                        // Check actuation of each control on the binding.
                        for (var n = 0; n < binding->controlCount; ++n)
                        {
                            var controlIndex = binding->controlStartIndex + n;
                            var magnitude = memory.controlMagnitudes[controlIndex];

                            if (magnitude > 0)
                                ++numActuations;

                            if (magnitude > highestActuationLevel)
                            {
                                controlWithHighestActuation = controlIndex;
                                bindingWithHighestActuation = bindingIndex;
                                highestActuationLevel = magnitude;
                            }
                        }
                    }
                }

                // Update our record of whether there are multiple concurrent actuations.
                if (numActuations <= 1)
                    actionState->hasMultipleConcurrentActuations = false;

                // If we didn't find a control with a higher actuation level, then go and process
                // the control value change.
                if (controlWithHighestActuation != kInvalidIndex)
                {
                    // We do have a control with a higher actuation level. Switch from our current
                    // control to processing the control with the now highest actuation level.
                    //
                    // NOTE: We are processing an artificial control state change here. Information
                    //       such as the timestamp will not correspond to when the control actually
                    //       changed value. However, if we skip processing this as a separate control
                    //       change here, interactions may not behave properly as they would not be
                    //       seeing that we just lowered the actuation level on the action.
                    trigger.controlIndex = controlWithHighestActuation;
                    trigger.bindingIndex = bindingWithHighestActuation;
                    trigger.magnitude = highestActuationLevel;

                    Profiler.EndSample();
                    return false;
                }
            }

            Profiler.EndSample();

            // If we're not really effecting any change on the action, ignore the control state change.
            // NOTE: We may be looking at a control here that points in a completely direction, for example, even
            //       though it has the same magnitude. However, we require a control to *higher* absolute actuation
            //       before we let it drive the action.
            if (Mathf.Approximately(trigger.magnitude, actionState->magnitude))
            {
                if (trigger.magnitude > 0 && trigger.controlIndex != actionState->controlIndex)
                    actionState->hasMultipleConcurrentActuations = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// When there is no interaction on an action, this method perform the default interaction logic that we
        /// run when a bound control changes value.
        /// </summary>
        /// <param name="trigger">Control trigger state.</param>
        /// <param name="actionIndex"></param>
        /// <remarks>
        /// The default interaction does not have its own <see cref="InteractionState"/>. Whatever we do in here,
        /// we store directly on the action state.
        /// </remarks>
        private void ProcessDefaultInteraction(ref TriggerState trigger, int actionIndex)
        {
            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount,
                "Action index out of range when processing default interaction");

            switch (actionStates[actionIndex].phase)
            {
                case InputActionPhase.Waiting:
                {
                    // Pass-through actions we perform on every value change and then go back
                    // to waiting.
                    if (trigger.passThrough)
                    {
                        ChangePhaseOfAction(InputActionPhase.Performed, ref trigger,
                            phaseAfterPerformedOrCancelled: InputActionPhase.Waiting);
                        break;
                    }

                    // Ignore if the control has not crossed its actuation threshold.
                    if (IsActuated(ref trigger))
                    {
                        // Go into started, then perform and then go back to started.
                        ChangePhaseOfAction(InputActionPhase.Started, ref trigger);
                        ChangePhaseOfAction(InputActionPhase.Performed, ref trigger,
                            phaseAfterPerformedOrCancelled: InputActionPhase.Started);
                    }

                    break;
                }

                case InputActionPhase.Started:
                {
                    if (!IsActuated(ref trigger))
                    {
                        // Control went back to below actuation threshold. Cancel interaction.
                        ChangePhaseOfAction(InputActionPhase.Cancelled, ref trigger);
                    }
                    else
                    {
                        // Control changed value above magnitude threshold. Perform and remain started.
                        ChangePhaseOfAction(InputActionPhase.Performed, ref trigger,
                            phaseAfterPerformedOrCancelled: InputActionPhase.Started);
                    }
                    break;
                }

                default:
                    Debug.Assert(false, "Should not get here");
                    break;
            }
        }

        private void ProcessInteractions(ref TriggerState trigger, int interactionStartIndex, int interactionCount)
        {
            var context = new InputInteractionContext
            {
                m_State = this,
                m_TriggerState = trigger
            };

            for (var i = 0; i < interactionCount; ++i)
            {
                var index = interactionStartIndex + i;
                var state = interactionStates[index];
                var interaction = interactions[index];

                context.m_TriggerState.phase = state.phase;
                context.m_TriggerState.startTime = state.startTime;
                context.m_TriggerState.interactionIndex = index;

                interaction.Process(ref context);
            }
        }

        private void ProcessTimeout(double time, int mapIndex, int controlIndex, int bindingIndex, int interactionIndex)
        {
            Debug.Assert(controlIndex >= 0 && controlIndex < totalControlCount, "Control index out of range");
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");
            Debug.Assert(interactionIndex >= 0 && interactionIndex < totalInteractionCount, "Interaction index out of range");

            var currentState = interactionStates[interactionIndex];
            var actionIndex = bindingStates[bindingIndex].actionIndex;

            var context = new InputInteractionContext
            {
                m_State = this,
                m_TriggerState =
                    new TriggerState
                {
                    phase = currentState.phase,
                    time = time,
                    mapIndex = mapIndex,
                    controlIndex = controlIndex,
                    bindingIndex = bindingIndex,
                    interactionIndex = interactionIndex,
                    continuous = actionStates[actionIndex].continuous,
                },
                timerHasExpired = true,
            };

            currentState.isTimerRunning = false;
            interactionStates[interactionIndex] = currentState;

            // Let interaction handle timer expiration.
            interactions[interactionIndex].Process(ref context);
        }

        internal void StartTimeout(float seconds, ref TriggerState trigger)
        {
            Debug.Assert(trigger.mapIndex >= 0 && trigger.mapIndex < totalMapCount, "Map index out of range");
            Debug.Assert(trigger.controlIndex >= 0 && trigger.controlIndex < totalControlCount, "Control index out of range");
            Debug.Assert(trigger.interactionIndex >= 0 && trigger.interactionIndex < totalInteractionCount, "Interaction index out of range");

            var manager = InputSystem.s_Manager;
            var currentTime = trigger.time;
            var control = controls[trigger.controlIndex];
            var interactionIndex = trigger.interactionIndex;
            var monitorIndex =
                ToCombinedMapAndControlAndBindingIndex(trigger.mapIndex, trigger.controlIndex, trigger.bindingIndex);

            // If there's already a timeout running, cancel it first.
            if (interactionStates[interactionIndex].isTimerRunning)
                StopTimeout(trigger.mapIndex, trigger.controlIndex, trigger.bindingIndex, interactionIndex);

            // Add new timeout.
            manager.AddStateChangeMonitorTimeout(control, this, currentTime + seconds, monitorIndex,
                interactionIndex);

            // Update state.
            var interactionState = interactionStates[interactionIndex];
            interactionState.isTimerRunning = true;
            interactionStates[interactionIndex] = interactionState;
        }

        private void StopTimeout(int mapIndex, int controlIndex, int bindingIndex, int interactionIndex)
        {
            Debug.Assert(mapIndex >= 0 && mapIndex < totalMapCount, "Map index out of range");
            Debug.Assert(controlIndex >= 0 && controlIndex < totalControlCount, "Control index out of range");
            Debug.Assert(interactionIndex >= 0 && interactionIndex < totalInteractionCount, "Interaction index out of range");

            var manager = InputSystem.s_Manager;
            var monitorIndex =
                ToCombinedMapAndControlAndBindingIndex(mapIndex, controlIndex, bindingIndex);

            manager.RemoveStateChangeMonitorTimeout(this, monitorIndex, interactionIndex);

            // Update state.
            var interactionState = interactionStates[interactionIndex];
            interactionState.isTimerRunning = false;
            interactionStates[interactionIndex] = interactionState;
        }

        /// <summary>
        /// Perform a phase change on the given interaction. Only visible to observers
        /// if it happens to change the phase of the action, too.
        /// </summary>
        /// <param name="newPhase">New phase to transition the interaction to.</param>
        /// <param name="trigger">Information about the binding and control that triggered the phase change.</param>
        /// <param name="phaseAfterPerformed">If <paramref name="newPhase"/> is <see cref="InputActionPhase.Performed"/>,
        /// this determines which phase to transition to after the action has been performed. This would usually be
        /// <see cref="InputActionPhase.Waiting"/> (default), <see cref="InputActionPhase.Started"/> (if the action is supposed
        /// to be oscillate between started and performed), or <see cref="InputActionPhase.Performed"/> (if the action is
        /// supposed to perform over and over again until cancelled).</param>
        /// <remarks>
        /// Multiple interactions on the same binding can be started concurrently but the
        /// first interaction that starts will get to drive an action until it either cancels
        /// or performs the action.
        ///
        /// If an interaction driving an action performs it, all interactions will reset and
        /// go back waiting.
        ///
        /// If an interaction driving an action cancels it, the next interaction in the list which
        /// has already started will get to drive the action (example: a TapInteraction and a
        /// SlowTapInteraction both start and the TapInteraction gets to drive the action because
        /// it comes first; then the TapInteraction cancels because the button is held for too
        /// long and the SlowTapInteraction will get to drive the action next).
        /// </remarks>
        internal void ChangePhaseOfInteraction(InputActionPhase newPhase, ref TriggerState trigger,
            InputActionPhase phaseAfterPerformed = InputActionPhase.Waiting)
        {
            var interactionIndex = trigger.interactionIndex;
            var bindingIndex = trigger.bindingIndex;

            Debug.Assert(interactionIndex >= 0 && interactionIndex < totalInteractionCount, "Interaction index out of range");
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");

            ////TODO: need to make sure that performed and cancelled phase changes happen on the *same* binding&control
            ////      as the start of the phase

            var phaseAfterPerformedOrCancelled = InputActionPhase.Waiting;
            if (newPhase == InputActionPhase.Performed)
                phaseAfterPerformedOrCancelled = phaseAfterPerformed;

            // Any time an interaction changes phase, we cancel all pending timeouts.
            if (interactionStates[interactionIndex].isTimerRunning)
                StopTimeout(trigger.mapIndex, trigger.controlIndex, trigger.bindingIndex, trigger.interactionIndex);

            // Update interaction state.
            interactionStates[interactionIndex].phase = newPhase;
            interactionStates[interactionIndex].triggerControlIndex = trigger.controlIndex;
            if (newPhase == InputActionPhase.Started)
                interactionStates[interactionIndex].startTime = trigger.time;

            ////REVIEW: If we want to defer triggering of actions, this is the point where we probably need to cut things off
            // See if it affects the phase of an associated action.
            var actionIndex = bindingStates[bindingIndex].actionIndex; // We already had to tap this array and entry in ProcessControlStateChange.
            if (actionIndex != -1)
            {
                if (actionStates[actionIndex].phase == InputActionPhase.Waiting)
                {
                    // We're the first interaction to go to the start phase.
                    ChangePhaseOfAction(newPhase, ref trigger,
                        phaseAfterPerformedOrCancelled: phaseAfterPerformedOrCancelled);
                }
                else if (newPhase == InputActionPhase.Cancelled && actionStates[actionIndex].interactionIndex == trigger.interactionIndex)
                {
                    // We're cancelling but maybe there's another interaction ready
                    // to go into start phase.

                    ChangePhaseOfAction(newPhase, ref trigger);

                    var interactionStartIndex = bindingStates[bindingIndex].interactionStartIndex;
                    var numInteractions = bindingStates[bindingIndex].interactionCount;
                    for (var i = 0; i < numInteractions; ++i)
                    {
                        var index = interactionStartIndex + i;
                        if (index != trigger.interactionIndex && interactionStates[index].phase == InputActionPhase.Started)
                        {
                            ////REVIEW: does this handle continuous mode correctly?
                            var triggerForInteraction = new TriggerState
                            {
                                phase = InputActionPhase.Started,
                                controlIndex = interactionStates[index].triggerControlIndex,
                                bindingIndex = trigger.bindingIndex,
                                interactionIndex = index,
                                time = trigger.time,
                                startTime = interactionStates[index].startTime
                            };
                            ChangePhaseOfAction(InputActionPhase.Started, ref triggerForInteraction);
                            break;
                        }
                    }
                }
                else if (actionStates[actionIndex].interactionIndex == trigger.interactionIndex)
                {
                    // Any other phase change goes to action if we're the interaction driving
                    // the current phase.
                    ChangePhaseOfAction(newPhase, ref trigger, phaseAfterPerformedOrCancelled);

                    // We're the interaction driving the action and we performed the action,
                    // so reset any other interaction to waiting state.
                    if (newPhase == InputActionPhase.Performed)
                    {
                        var interactionStartIndex = bindingStates[bindingIndex].interactionStartIndex;
                        var numInteractions = bindingStates[bindingIndex].interactionCount;
                        for (var i = 0; i < numInteractions; ++i)
                        {
                            var index = interactionStartIndex + i;
                            if (index != trigger.interactionIndex)
                                ResetInteractionState(trigger.mapIndex, trigger.bindingIndex, index);
                        }
                    }
                }
            }

            // If the interaction performed or cancelled, go back to waiting.
            // Exception: if it was performed and we're to remain in started state, set the interaction
            //            to started. Note that for that phase transition, there are no callbacks being
            //            triggered (i.e. we don't call 'started' every time after 'performed').
            if (newPhase == InputActionPhase.Performed && phaseAfterPerformed != InputActionPhase.Waiting)
            {
                interactionStates[interactionIndex].phase = phaseAfterPerformed;
            }
            else if (newPhase == InputActionPhase.Performed || newPhase == InputActionPhase.Cancelled)
            {
                ResetInteractionState(trigger.mapIndex, trigger.bindingIndex, trigger.interactionIndex);
            }
            ////TODO: reset entire chain
        }

        /// <summary>
        /// Change the current phase of the action referenced by <paramref name="trigger"/> to <paramref name="newPhase"/>.
        /// </summary>
        /// <param name="newPhase">New phase to transition to.</param>
        /// <param name="trigger">Trigger that caused the change in phase.</param>
        /// <param name="phaseAfterPerformedOrCancelled"></param>
        /// <remarks>
        /// The change in phase is visible to observers, i.e. on the various callbacks and notifications.
        ///
        /// If <paramref name="newPhase"/> is <see cref="InputActionPhase.Performed"/> or <see cref="InputActionPhase.Cancelled"/>,
        /// the action will subsequently immediately transition to <paramref name="phaseAfterPerformedOrCancelled"/>
        /// (<see cref="InputActionPhase.Waiting"/> by default). This change is not visible to observers, i.e. there won't
        /// be another run through callbacks.
        /// </remarks>
        private void ChangePhaseOfAction(InputActionPhase newPhase, ref TriggerState trigger,
            InputActionPhase phaseAfterPerformedOrCancelled = InputActionPhase.Waiting)
        {
            Debug.Assert(trigger.mapIndex >= 0 && trigger.mapIndex < totalMapCount, "Map index out of range");
            Debug.Assert(trigger.controlIndex >= 0 && trigger.controlIndex < totalControlCount, "Control index out of range");
            Debug.Assert(trigger.bindingIndex >= 0 && trigger.bindingIndex < totalBindingCount, "Binding index out of range");

            var actionIndex = bindingStates[trigger.bindingIndex].actionIndex;
            if (actionIndex == kInvalidIndex)
                return; // No action associated with binding.

            // Update action state.
            var actionState = &actionStates[actionIndex];
            Debug.Assert(trigger.mapIndex == actionState->mapIndex,
                "Map index on trigger does not correspond to map index of trigger state");
            var newState = trigger;
            newState.flags = actionState->flags;
            newState.phase = newPhase;
            if (!newState.haveMagnitude)
                newState.magnitude = ComputeMagnitude(trigger.bindingIndex, trigger.controlIndex);
            *actionState = newState;

            // Let listeners know.
            var map = maps[trigger.mapIndex];
            Debug.Assert(actionIndex >= mapIndices[trigger.mapIndex].actionStartIndex,
                "actionIndex is below actionStartIndex for map that the action belongs to");
            var action = map.m_Actions[actionIndex - mapIndices[trigger.mapIndex].actionStartIndex];
            trigger.phase = newPhase;
            switch (newPhase)
            {
                case InputActionPhase.Started:
                {
                    CallActionListeners(actionIndex, map, newPhase, ref action.m_OnStarted);
                    break;
                }

                case InputActionPhase.Performed:
                {
                    CallActionListeners(actionIndex, map, newPhase, ref action.m_OnPerformed);
                    actionState->phase = phaseAfterPerformedOrCancelled;

                    // If the action is continuous and remains in performed or started state, make sure the action
                    // is on the list of continuous actions that we check every update.
                    if ((phaseAfterPerformedOrCancelled == InputActionPhase.Started ||
                         phaseAfterPerformedOrCancelled == InputActionPhase.Performed) &&
                        actionState->continuous &&
                        !actionState->onContinuousList)
                    {
                        AddContinuousAction(actionIndex);
                    }
                    break;
                }

                case InputActionPhase.Cancelled:
                {
                    CallActionListeners(actionIndex, map, newPhase, ref action.m_OnCancelled);
                    actionState->phase = phaseAfterPerformedOrCancelled;

                    // Remove from list of continuous actions, if necessary.
                    if (actionState->onContinuousList)
                        RemoveContinuousAction(actionIndex);
                    break;
                }

                case InputActionPhase.Waiting:
                {
                    if (actionState->onContinuousList)
                        RemoveContinuousAction(actionIndex);
                    break;
                }
            }
        }

        private void CallActionListeners(int actionIndex, InputActionMap actionMap, InputActionPhase phase, ref InlinedArray<InputActionListener> listeners)
        {
            // If there's no listeners, don't bother with anything else.
            var callbacksOnMap = actionMap.m_ActionCallbacks;
            if (listeners.length == 0 && callbacksOnMap.length == 0 && s_OnActionChange.length == 0)
                return;

            var context = new InputAction.CallbackContext
            {
                m_State = this,
                m_ActionIndex = actionIndex,
            };

            Profiler.BeginSample("InputActionCallback");

            // Global callback goes first.
            if (s_OnActionChange.length > 0)
            {
                var action = context.action;

                InputActionChange change;
                switch (phase)
                {
                    case InputActionPhase.Started:
                        change = InputActionChange.ActionStarted;
                        break;
                    case InputActionPhase.Performed:
                        change = InputActionChange.ActionPerformed;
                        break;
                    case InputActionPhase.Cancelled:
                        change = InputActionChange.ActionCancelled;
                        break;
                    default:
                        Debug.Assert(false, "Should not reach here");
                        return;
                }

                for (var i = 0; i < s_OnActionChange.length; ++i)
                    s_OnActionChange[i](action, change);
            }

            // Run callbacks (if any) directly on action.
            var listenerCount = listeners.length;
            for (var i = 0; i < listenerCount; ++i)
            {
                try
                {
                    listeners[i](context);
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"{exception.GetType().Name} thrown during execution of '{phase}' callback on action '{GetActionOrNull(ref actionStates[actionIndex])}'");
                    Debug.LogException(exception);
                }
            }

            // Run callbacks (if any) on action map.
            var listenerCountOnMap = callbacksOnMap.length;
            for (var i = 0; i < listenerCountOnMap; ++i)
            {
                try
                {
                    callbacksOnMap[i](context);
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"{exception.GetType().Name} thrown during execution of callback for '{phase}' phase of '{GetActionOrNull(ref actionStates[actionIndex]).name}' action in map '{actionMap.name}'");
                    Debug.LogException(exception);
                }
            }

            Profiler.EndSample();
        }

        private object GetActionOrNoneString(ref TriggerState trigger)
        {
            var action = GetActionOrNull(ref trigger);
            if (action == null)
                return "<none>";
            return action;
        }

        internal InputAction GetActionOrNull(int bindingIndex)
        {
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");

            var actionIndex = bindingStates[bindingIndex].actionIndex;
            if (actionIndex == kInvalidIndex)
                return null;

            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount,
                "Action index out of range when getting action");
            var mapIndex = bindingStates[bindingIndex].mapIndex;
            var actionStartIndex = mapIndices[mapIndex].actionStartIndex;
            return maps[mapIndex].m_Actions[actionIndex - actionStartIndex];
        }

        internal InputAction GetActionOrNull(ref TriggerState trigger)
        {
            Debug.Assert(trigger.mapIndex >= 0 && trigger.mapIndex < totalMapCount, "Map index out of range");
            Debug.Assert(trigger.bindingIndex >= 0 && trigger.bindingIndex < totalBindingCount, "Binding index out of range");

            var actionIndex = bindingStates[trigger.bindingIndex].actionIndex;
            if (actionIndex == kInvalidIndex)
                return null;

            Debug.Assert(actionIndex >= 0 && actionIndex < totalActionCount, "Action index out of range");
            var actionStartIndex = mapIndices[trigger.mapIndex].actionStartIndex;
            return maps[trigger.mapIndex].m_Actions[actionIndex - actionStartIndex];
        }

        internal InputControl GetControl(ref TriggerState trigger)
        {
            Debug.Assert(trigger.controlIndex != kInvalidIndex, "Control index is invalid");
            Debug.Assert(trigger.controlIndex >= 0 && trigger.controlIndex < totalControlCount, "Control index out of range");
            return controls[trigger.controlIndex];
        }

        private IInputInteraction GetInteractionOrNull(ref TriggerState trigger)
        {
            if (trigger.interactionIndex == kInvalidIndex)
                return null;

            Debug.Assert(trigger.interactionIndex >= 0 && trigger.interactionIndex < totalInteractionCount, "Interaction index out of range");
            return interactions[trigger.interactionIndex];
        }

        internal InputBinding GetBinding(int bindingIndex)
        {
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");
            var mapIndex = bindingStates[bindingIndex].mapIndex;
            var bindingStartIndex = mapIndices[mapIndex].bindingStartIndex;
            return maps[mapIndex].m_Bindings[bindingIndex - bindingStartIndex];
        }

        private void ResetInteractionStateAndCancelIfNecessary(int mapIndex, int bindingIndex, int interactionIndex)
        {
            Debug.Assert(interactionIndex >= 0 && interactionIndex < totalInteractionCount, "Interaction index out of range");
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");

            // If interaction is currently driving an action and it has been started or performed,
            // cancel it.
            //
            // NOTE: We could just blindly call ChangePhaseOfInteraction() and it would handle the case of
            //       when the interaction is currently driving the action automatically. However, doing so
            //       would give other interactions a chance to take over which is something we don't want to
            //       happen when resetting actions.
            var actionIndex = bindingStates[bindingIndex].actionIndex;
            if (actionStates[actionIndex].interactionIndex == interactionIndex)
            {
                switch (interactionStates[interactionIndex].phase)
                {
                    case InputActionPhase.Started:
                    case InputActionPhase.Performed:
                        ChangePhaseOfInteraction(InputActionPhase.Cancelled, ref actionStates[actionIndex]);
                        break;
                }
            }

            ResetInteractionState(mapIndex, bindingIndex, interactionIndex);
        }

        private void ResetInteractionState(int mapIndex, int bindingIndex, int interactionIndex)
        {
            Debug.Assert(interactionIndex >= 0 && interactionIndex < totalInteractionCount, "Interaction index out of range");
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");

            // Clean up internal state that the interaction may keep.
            interactions[interactionIndex].Reset();

            // Clean up timer.
            if (interactionStates[interactionIndex].isTimerRunning)
            {
                var controlIndex = interactionStates[interactionIndex].triggerControlIndex;
                StopTimeout(mapIndex, controlIndex, bindingIndex, interactionIndex);
            }

            // Reset state record.
            interactionStates[interactionIndex] =
                new InteractionState
            {
                // We never set interactions to disabled. This way we don't have to go through them
                // when we disable/enable actions.
                phase = InputActionPhase.Waiting,
            };
        }

        internal int GetValueSizeInBytes(int bindingIndex, int controlIndex)
        {
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");
            Debug.Assert(controlIndex >= 0 && controlIndex < totalControlCount, "Control index out of range");

            if (bindingStates[bindingIndex].isPartOfComposite) ////TODO: instead, just have compositeOrCompositeBindingIndex be invalid
            {
                var compositeBindingIndex = bindingStates[bindingIndex].compositeOrCompositeBindingIndex;
                var compositeIndex = bindingStates[compositeBindingIndex].compositeOrCompositeBindingIndex;
                var compositeObject = composites[compositeIndex];
                Debug.Assert(compositeObject != null);

                return compositeObject.valueSizeInBytes;
            }

            var control = controls[controlIndex];
            Debug.Assert(control != null);
            return control.valueSizeInBytes;
        }

        internal Type GetValueType(int bindingIndex, int controlIndex)
        {
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");
            Debug.Assert(controlIndex >= 0 && controlIndex < totalControlCount, "Control index out of range");

            if (bindingStates[bindingIndex].isPartOfComposite) ////TODO: instead, just have compositeOrCompositeBindingIndex be invalid
            {
                var compositeBindingIndex = bindingStates[bindingIndex].compositeOrCompositeBindingIndex;
                var compositeIndex = bindingStates[compositeBindingIndex].compositeOrCompositeBindingIndex;
                var compositeObject = composites[compositeIndex];
                Debug.Assert(compositeObject != null, "Composite object is null");

                return compositeObject.valueType;
            }

            var control = controls[controlIndex];
            Debug.Assert(control != null, "Control is null");
            return control.valueType;
        }

        internal bool IsActuated(ref TriggerState trigger, float threshold = 0)
        {
            if (!trigger.haveMagnitude)
                trigger.magnitude = ComputeMagnitude(trigger.bindingIndex, trigger.controlIndex);

            if (trigger.magnitude < 0)
                return true;

            if (Mathf.Approximately(threshold, 0))
                return trigger.magnitude > 0;

            return trigger.magnitude >= threshold;
        }

        private float ComputeMagnitude(int bindingIndex, int controlIndex)
        {
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index is out of range");
            Debug.Assert(controlIndex >= 0 && controlIndex < totalControlCount, "Control index is out of range");

            if (bindingStates[bindingIndex].isPartOfComposite)
            {
                var compositeBindingIndex = bindingStates[bindingIndex].compositeOrCompositeBindingIndex;
                var compositeIndex = bindingStates[compositeBindingIndex].compositeOrCompositeBindingIndex;
                var compositeObject = composites[compositeIndex];

                var context = new InputBindingCompositeContext
                {
                    m_State = this,
                    m_BindingIndex = compositeBindingIndex
                };

                return compositeObject.EvaluateMagnitude(ref context);
            }

            var control = controls[controlIndex];
            if (control.CheckStateIsAtDefault())
            {
                // Avoid magnitude computation if control state is at default.
                return 0;
            }

            return control.EvaluateMagnitude();
        }

        ////REVIEW: we can unify the reading paths once we have blittable type constraints

        internal void ReadValue(int bindingIndex, int controlIndex, void* buffer, int bufferSize)
        {
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index out of range");
            Debug.Assert(controlIndex >= 0 && controlIndex < totalControlCount, "Control index out of range");

            InputControl control = null;

            // If the binding that triggered the action is part of a composite, let
            // the composite determine the value we return.
            if (bindingStates[bindingIndex].isPartOfComposite)
            {
                var compositeBindingIndex = bindingStates[bindingIndex].compositeOrCompositeBindingIndex;
                var compositeIndex = bindingStates[compositeBindingIndex].compositeOrCompositeBindingIndex;
                var compositeObject = composites[compositeIndex];
                Debug.Assert(compositeObject != null, "Composite object is null");

                var context = new InputBindingCompositeContext
                {
                    m_State = this,
                    m_BindingIndex = compositeBindingIndex
                };

                compositeObject.ReadValue(ref context, buffer, bufferSize);
            }
            else
            {
                control = controls[controlIndex];
                Debug.Assert(control != null, "Control is null");
                control.ReadValueIntoBuffer(buffer, bufferSize);
            }

            // Run value through processors, if any.
            var processorCount = bindingStates[bindingIndex].processorCount;
            if (processorCount > 0)
            {
                var processorStartIndex = bindingStates[bindingIndex].processorStartIndex;
                for (var i = 0; i < processorCount; ++i)
                    processors[processorStartIndex + i].Process(buffer, bufferSize, control);
            }
        }

        internal TValue ReadValue<TValue>(int bindingIndex, int controlIndex, bool ignoreComposites = false)
            where TValue : struct
        {
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index is out of range");
            Debug.Assert(controlIndex >= 0 && controlIndex < totalControlCount, "Control index is out of range");

            var value = default(TValue);

            // In the case of a composite, this will be null.
            InputControl<TValue> controlOfType = null;

            // If the binding that triggered the action is part of a composite, let
            // the composite determine the value we return.
            if (!ignoreComposites && bindingStates[bindingIndex].isPartOfComposite)
            {
                var compositeBindingIndex = bindingStates[bindingIndex].compositeOrCompositeBindingIndex;
                Debug.Assert(compositeBindingIndex >= 0 && compositeBindingIndex < totalBindingCount);
                var compositeIndex = bindingStates[compositeBindingIndex].compositeOrCompositeBindingIndex;
                var compositeObject = composites[compositeIndex];
                Debug.Assert(compositeObject != null, "Composite object is null");

                var compositeOfType = compositeObject as InputBindingComposite<TValue>;
                if (compositeOfType == null)
                    throw new InvalidOperationException(
                        $"Cannot read value of type '{typeof(TValue).Name}' from composite '{compositeObject}' bound to action '{GetActionOrNull(bindingIndex)}' (composite is a '{compositeIndex.GetType().Name}' with value type '{TypeHelpers.GetNiceTypeName(compositeObject.GetType().GetGenericArguments()[0])}')");

                var context = new InputBindingCompositeContext
                {
                    m_State = this,
                    m_BindingIndex = compositeBindingIndex
                };

                value = compositeOfType.ReadValue(ref context);
            }
            else
            {
                var control = controls[controlIndex];
                Debug.Assert(control != null, "Control is null");

                controlOfType = control as InputControl<TValue>;
                if (controlOfType == null)
                    throw new InvalidOperationException(
                        $"Cannot read value of type '{TypeHelpers.GetNiceTypeName(typeof(TValue))}' from control '{control.path}' bound to action '{GetActionOrNull(bindingIndex)}' (control is a '{control.GetType().Name}' with value type '{TypeHelpers.GetNiceTypeName(control.valueType)}')");

                value = controlOfType.ReadValue();
            }

            // Run value through processors, if any.
            var processorCount = bindingStates[bindingIndex].processorCount;
            if (processorCount > 0)
            {
                var processorStartIndex = bindingStates[bindingIndex].processorStartIndex;
                for (var i = 0; i < processorCount; ++i)
                {
                    var processor = processors[processorStartIndex + i] as InputProcessor<TValue>;
                    if (processor != null)
                        value = processor.Process(value, controlOfType);
                }
            }

            return value;
        }

        /// <summary>
        /// Read the value of the given part of a composite binding.
        /// </summary>
        /// <param name="bindingIndex">Index of the composite binding in <see cref="bindingStates"/>.</param>
        /// <param name="partNumber">Index of the part. Note that part indices start at 1!</param>
        /// <typeparam name="TValue">Value type to read. Must correspond to the value of bound controls or an exception will
        /// be thrown.</typeparam>
        /// <returns>Greatest value from among the bound controls for the given part.</returns>
        /// <remarks>
        /// Composites are composed of "parts". Each part has an associated name (e.g. "negative" or "positive") which is
        /// referenced by <see cref="InputBinding.name"/> of bindings that are part of the composite. However, multiple
        /// bindings may reference the same part (e.g. there could be a binding for "W" and another binding for "UpArrow"
        /// and both would reference the "Up" part).
        ///
        /// However, a given composite will only be interested in a single value for any given part. What we do is give
        /// a composite an integer key for every part. When it asks for a value for the given part, we go through all
        /// bindings that reference the given part and return the greatest value from among the controls of all those
        /// bindings.
        ///
        /// <example>
        /// <code>
        /// // Read a float value from the second part of the composite binding at index 3.
        /// ReadCompositePartValue&lt;float&gt;(3, 2);
        /// </code>
        /// </example>
        /// </remarks>
        internal TValue ReadCompositePartValue<TValue>(int bindingIndex, int partNumber)
            where TValue : struct, IComparable<TValue>
        {
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index is out of range");
            Debug.Assert(bindingStates[bindingIndex].isComposite, "Binding must be a composite");

            var result = default(TValue);
            var firstChildBindingIndex = bindingIndex + 1;
            var isFirstValue = true;

            // Find the binding in the composite that both has the given part number and
            // the greatest value.
            //
            // NOTE: It is tempting to go by control magnitudes instead as those are readily available to us (controlMagnitudes)
            //       and avoids us reading values that we're not going to use. Unfortunately, we can't do that as several controls
            //       used by a composite may all have been updated with a single event (e.g. WASD on a keyboard will usually see
            //       just one update that refreshes the entire state of the keyboard). In that case, one of the controls will
            //       see its state monitor trigger first and in turn trigger processing of the action and composite. Thus only
            //       that one single control would have its value refreshed in controlMagnitudes whereas the other control magnitudes
            //       would be stale.
            for (var index = firstChildBindingIndex; index < totalBindingCount && bindingStates[index].isPartOfComposite; ++index)
            {
                if (bindingStates[index].partIndex != partNumber)
                    continue;

                var controlCount = bindingStates[index].controlCount;
                var controlStartIndex = bindingStates[index].controlStartIndex;
                for (var i = 0; i < controlCount; ++i)
                {
                    var controlIndex = controlStartIndex + i;
                    var value = ReadValue<TValue>(index, controlIndex, ignoreComposites: true);

                    if (isFirstValue)
                    {
                        result = value;
                        isFirstValue = false;
                    }
                    else if (value.CompareTo(result) > 0)
                    {
                        result = value;
                    }
                }
            }

            return result;
        }

        internal object ReadValueAsObject(int bindingIndex, int controlIndex)
        {
            Debug.Assert(bindingIndex >= 0 && bindingIndex < totalBindingCount, "Binding index is out of range");
            Debug.Assert(controlIndex >= 0 && controlIndex < totalControlCount, "Control index is out of range");

            InputControl control = null;
            object value;

            // If the binding that triggered the action is part of a composite, let
            // the composite determine the value we return.
            if (bindingStates[bindingIndex].isPartOfComposite) ////TODO: instead, just have compositeOrCompositeBindingIndex be invalid
            {
                var compositeBindingIndex = bindingStates[bindingIndex].compositeOrCompositeBindingIndex;
                Debug.Assert(compositeBindingIndex >= 0 && compositeBindingIndex < totalBindingCount, "Binding index is out of range");
                var compositeIndex = bindingStates[compositeBindingIndex].compositeOrCompositeBindingIndex;
                var compositeObject = composites[compositeIndex];
                Debug.Assert(compositeObject != null, "Composite object is null");

                var context = new InputBindingCompositeContext
                {
                    m_State = this,
                    m_BindingIndex = compositeBindingIndex
                };

                value = compositeObject.ReadValueAsObject(ref context);
            }
            else
            {
                control = controls[controlIndex];
                Debug.Assert(control != null, "Control is null");
                value = control.ReadValueAsObject();
            }

            // Run value through processors, if any.
            var processorCount = bindingStates[bindingIndex].processorCount;
            if (processorCount > 0)
            {
                var processorStartIndex = bindingStates[bindingIndex].processorStartIndex;
                for (var i = 0; i < processorCount; ++i)
                    value = processors[processorStartIndex + i].ProcessAsObject(value, control);
            }

            return value;
        }

        /// <summary>
        /// Records the current state of a single interaction attached to a binding.
        /// Each interaction keeps track of its own trigger control and phase progression.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 12)]
        internal struct InteractionState
        {
            [FieldOffset(0)] private ushort m_TriggerControlIndex;
            [FieldOffset(2)] private byte m_Phase;
            [FieldOffset(3)] private byte m_Flags;
            [FieldOffset(4)] private double m_StartTime;

            public int triggerControlIndex
            {
                get => m_TriggerControlIndex;
                set
                {
                    Debug.Assert(value >= 0 && value <= ushort.MaxValue);
                    if (value < 0 || value > ushort.MaxValue)
                        throw new NotSupportedException("Cannot have more than ushort.MaxValue controls in a single InputActionState");
                    m_TriggerControlIndex = (ushort)value;
                }
            }

            public double startTime
            {
                get => m_StartTime;
                set => m_StartTime = value;
            }

            public bool isTimerRunning
            {
                get => ((Flags)m_Flags & Flags.TimerRunning) == Flags.TimerRunning;
                set
                {
                    if (value)
                        m_Flags |= (byte)Flags.TimerRunning;
                    else
                    {
                        var mask = ~Flags.TimerRunning;
                        m_Flags &= (byte)mask;
                    }
                }
            }

            public InputActionPhase phase
            {
                get => (InputActionPhase)m_Phase;
                set => m_Phase = (byte)value;
            }

            [Flags]
            private enum Flags
            {
                TimerRunning = 1 << 0,
            }
        }

        /// <summary>
        /// Runtime state for a single binding.
        /// </summary>
        /// <remarks>
        /// Correlated to the <see cref="InputBinding"/> it corresponds to by the index in the binding
        /// array.
        /// </remarks>
        [StructLayout(LayoutKind.Explicit, Size = 20)]
        internal struct BindingState
        {
            [FieldOffset(0)] private byte m_ControlCount;
            [FieldOffset(1)] private byte m_InteractionCount;
            [FieldOffset(2)] private byte m_ProcessorCount;
            [FieldOffset(3)] private byte m_MapIndex;
            [FieldOffset(4)] private byte m_Flags;
            [FieldOffset(5)] private byte m_PartIndex;
            [FieldOffset(6)] private ushort m_ActionIndex;
            [FieldOffset(8)] private ushort m_CompositeOrCompositeBindingIndex;
            [FieldOffset(10)] private ushort m_ProcessorStartIndex;
            [FieldOffset(12)] private ushort m_InteractionStartIndex;
            [FieldOffset(14)] private ushort m_ControlStartIndex;
            [FieldOffset(16)] private int m_TriggerEventIdForComposite;

            [Flags]
            public enum Flags
            {
                ChainsWithNext = 1 << 0,
                EndOfChain = 1 << 1,
                Composite = 1 << 2,
                PartOfComposite = 1 << 3,
                InitialStateCheckPending = 1 << 4,
                WantsInitialStateCheck = 1 << 5,
            }

            /// <summary>
            /// Index into <see cref="controls"/> of first control associated with the binding.
            /// </summary>
            /// <remarks>
            /// For composites, this is the index of the first control that is bound by any of the parts in the composite.
            /// </remarks>
            public int controlStartIndex
            {
                get => m_ControlStartIndex;
                set
                {
                    Debug.Assert(value != kInvalidIndex);
                    if (value >= ushort.MaxValue)
                        throw new NotSupportedException("Total control count in state cannot exceed byte.MaxValue=" + ushort.MaxValue);
                    m_ControlStartIndex = (ushort)value;
                }
            }

            /// <summary>
            /// Number of controls associated with this binding.
            /// </summary>
            /// <remarks>
            /// For composites, this is the total number of controls bound by all parts of the composite combined.
            /// </remarks>
            public int controlCount
            {
                get => m_ControlCount;
                set
                {
                    if (value >= byte.MaxValue)
                        throw new NotSupportedException("Control count per binding cannot exceed byte.MaxValue=" + byte.MaxValue);
                    m_ControlCount = (byte)value;
                }
            }

            /// <summary>
            /// Index into <see cref="InputActionState.interactionStates"/> of first interaction associated with the binding.
            /// </summary>
            public int interactionStartIndex
            {
                get
                {
                    if (m_InteractionStartIndex == ushort.MaxValue)
                        return kInvalidIndex;
                    return m_InteractionStartIndex;
                }
                set
                {
                    if (value == kInvalidIndex)
                        m_InteractionStartIndex = ushort.MaxValue;
                    else
                    {
                        if (value >= ushort.MaxValue)
                            throw new NotSupportedException("Interaction count cannot exceed ushort.MaxValue=" + ushort.MaxValue);
                        m_InteractionStartIndex = (ushort)value;
                    }
                }
            }

            /// <summary>
            /// Number of interactions associated with this binding.
            /// </summary>
            public int interactionCount
            {
                get => m_InteractionCount;
                set
                {
                    if (value >= byte.MaxValue)
                        throw new NotSupportedException("Interaction count per binding cannot exceed byte.MaxValue=" + byte.MaxValue);
                    m_InteractionCount = (byte)value;
                }
            }

            public int processorStartIndex
            {
                get
                {
                    if (m_ProcessorStartIndex == ushort.MaxValue)
                        return kInvalidIndex;
                    return m_ProcessorStartIndex;
                }
                set
                {
                    if (value == kInvalidIndex)
                        m_ProcessorStartIndex = ushort.MaxValue;
                    else
                    {
                        if (value >= ushort.MaxValue)
                            throw new NotSupportedException("Processor count cannot exceed ushort.MaxValue=" + ushort.MaxValue);
                        m_ProcessorStartIndex = (ushort)value;
                    }
                }
            }

            public int processorCount
            {
                get => m_ProcessorCount;
                set
                {
                    if (value >= byte.MaxValue)
                        throw new NotSupportedException("Processor count per binding cannot exceed byte.MaxValue=" + byte.MaxValue);
                    m_ProcessorCount = (byte)value;
                }
            }

            /// <summary>
            /// Index of the action being triggered by the binding (if any).
            /// </summary>
            /// <remarks>
            /// For bindings that don't trigger actions, this is <see cref="kInvalidIndex"/>.
            ///
            /// For bindings that are part of a composite, we force this to be the action set on the composite itself.
            /// </remarks>
            public int actionIndex
            {
                get
                {
                    if (m_ActionIndex == ushort.MaxValue)
                        return kInvalidIndex;
                    return m_ActionIndex;
                }
                set
                {
                    if (value == kInvalidIndex)
                        m_ActionIndex = ushort.MaxValue;
                    else
                    {
                        if (value >= ushort.MaxValue)
                            throw new NotSupportedException("Action count cannot exceed ushort.MaxValue=" + ushort.MaxValue);
                        m_ActionIndex = (ushort)value;
                    }
                }
            }

            public int mapIndex
            {
                get => m_MapIndex;
                set
                {
                    Debug.Assert(value != kInvalidIndex);
                    if (value >= byte.MaxValue)
                        throw new NotSupportedException("Map count cannot exceed byte.MaxValue=" + byte.MaxValue);
                    m_MapIndex = (byte)value;
                }
            }

            /// <summary>
            /// If this is a composite binding, this is the index of the composite in <see cref="composites"/>.
            /// If the binding is part of a composite, this is the index of the binding that is the composite.
            /// If the binding is neither a composite nor part of a composite, this is <see cref="kInvalidIndex"/>.
            /// </summary>
            public int compositeOrCompositeBindingIndex
            {
                get
                {
                    if (m_CompositeOrCompositeBindingIndex == ushort.MaxValue)
                        return kInvalidIndex;
                    return m_CompositeOrCompositeBindingIndex;
                }
                set
                {
                    if (value == kInvalidIndex)
                        m_CompositeOrCompositeBindingIndex = ushort.MaxValue;
                    else
                    {
                        if (value >= ushort.MaxValue)
                            throw new NotSupportedException("Composite count cannot exceed ushort.MaxValue=" + ushort.MaxValue);
                        m_CompositeOrCompositeBindingIndex = (ushort)value;
                    }
                }
            }

            /// <summary>
            /// <see cref="InputEvent.eventId">ID</see> of the event that last triggered the binding.
            /// </summary>
            /// <remarks>
            /// We only store this for composites ATM.
            /// </remarks>
            public int triggerEventIdForComposite
            {
                get => m_TriggerEventIdForComposite;
                set => m_TriggerEventIdForComposite = value;
            }

            public Flags flags
            {
                get => (Flags)m_Flags;
                set => m_Flags = (byte)value;
            }

            public bool chainsWithNext
            {
                get => (flags & Flags.ChainsWithNext) == Flags.ChainsWithNext;
                set
                {
                    if (value)
                        flags |= Flags.ChainsWithNext;
                    else
                        flags &= ~Flags.ChainsWithNext;
                }
            }

            public bool isEndOfChain
            {
                get => (flags & Flags.EndOfChain) == Flags.EndOfChain;
                set
                {
                    if (value)
                        flags |= Flags.EndOfChain;
                    else
                        flags &= ~Flags.EndOfChain;
                }
            }

            public bool isPartOfChain => chainsWithNext || isEndOfChain;

            public bool isComposite
            {
                get => (flags & Flags.Composite) == Flags.Composite;
                set
                {
                    if (value)
                        flags |= Flags.Composite;
                    else
                        flags &= ~Flags.Composite;
                }
            }

            public bool isPartOfComposite
            {
                get => (flags & Flags.PartOfComposite) == Flags.PartOfComposite;
                set
                {
                    if (value)
                        flags |= Flags.PartOfComposite;
                    else
                        flags &= ~Flags.PartOfComposite;
                }
            }

            public bool initialStateCheckPending
            {
                get => (flags & Flags.InitialStateCheckPending) != 0;
                set
                {
                    if (value)
                        flags |= Flags.InitialStateCheckPending;
                    else
                        flags &= ~Flags.InitialStateCheckPending;
                }
            }

            public bool wantsInitialStateCheck
            {
                get => (flags & Flags.WantsInitialStateCheck) != 0;
                set
                {
                    if (value)
                        flags |= Flags.WantsInitialStateCheck;
                    else
                        flags &= ~Flags.WantsInitialStateCheck;
                }
            }

            public int partIndex
            {
                get => m_PartIndex;
                set
                {
                    if (partIndex < 0)
                        throw new ArgumentOutOfRangeException(nameof(value), "Part index must not be negative");
                    if (partIndex > byte.MaxValue)
                        throw new InvalidOperationException("Part count must not exceed byte.MaxValue=" + byte.MaxValue);
                    m_PartIndex = (byte)value;
                }
            }
        }

        /// <summary>
        /// Record of an input control change and its related data.
        /// </summary>
        /// <remarks>
        /// This serves a dual purpose. One is, trigger states represent control actuations while we process them. The
        /// other is to represent the current actuation state of an action as a whole. The latter is stored in <see cref="actionStates"/>
        /// while the former is passed around as temporary instances on the stack.
        /// </remarks>
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        public struct TriggerState
        {
            [FieldOffset(0)] private byte m_Phase;
            [FieldOffset(1)] private byte m_Flags;
            [FieldOffset(2)] private byte m_MapIndex;
            // One byte available here.
            ////REVIEW: can we condense these to floats? would save us a whopping 8 bytes
            [FieldOffset(4)] private double m_Time;
            [FieldOffset(12)] private double m_StartTime;
            [FieldOffset(20)] private ushort m_ControlIndex;
            // Two bytes available here.
            [FieldOffset(24)] private ushort m_BindingIndex;
            [FieldOffset(26)] private ushort m_InteractionIndex;
            [FieldOffset(28)] private float m_Magnitude;

            /// <summary>
            /// Phase being triggered by the control value change.
            /// </summary>
            public InputActionPhase phase
            {
                get => (InputActionPhase)m_Phase;
                set => m_Phase = (byte)value;
            }

            /// <summary>
            /// The time the binding got triggered.
            /// </summary>
            public double time
            {
                get => m_Time;
                set => m_Time = value;
            }

            /// <summary>
            /// The time when the binding moved into <see cref="InputActionPhase.Started"/>.
            /// </summary>
            public double startTime
            {
                get => m_StartTime;
                set => m_StartTime = value;
            }

            /// <summary>
            /// Amount of actuation on the control.
            /// </summary>
            /// <remarks>
            /// This is only valid if <see cref="haveMagnitude"/> is true.
            ///
            /// Note that this may differ from the actuation stored for <see cref="controlIndex"/> in <see
            /// cref="UnmanagedMemory.controlMagnitudes"/> if the binding is a composite.
            /// </remarks>
            public float magnitude
            {
                get => m_Magnitude;
                set
                {
                    flags |= Flags.HaveMagnitude;
                    m_Magnitude = value;
                }
            }

            /// <summary>
            /// Whether <see cref="magnitude"/> has been set.
            /// </summary>
            /// <remarks>
            /// Magnitude computation is expensive so we only want to do it once. Also, we sometimes need to compare
            /// a current magnitude to a magnitude value from a previous frame and the magnitude of the control
            /// may have already changed.
            /// </remarks>
            public bool haveMagnitude => (flags & Flags.HaveMagnitude) != 0;

            /// <summary>
            /// Index of the action map in <see cref="maps"/> that contains the binding that triggered.
            /// </summary>
            public int mapIndex
            {
                get => m_MapIndex;
                set
                {
                    if (value < 0 || value > byte.MaxValue)
                        throw new NotSupportedException("More than byte.MaxValue InputActionMaps in a single InputActionState");
                    m_MapIndex = (byte)value;
                }
            }

            /// <summary>
            /// Index of the control currently driving the action or <see cref="kInvalidIndex"/> if none.
            /// </summary>
            public int controlIndex
            {
                get
                {
                    if (m_ControlIndex == ushort.MaxValue)
                        return kInvalidIndex;
                    return m_ControlIndex;
                }
                set
                {
                    if (value == kInvalidIndex)
                        m_ControlIndex = ushort.MaxValue;
                    else
                    {
                        if (value < 0 || value >= ushort.MaxValue)
                            throw new NotSupportedException("More than ushort.MaxValue-1 controls in a single InputActionState");
                        m_ControlIndex = (ushort)value;
                    }
                }
            }

            /// <summary>
            /// Index into <see cref="bindingStates"/> for the binding that triggered.
            /// </summary>
            /// <remarks>
            /// This corresponds 1:1 to an <see cref="InputBinding"/>.
            /// </remarks>
            public int bindingIndex
            {
                get => m_BindingIndex;
                set
                {
                    if (value < 0 || value > ushort.MaxValue)
                        throw new NotSupportedException("More than ushort.MaxValue bindings in a single InputActionState");
                    m_BindingIndex = (ushort)value;
                }
            }

            /// <summary>
            /// Index into <see cref="InputActionState.interactionStates"/> for the interaction that triggered.
            /// </summary>
            /// <remarks>
            /// Is <see cref="InputActionState.kInvalidIndex"/> if there is no interaction present on the binding.
            /// </remarks>
            public int interactionIndex
            {
                get
                {
                    if (m_InteractionIndex == ushort.MaxValue)
                        return kInvalidIndex;
                    return m_InteractionIndex;
                }
                set
                {
                    if (value == kInvalidIndex)
                        m_InteractionIndex = ushort.MaxValue;
                    else
                    {
                        if (value < 0 || value >= ushort.MaxValue)
                            throw new NotSupportedException("More than ushort.MaxValue-1 interactions in a single InputActionState");
                        m_InteractionIndex = (ushort)value;
                    }
                }
            }

            /// <summary>
            /// Whether the action associated with the trigger state is marked as continuous.
            /// </summary>
            /// <seealso cref="InputAction.continuous"/>
            public bool continuous
            {
                get => (flags & Flags.Continuous) != 0;
                set
                {
                    if (value)
                        flags |= Flags.Continuous;
                    else
                        flags &= ~Flags.Continuous;
                }
            }

            /// <summary>
            /// Whether the action is currently on the list of continuous actions.
            /// </summary>
            public bool onContinuousList
            {
                get => (flags & Flags.OnContinuousList) != 0;
                set
                {
                    if (value)
                        flags |= Flags.OnContinuousList;
                    else
                        flags &= ~Flags.OnContinuousList;
                }
            }

            /// <summary>
            /// Whether the action associated with the trigger state is marked as pass-through.
            /// </summary>
            /// <seealso cref="InputAction.passThrough"/>
            public bool passThrough
            {
                get => (flags & Flags.PassThrough) != 0;
                set
                {
                    if (value)
                        flags |= Flags.PassThrough;
                    else
                        flags &= ~Flags.PassThrough;
                }
            }

            /// <summary>
            /// Whether the action may potentially see multiple concurrent actuations from its bindings
            /// and wants them resolved automatically.
            /// </summary>
            /// <remarks>
            /// We use this to gate some of the more expensive checks that are pointless to
            /// perform if we don't have to disambiguate input from concurrent sources.
            ///
            /// Always disabled if <see cref="passThrough"/> is true.
            /// </remarks>
            public bool mayNeedConflictResolution
            {
                get => (flags & Flags.MayNeedConflictResolution) != 0;
                set
                {
                    if (value)
                        flags |= Flags.MayNeedConflictResolution;
                    else
                        flags &= ~Flags.MayNeedConflictResolution;
                }
            }

            /// <summary>
            /// Whether the action currently has several concurrent actuations from its bindings.
            /// </summary>
            /// <remarks>
            /// This is only used when automatic conflict resolution is enabled (<see cref="mayNeedConflictResolution"/>).
            /// </remarks>
            public bool hasMultipleConcurrentActuations
            {
                get => (flags & Flags.HasMultipleConcurrentActuations) != 0;
                set
                {
                    if (value)
                        flags |= Flags.HasMultipleConcurrentActuations;
                    else
                        flags &= ~Flags.HasMultipleConcurrentActuations;
                }
            }

            public Flags flags
            {
                get => (Flags)m_Flags;
                set => m_Flags = (byte)value;
            }

            [Flags]
            public enum Flags
            {
                /// <summary>
                /// Whether the action associated with the trigger state is continuous.
                /// </summary>
                /// <seealso cref="InputAction.continuous"/>
                Continuous = 1 << 0,

                /// <summary>
                /// Whether the action is currently on the list of actions to check continuously.
                /// </summary>
                /// <seealso cref="InputActionState.m_ContinuousActions"/>
                OnContinuousList = 1 << 1,

                /// <summary>
                /// Whether <see cref="magnitude"/> has been set.
                /// </summary>
                HaveMagnitude = 1 << 2,

                /// <summary>
                /// Whether the action associated with the trigger state is marked as pass-through.
                /// </summary>
                /// <seealso cref="InputAction.passThrough"/>
                PassThrough = 1 << 3,

                /// <summary>
                /// Whether the action has more than one control bound to it.
                /// </summary>
                /// <remarks>
                /// An action may have arbitrary many bindings yet may still resolve only to a single control
                /// at runtime. In that case, this flag is NOT set. We only set it if binding resolution for
                /// an action indeed ended up with multiple controls able to trigger the same action.
                /// </remarks>
                MayNeedConflictResolution = 1 << 4,

                /// <summary>
                /// Whether there are currently multiple bound controls that are actuated.
                /// </summary>
                /// <remarks>
                /// This is only used if <see cref="TriggerState.mayNeedConflictResolution"/> is true.
                /// </remarks>
                HasMultipleConcurrentActuations = 1 << 5,
            }
        }

        /// <summary>
        /// Tells us where the data for a single action map is found in the
        /// various arrays.
        /// </summary>
        public struct ActionMapIndices
        {
            public int actionStartIndex;
            public int actionCount;
            public int controlStartIndex;
            public int controlCount;
            public int bindingStartIndex;
            public int bindingCount;
            public int interactionStartIndex;
            public int interactionCount;
            public int processorStartIndex;
            public int processorCount;
            public int compositeStartIndex;
            public int compositeCount;
        }

        /// <summary>
        /// Unmanaged memory kept for action maps.
        /// </summary>
        /// <remarks>
        /// Most of the dynamic execution state for actions we keep in a single block of unmanaged memory.
        /// Essentially, only the C# heap objects (like IInputInteraction and such) we keep in managed arrays.
        /// Aside from being able to condense the data into a single block of memory and not having to have
        /// it spread out on the GC heap, we gain the advantage of being able to freely allocate and re-allocate
        /// these blocks without creating garbage on the GC heap.
        ///
        /// The data here is set up by <see cref="InputBindingResolver"/>.
        /// </remarks>
        public struct UnmanagedMemory : IDisposable
        {
            public bool isAllocated => basePtr != null;

            public void* basePtr;

            /// <summary>
            /// Number of action maps and entries in <see cref="mapIndices"/> and <see cref="maps"/>.
            /// </summary>
            public int mapCount;

            /// <summary>
            /// Total number of actions (i.e. from all maps combined) and entries in <see cref="actionStates"/>.
            /// </summary>
            public int actionCount;

            /// <summary>
            /// Total number of interactions and entries in <see cref="interactionStates"/> and <see cref="interactions"/>.
            /// </summary>
            public int interactionCount;

            /// <summary>
            /// Total number of bindings and entries in <see cref="bindingStates"/>.
            /// </summary>
            public int bindingCount;

            /// <summary>
            /// Total number of bound controls and entries in <see cref="controls"/>.
            /// </summary>
            public int controlCount;

            /// <summary>
            /// Total number of composite bindings and entries in <see cref="composites"/>.
            /// </summary>
            public int compositeCount;

            /// <summary>
            /// Total size of allocated unmanaged memory.
            /// </summary>
            public int sizeInBytes =>
                mapCount * sizeof(ActionMapIndices) + // mapIndices
                actionCount * sizeof(TriggerState) + // actionStates
                bindingCount * sizeof(BindingState) + // bindingStates
                interactionCount * sizeof(InteractionState) + // interactionStates
                controlCount * sizeof(float) + // controlMagnitudes
                compositeCount * sizeof(float) + // compositeMagnitudes
                controlCount * sizeof(int) + // controlIndexToBindingIndex
                actionCount * sizeof(ushort) * 2 + // actionBindingIndicesAndCounts
                bindingCount * sizeof(ushort); // actionBindingIndices

            /// <summary>
            /// Trigger state of all actions added to the state.
            /// </summary>
            /// <remarks>
            /// This array also tells which actions are enabled or disabled. Any action with phase
            /// <see cref="InputActionPhase.Disabled"/> is disabled.
            /// </remarks>
            public TriggerState* actionStates;

            /// <summary>
            /// State of all bindings added to the state.
            /// </summary>
            /// <remarks>
            /// For the most part, this is read-only information set up during resolution.
            /// </remarks>
            public BindingState* bindingStates;

            /// <summary>
            /// State of all interactions on bindings in the action map.
            /// </summary>
            /// <remarks>
            /// Any interaction mentioned on any of the bindings gets its own execution state record
            /// in here. The interactions for any one binding are grouped together.
            /// </remarks>
            public InteractionState* interactionStates;

            /// <summary>
            ///
            /// </summary>
            /// <remarks>
            /// This array is NOT kept strictly up to date. In fact, we only use it for conflict resolution
            /// between multiple bound controls at the moment. Meaning that in the majority of cases, the magnitude
            /// stored for a control here will NOT be up to date.
            ///
            /// Also note that for controls that are part of composites, this will NOT be the magnitude of the
            /// control but rather be the magnitude of the entire compound.
            /// </remarks>
            public float* controlMagnitudes;

            public float* compositeMagnitudes;

            /// <summary>
            /// Array of pair of ints, one pair for each action (same index as <see cref="actionStates"/>). First int
            /// is count of bindings on action, second int is index into <see cref="actionBindingIndices"/> where
            /// bindings of action are found.
            /// </summary>
            public ushort* actionBindingIndicesAndCounts;

            /// <summary>
            /// Array of indices into <see cref="bindingStates"/>. The indices for every action are laid out sequentially.
            /// The array slice corresponding to each action can be determined by looking it up in <see cref="actionBindingIndicesAndCounts"/>.
            /// </summary>
            public ushort* actionBindingIndices;

            ////REVIEW: make this an array of shorts rather than ints?
            public int* controlIndexToBindingIndex;

            public ActionMapIndices* mapIndices;

            public void Allocate(int mapCount, int actionCount, int bindingCount, int controlCount, int interactionCount, int compositeCount)
            {
                Debug.Assert(basePtr == null, "Memory already allocated! Free first!");
                Debug.Assert(mapCount >= 1, "Map count out of range");
                Debug.Assert(actionCount >= 0, "Action count out of range");
                Debug.Assert(bindingCount >= 0, "Binding count out of range");
                Debug.Assert(interactionCount >= 0, "Interaction count out of range");
                Debug.Assert(compositeCount >= 0, "Composite count out of range");

                this.mapCount = mapCount;
                this.actionCount = actionCount;
                this.interactionCount = interactionCount;
                this.bindingCount = bindingCount;
                this.controlCount = controlCount;
                this.compositeCount = compositeCount;

                var ptr = (byte*)UnsafeUtility.Malloc(sizeInBytes, 4, Allocator.Persistent);
                UnsafeUtility.MemClear(ptr, sizeInBytes);

                basePtr = ptr;

                // NOTE: This depends on the individual structs being sufficiently aligned in order to not
                //       cause any misalignment here.
                mapIndices = (ActionMapIndices*)ptr; ptr += mapCount * sizeof(ActionMapIndices);
                actionStates = (TriggerState*)ptr; ptr += actionCount * sizeof(TriggerState);
                interactionStates = (InteractionState*)ptr; ptr += interactionCount * sizeof(InteractionState);
                bindingStates = (BindingState*)ptr; ptr += bindingCount * sizeof(BindingState);
                controlMagnitudes = (float*)ptr; ptr += controlCount * sizeof(float);
                compositeMagnitudes = (float*)ptr; ptr += compositeCount * sizeof(float);
                controlIndexToBindingIndex = (int*)ptr; ptr += controlCount * sizeof(int);
                actionBindingIndicesAndCounts = (ushort*)ptr; ptr += actionCount * sizeof(ushort) * 2;
                actionBindingIndices = (ushort*)ptr; ptr += bindingCount * sizeof(ushort);
            }

            public void Dispose()
            {
                if (basePtr == null)
                    return;

                UnsafeUtility.Free(basePtr, Allocator.Persistent);

                basePtr = null;
                actionStates = null;
                interactionStates = null;
                bindingStates = null;
                mapIndices = null;
                controlMagnitudes = null;
                compositeMagnitudes = null;
                controlIndexToBindingIndex = null;
                actionBindingIndices = null;
                actionBindingIndicesAndCounts = null;

                mapCount = 0;
                actionCount = 0;
                bindingCount = 0;
                controlCount = 0;
                interactionCount = 0;
                compositeCount = 0;
            }

            public void CopyDataFrom(UnmanagedMemory memory)
            {
                Debug.Assert(memory.basePtr != null, "Given struct has no allocated data");

                // Even if a certain array is empty (e.g. we have no controls), we set the pointer
                // in Allocate() to something other than null.

                UnsafeUtility.MemCpy(mapIndices, memory.mapIndices, memory.mapCount * sizeof(ActionMapIndices));
                UnsafeUtility.MemCpy(actionStates, memory.actionStates, memory.actionCount * sizeof(TriggerState));
                UnsafeUtility.MemCpy(bindingStates, memory.bindingStates, memory.bindingCount * sizeof(BindingState));
                UnsafeUtility.MemCpy(interactionStates, memory.interactionStates, memory.interactionCount * sizeof(InteractionState));
                UnsafeUtility.MemCpy(controlMagnitudes, memory.controlMagnitudes, memory.controlCount * sizeof(float));
                UnsafeUtility.MemCpy(compositeMagnitudes, memory.compositeMagnitudes, memory.compositeCount * sizeof(float));
                UnsafeUtility.MemCpy(controlIndexToBindingIndex, memory.controlIndexToBindingIndex, memory.controlCount * sizeof(int));
                UnsafeUtility.MemCpy(actionBindingIndicesAndCounts, memory.actionBindingIndicesAndCounts, memory.actionCount * sizeof(ushort) * 2);
                UnsafeUtility.MemCpy(actionBindingIndices, memory.actionBindingIndices, memory.bindingCount * sizeof(ushort));
            }

            public UnmanagedMemory Clone()
            {
                if (!isAllocated)
                    return new UnmanagedMemory();

                var clone = new UnmanagedMemory();
                clone.Allocate(
                    mapCount: mapCount,
                    actionCount: actionCount,
                    controlCount: controlCount,
                    bindingCount: bindingCount,
                    interactionCount: interactionCount,
                    compositeCount: compositeCount);
                clone.CopyDataFrom(this);

                return clone;
            }
        }

        #region Global State

        /// <summary>
        /// List of weak references to all action map states currently in the system.
        /// </summary>
        /// <remarks>
        /// When the control setup in the system changes, we need a way for control resolution that
        /// has already been done to be invalidated and redone. We also want a way to find all
        /// currently enabled actions in the system.
        ///
        /// Both of these needs are served by this global list.
        /// </remarks>
        private static InlinedArray<GCHandle> s_GlobalList;
        internal static InlinedArray<Action<object, InputActionChange>> s_OnActionChange;

        private void AddToGlobaList()
        {
            CompactGlobalList();
            var handle = GCHandle.Alloc(this, GCHandleType.Weak);
            s_GlobalList.AppendWithCapacity(handle);
        }

        private void RemoveMapFromGlobalList()
        {
            var count = s_GlobalList.length;
            for (var i = 0; i < count; ++i)
                if (s_GlobalList[i].Target == this)
                {
                    s_GlobalList[i].Free();
                    s_GlobalList.RemoveAtByMovingTailWithCapacity(i);
                    break;
                }
        }

        /// <summary>
        /// Remove any entries for states that have been reclaimed by GC.
        /// </summary>
        private static void CompactGlobalList()
        {
            var length = s_GlobalList.length;
            var head = 0;
            for (var i = 0; i < length; ++i)
            {
                if (s_GlobalList[i].Target != null)
                {
                    if (head != i)
                        s_GlobalList[head] = s_GlobalList[i];
                    ++head;
                }
                else
                {
                    s_GlobalList[i].Free();
                }
            }
            s_GlobalList.length = head;
        }

        internal static void NotifyListenersOfActionChange(InputActionChange change, object actionOrMap)
        {
            Debug.Assert(actionOrMap != null, "Should have action or action map object to notify about");
            Debug.Assert(actionOrMap is InputAction || ((InputActionMap)actionOrMap).m_SingletonAction == null,
                "Must not send notifications for changes made to hidden action maps of singleton actions");

            for (var i = 0; i < s_OnActionChange.length; ++i)
                DelegateHelpers.InvokeCallbacksSafe(ref s_OnActionChange, actionOrMap, change, "onActionChange");
        }

        /// <summary>
        /// Nuke global state we have to keep track of action map states.
        /// </summary>
        internal static void ResetGlobals()
        {
            DestroyAllActionMapStates();
            for (var i = 0; i < s_GlobalList.length; ++i)
                s_GlobalList[i].Free();
            s_GlobalList.length = 0;
            s_OnActionChange.Clear();
        }

        // Walk all maps with enabled actions and add all enabled actions to the given list.
        internal static int FindAllEnabledActions(List<InputAction> result)
        {
            var numFound = 0;
            var stateCount = s_GlobalList.length;
            for (var i = 0; i < stateCount; ++i)
            {
                var state = (InputActionState)s_GlobalList[i].Target;
                if (state == null)
                    continue;

                var mapCount = state.totalMapCount;
                var maps = state.maps;
                for (var n = 0; n < mapCount; ++n)
                {
                    var map = maps[n];
                    if (!map.enabled)
                        continue;

                    var actions = map.m_Actions;
                    var actionCount = actions.Length;
                    if (map.m_EnabledActionsCount == actionCount)
                    {
                        result.AddRange(actions);
                        numFound += actionCount;
                    }
                    else
                    {
                        var actionStartIndex = state.mapIndices[map.m_MapIndexInState].actionStartIndex;
                        for (var k = 0; k < actionCount; ++k)
                        {
                            if (state.actionStates[actionStartIndex + k].phase != InputActionPhase.Disabled)
                            {
                                result.Add(actions[k]);
                                ++numFound;
                            }
                        }
                    }
                }
            }

            return numFound;
        }

        ////TODO: when re-resolving, we need to preserve InteractionStates and not just reset them

        /// <summary>
        /// Deal with the fact that the control setup in the system may change at any time and can affect
        /// actions that had their controls already resolved.
        /// </summary>
        /// <remarks>
        /// Note that this method can NOT deal with changes other than the control setup in the system
        /// changing. Specifically, it will NOT handle configuration changes in action maps (e.g. bindings
        /// being altered) correctly.
        ///
        /// We get called from <see cref="InputManager"/> directly rather than hooking into <see cref="InputSystem.onDeviceChange"/>
        /// so that we're not adding needless calls for device changes that are not of interest to us.
        /// </remarks>
        internal static void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            Debug.Assert(device != null);

            // We only care about new devices appearing or existing devices disappearing.
            if (change != InputDeviceChange.Added && change != InputDeviceChange.Removed)
                return;

            for (var i = 0; i < s_GlobalList.length; ++i)
            {
                var state = (InputActionState)s_GlobalList[i].Target;
                if (state == null)
                {
                    // Stale entry in the list. State has already been reclaimed by GC. Remove it.
                    s_GlobalList[i].Free();
                    s_GlobalList.RemoveAtWithCapacity(i);
                    continue;
                }

                // If this state is not affected by the addition or removal of the given
                // device, skip it.
                if (change == InputDeviceChange.Added && !state.CanUseDevice(device))
                    continue;
                if (change == InputDeviceChange.Removed && !state.IsUsingDevice(device))
                    continue;

                // Trigger a lazy-resolve on all action maps in the state.
                for (var n = 0; n < state.totalMapCount; ++n)
                    if (state.maps[n].LazyResolveBindings())
                    {
                        // Map has chosen to resolve right away (i.e it has enabled actions).
                        // This will resolve bindings for *all* maps in the state, so we're done here.
                        break;
                    }
            }
        }

        internal static void DisableAllActions()
        {
            for (var i = 0; i < s_GlobalList.length; ++i)
            {
                var state = (InputActionState)s_GlobalList[i].Target;
                if (state == null)
                    continue;

                var mapCount = state.totalMapCount;
                var maps = state.maps;
                for (var n = 0; n < mapCount; ++n)
                {
                    maps[n].Disable();
                    Debug.Assert(!maps[n].enabled);
                }
            }
        }

        /// <summary>
        /// Forcibly destroy all states currently on the global list.
        /// </summary>
        /// <remarks>
        /// We do this when exiting play mode in the editor to make sure we are cleaning up our
        /// unmanaged memory allocations.
        /// </remarks>
        internal static void DestroyAllActionMapStates()
        {
            while (s_GlobalList.length > 0)
            {
                var index = s_GlobalList.length - 1;
                var state = (InputActionState)s_GlobalList[index].Target;
                if (state == null)
                {
                    // Already destroyed.
                    s_GlobalList[index].Free();
                    s_GlobalList.RemoveAtWithCapacity(index);
                    continue;
                }

                state.Destroy();
            }
        }

        #endregion
    }
}
