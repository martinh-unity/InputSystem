using System;
using System.Runtime.InteropServices;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.Profiling;

////TODO: property that tells whether a Touchscreen is multi-touch capable

////TODO: property that tells whether a Touchscreen supports pressure

////TODO: add support for screen orientation

////TODO: touch is hardwired to certain memory layouts ATM; either allow flexibility or make sure the layouts cannot be changed

////TODO: startTimes are baked *external* times; reset touch when coming out of play mode

////REVIEW: where should we put handset vibration support? should that sit on the touchscreen class? be its own separate device?

// What I want:
// X There should always be a reliable "primary" touch
// X Touch #0..#n should always correspond to first..last touch
// - Reliable binding from actions
// X TouchManager should make tracking touches super easy
// - Lots of tests to cover all this
// - Ideally, make touch not depend on hardcoded state format

namespace UnityEngine.InputSystem.LowLevel
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1028:EnumStorageShouldBeInt32", Justification = "byte to correspond to TouchState layout.")]
    [Flags]
    internal enum TouchFlags : byte
    {
        // NOTE: Leaving the first 4 bits for native.

        IndirectTouch = 1 << 0,
        PrimaryTouch = 1 << 4,
        Tap = 1 << 5,

        // We use this flag internally to detect the case where the primary touch switches from
        // one finger to another. In this case, we don't want to trigger a release to trigger
        // a tap.
        InheritedPrimaryTouch = 1 << 6,
    }

    ////REVIEW: add timestamp directly to touch?
    /// <summary>
    /// State layout for a single touch.
    /// </summary>
    // IMPORTANT: Must match TouchInputState in native code.
    [StructLayout(LayoutKind.Explicit, Size = kSizeInBytes)]
    public struct TouchState : IInputStateTypeInfo
    {
        internal const int kSizeInBytes = 56;

        public static FourCC kFormat => new FourCC('T', 'O', 'U', 'C');

        [InputControl(layout = "Integer")]
        [FieldOffset(0)]
        public int touchId;

        [InputControl]
        [FieldOffset(4)]
        public Vector2 position;

        [InputControl]
        [FieldOffset(12)]
        public Vector2 delta;

        [InputControl(layout = "Axis")]
        [FieldOffset(20)]
        public float pressure;

        [InputControl]
        [FieldOffset(24)]
        public Vector2 radius;

        [InputControl(name = "phase", layout = "TouchPhase")]
        [InputControl(name = "press", layout = "TouchPress", useStateFrom = "phase")]
        [FieldOffset(32)]
        public byte phaseId;

        [InputControl(name = "tapCount", layout = "Integer")]
        [FieldOffset(33)]
        public byte tapCount;

        [InputControl(layout = "Digital")]
        [FieldOffset(34)]
        public byte displayIndex;

        [InputControl(name = "indirectTouch", layout = "Button", bit = 0)]
        [InputControl(name = "tap", layout = "Button", bit = 5)]
        [FieldOffset(35)]
        public byte flags;

        // Wasting four bytes in the name of alignment here.

        // NOTE: The following data is NOT sent by native but rather data we add on the managed side to each touch.
        [InputControl(name = "startTime", layout  = "Double")]
        [FieldOffset(40)]
        public double startTime; // In *external* time, i.e. currentTimeOffsetToRealtimeSinceStartup baked in.
        [InputControl]
        [FieldOffset(48)]
        public Vector2 startPosition;

        public TouchPhase phase
        {
            get => (TouchPhase)phaseId;
            set => phaseId = (byte)value;
        }

        public bool isNoneEndedOrCanceled => phase == TouchPhase.None || phase == TouchPhase.Ended ||
        phase == TouchPhase.Canceled;
        public bool isInProgress => phase == TouchPhase.Began || phase == TouchPhase.Moved ||
        phase == TouchPhase.Stationary;

        public bool isPrimaryTouch
        {
            get => (flags & (byte)TouchFlags.PrimaryTouch) != 0;
            set
            {
                if (value)
                    flags |= (byte)TouchFlags.PrimaryTouch;
                else
                    flags &= (byte)~TouchFlags.PrimaryTouch;
            }
        }

        /// <summary>
        /// Whether this touch has taken over as primary from another touch.
        /// </summary>
        public bool isInheritedPrimaryTouch
        {
            get => (flags & (byte)TouchFlags.InheritedPrimaryTouch) != 0;
            set
            {
                if (value)
                    flags |= (byte)TouchFlags.InheritedPrimaryTouch;
                else
                    flags &= (byte)~TouchFlags.InheritedPrimaryTouch;
            }
        }

        public bool isIndirectTouch
        {
            get => (flags & (byte)TouchFlags.IndirectTouch) != 0;
            set
            {
                if (value)
                    flags |= (byte)TouchFlags.IndirectTouch;
                else
                    flags &= (byte)~TouchFlags.IndirectTouch;
            }
        }

        public bool isTap
        {
            get => (flags & (byte)TouchFlags.Tap) != 0;
            set
            {
                if (value)
                    flags |= (byte)TouchFlags.Tap;
                else
                    flags &= (byte)~TouchFlags.Tap;
            }
        }

        public FourCC format
        {
            get { return kFormat; }
        }

        public override string ToString()
        {
            return $"{{ id={touchId} phase={phase} pos={position} delta={delta} pressure={pressure} radius={radius} primary={isPrimaryTouch} }}";
        }
    }

    /// <summary>
    /// Default state layout for touch devices.
    /// </summary>
    /// <remarks>
    /// Combines multiple pointers each corresponding to a single contact.
    ///
    /// All touches combine to quite a bit of state; ideally send delta events that update
    /// only specific fingers.
    ///
    /// This is NOT used by native. Instead, the native runtime always sends individual touches (<see cref="TouchState"/>)
    /// and leaves state management for a touchscreen as a whole to the managed part of the system.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = MaxTouches * TouchState.kSizeInBytes)]
    public unsafe struct TouchscreenState : IInputStateTypeInfo
    {
        public static FourCC kFormat => new FourCC('T', 'S', 'C', 'R');

        /// <summary>
        /// Maximum number of touches that can be tracked at the same time.
        /// </summary>
        /// <remarks>
        /// While most touchscreens only support a number of concurrent touches that is significantly lower
        /// than this number, having a larger pool of touch states to work with makes it possible to
        /// track short-lived touches better.
        /// </remarks>
        public const int MaxTouches = 10;

        /// <summary>
        /// Data for the touch that is deemed the "primary" touch at the moment.
        /// </summary>
        /// <remarks>
        /// This touch duplicates touch data from whichever touch is deemed the primary touch at the moment.
        /// When going from no fingers down to any finger down, the first finger to touch the screen is
        /// deemed the "primary touch". It stays the primary touch until released. At that point, if any other
        /// finger is still down, the next finger in <see cref="touchData"/> is
        ///
        /// Having this touch be its own separate state and own separate control allows actions to track the
        /// state of the primary touch even if the touch moves from one finger to another in <see cref="touchData"/>.
        /// </remarks>
        [InputControl(name = "primaryTouch", layout = "Touch", synthetic = true)]
        // Add controls compatible with what Pointer expects and redirect their
        // state to the state of touch0 so that this essentially becomes our
        // pointer control.
        // NOTE: Some controls from Pointer don't make sense for touch and we "park"
        //       them by assigning them invalid offsets (thus having automatic state
        //       layout put them at the end of our fixed state).
        [InputControl(name = "pointerId", useStateFrom = "primaryTouch/touchId")]
        [InputControl(name = "position", useStateFrom = "primaryTouch/position")]
        [InputControl(name = "delta", useStateFrom = "primaryTouch/delta")]
        [InputControl(name = "pressure", useStateFrom = "primaryTouch/pressure")]
        [InputControl(name = "radius", useStateFrom = "primaryTouch/radius")]
        [InputControl(name = "displayIndex", useStateFrom = "primaryTouch/displayIndex")]
        [InputControl(name = "tap", useStateFrom = "primaryTouch/tap", layout = "Button", synthetic = true, usage = "PrimaryAction")]
        [InputControl(name = "tapCount", useStateFrom = "primaryTouch/tapCount", layout = "Integer", synthetic = true)]
        [InputControl(name = "press", useStateFrom = "primaryTouch/phase", layout = "TouchPress", synthetic = true, usages = new string[0])]
        // Touch does not support twist and tilt. These will always be at default value.
        [InputControl(name = "twist", offset = InputStateBlock.AutomaticOffset)]
        [InputControl(name = "tilt", offset = InputStateBlock.AutomaticOffset)]
        [FieldOffset(0)]
        public fixed byte primaryTouchData[TouchState.kSizeInBytes];

        internal const int kTouchDataOffset = TouchState.kSizeInBytes;

        [InputControl(layout = "Touch", name = "touch", arraySize = MaxTouches)]
        [FieldOffset(kTouchDataOffset)]
        public fixed byte touchData[MaxTouches * TouchState.kSizeInBytes];

        public TouchState* primaryTouch
        {
            get
            {
                fixed(byte* ptr = primaryTouchData)
                return (TouchState*)ptr;
            }
        }

        public TouchState* touches
        {
            get
            {
                fixed(byte* ptr = touchData)
                return (TouchState*)ptr;
            }
        }

        public FourCC format
        {
            get { return kFormat; }
        }
    }
}

namespace UnityEngine.InputSystem
{
    public enum TouchPhase
    {
        /// <summary>
        /// No activity has been registered on the touch yet.
        /// </summary>
        None,

        Began,
        Moved,
        Ended,
        Canceled,
        Stationary,
    }

    /// <summary>
    /// A multi-touch surface.
    /// </summary>
    /// <remarks>
    /// Note that this class presents a fairly low-level touch API. When working with touch from script code,
    /// it is recommended to use the higher-level <see cref="Plugins.EnhancedTouch.Touch"/> API instead.
    /// </remarks>
    [InputControlLayout(stateType = typeof(TouchscreenState), isGenericTypeOfDevice = true)]
    public class Touchscreen : Pointer, IInputStateCallbackReceiver
    {
        /// <summary>
        /// Button that triggers when the screen is tapped.
        /// </summary>
        /// <remarks>
        /// </remarks>
        public ButtonControl tap { get; private set; }

        public IntegerControl tapCount { get; private set; }

        /// <summary>
        /// Synthetic control that has the data for the touch that is deemed the "primary" touch at the moment.
        /// </summary>
        /// <remarks>
        /// This touch duplicates touch data from whichever touch is deemed the primary touch at the moment.
        /// When going from no fingers down to any finger down, the first finger to touch the screen is
        /// deemed the "primary touch". It stays the primary touch until released. At that point, if any other
        /// finger is still down, the next finger in <see cref="touchData"/> is
        ///
        /// Having this touch be its own separate state and own separate control allows actions to track the
        /// state of the primary touch even if the touch moves from one finger to another in <see cref="touchData"/>.
        /// </remarks>
        public TouchControl primaryTouch { get; private set; }

        /// <summary>
        /// Array of all <see cref="TouchControl">TouchControls</see> on the device.
        /// </summary>
        /// <remarks>
        /// Will always contain <see cref="TouchscreenState.MaxTouches"/> entries regardless of
        /// which touches (if any) are currently in progress.
        /// </remarks>
        public ReadOnlyArray<TouchControl> touches { get; private set; }

        /// <summary>
        /// The touchscreen that was added or updated last or null if there is no
        /// touchscreen connected to the system.
        /// </summary>
        public new static Touchscreen current { get; internal set; }

        public override void MakeCurrent()
        {
            base.MakeCurrent();
            current = this;
        }

        protected override void OnRemoved()
        {
            base.OnRemoved();
            if (current == this)
                current = null;
        }

        protected override void FinishSetup(InputDeviceBuilder builder)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            base.FinishSetup(builder);

            tap = builder.GetControl<ButtonControl>(this, "tap");
            tapCount = builder.GetControl<IntegerControl>(this, "tapCount");
            primaryTouch = builder.GetControl<TouchControl>(this, "primaryTouch");

            // Find out how many touch controls we have.
            var touchControlCount = 0;
            foreach (var child in children)
                if (child is TouchControl)
                    ++touchControlCount;

            // Keep primaryTouch out of array.
            Debug.Assert(touchControlCount >= 1, "Should have found at least primaryTouch control");
            if (touchControlCount >= 1)
                --touchControlCount;

            // Gather touch controls into array.
            var touchArray = new TouchControl[touchControlCount];
            var touchIndex = 0;
            foreach (var child in children)
            {
                if (child == primaryTouch)
                    continue;

                if (child is TouchControl control)
                    touchArray[touchIndex++] = control;
            }

            touches = new ReadOnlyArray<TouchControl>(touchArray);
        }

        // Touch has more involved state handling than most other devices. To not put touch allocation logic
        // in all the various platform backends (i.e. see a touch with a certain ID coming in from the system
        // and then having to decide *where* to store that inside of Touchscreen's state), we have backends
        // send us individual touches ('TOUC') instead of whole Touchscreen snapshots ('TSRC'). Using
        // IInputStateCallbackReceiver, Touchscreen then dynamically decides where to store the touch.
        //
        // Also, Touchscreen has bits of logic to automatically synthesize the state of controls it inherits
        // from Pointer (such as "<Pointer>/press").
        //
        // NOTE: We do *NOT* make a effort here to prevent us from losing short-lived touches. This is different
        //       from the old input system where individual touches were not reused until the next frame. This meant
        //       that additional touches potentially had to be allocated in order to accomodate new touches coming
        //       in from the system.
        //
        //       The rationale for *NOT* doing this is that:
        //
        //       a) Actions don't need it. They observe every single state change and thus will not lose data
        //          even if it is short-lived (i.e. changes more than once in the same update).
        //       b) The higher-level Touch (EnhancedTouchSupport) API is provided to
        //          not only handle this scenario but also give a generally more flexible and useful touch API
        //          than writing code directly against Touchscreen.

        protected new unsafe bool OnCarryStateForward(void* statePtr)
        {
            Profiler.BeginSample("TouchCarryStateForward");

            var haveChangedState = false;

            ////TODO: early out and skip crawling through touches if we didn't change state in the last update
            ////      (also obsoletes the need for the if() check below)
            var touchStatePtr = (TouchState*)((byte*)statePtr + stateBlock.byteOffset + TouchscreenState.kTouchDataOffset);
            for (var i = 0; i < touches.Count; ++i, ++touchStatePtr)
            {
                // Reset delta.
                if (touchStatePtr->delta != default)
                {
                    haveChangedState = true;
                    touchStatePtr->delta = Vector2.zero;
                }

                // Reset tap count.
                // NOTE: We are basing this on startTime rather than adding on end time of the last touch. The reason is
                //       that to do so we would have to add another record to keep track of timestamps for each touch. And
                //       since we know the maximum time that a tap can take, we have a reasonable estimate for when a prior
                //       tap must have ended.
                if (touchStatePtr->tapCount > 0 && InputState.currentTime >= touchStatePtr->startTime + s_TapTime + s_TapDelayTime)
                {
                    touchStatePtr->tapCount = 0;
                    haveChangedState = true;
                }
            }

            var primaryTouchState = (TouchState*)((byte*)statePtr + stateBlock.byteOffset);
            if (primaryTouchState->delta != default)
            {
                primaryTouchState->delta = Vector2.zero;
                haveChangedState = true;
            }
            if (primaryTouchState->tapCount > 0 && InputState.currentTime >= primaryTouchState->startTime + s_TapTime + s_TapDelayTime)
            {
                primaryTouchState->tapCount = 0;
                haveChangedState = true;
            }

            Profiler.EndSample();

            return haveChangedState;
        }

        unsafe bool IInputStateCallbackReceiver.OnCarryStateForward(void* statePtr)
        {
            return OnCarryStateForward(statePtr);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", Justification = "Cannot satisfy both CA1801 and CA1033 (the latter requires adding this method)")]
        protected new unsafe bool OnReceiveStateWithDifferentFormat(void* statePtr, FourCC stateFormat, uint stateSize,
            ref uint offsetToStoreAt, InputEventPtr eventPtr)
        {
            if (stateFormat != TouchState.kFormat)
                return false;

            // We don't allow partial updates for TouchStates.
            if (eventPtr.IsA<DeltaStateEvent>())
                return false;

            Profiler.BeginSample("TouchAllocate");

            // For performance reasons, we read memory here directly rather than going through
            // ReadValue() of the individual TouchControl children. This means that Touchscreen,
            // unlike other devices, is hardwired to a single memory layout only.

            var newTouchState = (TouchState*)((byte*)statePtr);
            var currentTouchState = (TouchState*)((byte*)currentStatePtr + touches[0].stateBlock.byteOffset);
            var primaryTouchState = (TouchState*)((byte*)currentStatePtr + primaryTouch.stateBlock.byteOffset);
            var touchControlCount = touches.Count;

            ////REVIEW: The logic in here makes us inherently susceptible to the ordering of the touch events in the event
            ////        stream. I believe we have platforms (Android?) that send us touch events finger-by-finger (or touch-by-touch?)
            ////        rather than sorted by time. This will probably screw up the logic in here.

            // If it's an ongoing touch, try to find the TouchState we have allocated to the touch
            // previously.
            var phase = newTouchState->phase;
            if (phase != TouchPhase.Began)
            {
                var touchId = newTouchState->touchId;
                for (var i = 0; i < touchControlCount; ++i)
                {
                    if (currentTouchState[i].touchId == touchId)
                    {
                        offsetToStoreAt = (uint)i * TouchState.kSizeInBytes + TouchscreenState.kTouchDataOffset;

                        // Preserve primary touch state.
                        var isPrimaryTouch = currentTouchState[i].isPrimaryTouch;
                        var isInheritedPrimaryTouch = currentTouchState[i].isInheritedPrimaryTouch;
                        newTouchState->isPrimaryTouch = isPrimaryTouch;
                        newTouchState->isInheritedPrimaryTouch = isInheritedPrimaryTouch;

                        // Compute delta if touch doesn't have one.
                        if (newTouchState->delta == default)
                            newTouchState->delta = newTouchState->position - currentTouchState[i].position;

                        // Accumulate delta.
                        newTouchState->delta += currentTouchState[i].delta;

                        // Keep start time and position.
                        newTouchState->startTime = currentTouchState[i].startTime;
                        newTouchState->startPosition = currentTouchState[i].startPosition;

                        // Detect taps.
                        var isTap = newTouchState->isNoneEndedOrCanceled &&
                            (eventPtr.time - newTouchState->startTime) <= s_TapTime &&
                            ////REVIEW: this only takes the final delta to start position into account, not the delta over the lifetime of the
                            ////        touch; is this robust enough or do we need to make sure that we never move more than the tap radius
                            ////        over the entire lifetime of the touch?
                            (newTouchState->position - newTouchState->startPosition).sqrMagnitude <= s_TapRadiusSquared;
                        if (isTap)
                            newTouchState->tapCount = (byte)(currentTouchState[i].tapCount + 1);
                        else
                            newTouchState->tapCount = currentTouchState[i].tapCount; // Preserve tap count; reset in OnCarryStateForward.

                        // Update primary touch.
                        if (isPrimaryTouch)
                        {
                            if (newTouchState->isNoneEndedOrCanceled)
                            {
                                ////REVIEW: also reset tapCounts here when tap delay time has expired on the touch?

                                newTouchState->isPrimaryTouch = false;
                                newTouchState->isInheritedPrimaryTouch = false;

                                // Primary touch was ended. See if we have another ongoing touch that can take
                                // over.
                                var haveNewPrimaryTouch = false;
                                for (var n = 0; n < touchControlCount; ++n)
                                {
                                    if (n == i)
                                        continue;

                                    if (currentTouchState[n].isInProgress)
                                    {
                                        ////REVIEW: change timing on touch
                                        var newPrimaryTouch = *(currentTouchState + n);
                                        newPrimaryTouch.isPrimaryTouch = true;
                                        newPrimaryTouch.isInheritedPrimaryTouch = true;
                                        InputState.Change(touches[n], newPrimaryTouch, eventPtr: eventPtr);
                                        newPrimaryTouch.phase = TouchPhase.Moved;
                                        InputState.Change(primaryTouch, newPrimaryTouch, eventPtr: eventPtr);
                                        haveNewPrimaryTouch = true;
                                        break;
                                    }
                                }

                                if (!haveNewPrimaryTouch)
                                {
                                    // Tap on primary touch is only triggered if the touch never switched fingers.
                                    if (isTap && !isInheritedPrimaryTouch)
                                        TriggerTap(primaryTouch, newTouchState, eventPtr);
                                    else
                                        InputState.Change(primaryTouch, *newTouchState, eventPtr: eventPtr);
                                }
                            }
                            else
                            {
                                // Primary touch was updated.
                                InputState.Change(primaryTouch, *newTouchState, eventPtr: eventPtr);
                            }
                        }

                        if (isTap)
                        {
                            // Make tap button go down and up. InputManager will write the new touch state
                            // into touches[i] after we return from this method so we suppress writing the
                            // release directly ourselves.
                            //
                            // NOTE: We do this here instead of right away up there when we detect the touch so
                            //       that the state change notifications go together. First those for the primary
                            //       touch, then the ones for the touch record itself.
                            TriggerTap(touches[i], newTouchState, eventPtr, writeRelease: false);
                        }

                        Profiler.EndSample();
                        return true;
                    }
                }

                // Couldn't find an entry. Either it was a touch that we previously ran out of available
                // entries for or it's an event sent out of sequence. Ignore the touch to be consistent.

                Profiler.EndSample();
                return false;
            }

            // It's a new touch. Try to find an unused TouchState.
            for (var i = 0; i < touchControlCount; ++i, ++currentTouchState)
            {
                // NOTE: We're overwriting any ended touch immediately here. This means we immediately overwrite even
                //       if we still have other unused slots. What this gives us is a completely predictable touch #0..#N
                //       sequence (i.e. touch #N is only ever used if there are indeed #N concurrently touches). However,
                //       it does mean that we overwrite state aggressively. If you are not using actions or the higher-level
                //       Touch API, be aware of this!
                if (currentTouchState->isNoneEndedOrCanceled)
                {
                    offsetToStoreAt = (uint)i * TouchState.kSizeInBytes + TouchscreenState.kTouchDataOffset;
                    newTouchState->delta = Vector2.zero;
                    newTouchState->startTime = eventPtr.time;
                    newTouchState->startPosition = newTouchState->position;

                    // Tap counts are preserved from prior touches on the same finger.
                    newTouchState->tapCount = currentTouchState->tapCount;

                    // Make primary touch, if there's none currently.
                    if (primaryTouchState->isNoneEndedOrCanceled)
                    {
                        newTouchState->isPrimaryTouch = true;
                        newTouchState->isInheritedPrimaryTouch = false;
                        InputState.Change(primaryTouch, *newTouchState, eventPtr: eventPtr);
                    }

                    Profiler.EndSample();
                    return true;
                }
            }

            // We ran out of state and we don't want to stomp an existing ongoing touch.
            // Drop this touch entirely.
            // NOTE: Getting here means we're having fewer touch entries than the number of concurrent touches supported
            //       by the backend (or someone is simply sending us nonsense data).

            Profiler.EndSample();
            return false;
        }

        unsafe bool IInputStateCallbackReceiver.OnReceiveStateWithDifferentFormat(void* statePtr, FourCC stateFormat, uint stateSize,
            ref uint offsetToStoreAt, InputEventPtr eventPtr)
        {
            return OnReceiveStateWithDifferentFormat(statePtr, stateFormat, stateSize, ref offsetToStoreAt, eventPtr);
        }

        unsafe void IInputStateCallbackReceiver.OnBeforeWriteNewState(void* oldStatePtr, InputEventPtr newState)
        {
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", Justification = "Cannot satisfy both CA1801 and CA1033 (the latter requires adding this method)")]
        protected new unsafe void OnBeforeWriteNewState(void* oldStatePtr, InputEventPtr newState)
        {
        }

        // We can only detect taps on touch *release*. At which point it acts like button that triggers and releases
        // in one operation.
        private static unsafe void TriggerTap(TouchControl control, TouchState* state, InputEventPtr eventPtr, bool writeRelease = true)
        {
            ////REVIEW: we're updating the entire TouchControl here; we could update just the tap state using a delta event; problem
            ////        is that the tap *down* still needs a full update on the state

            // We don't increase tapCount here as we may be sending the tap from the same state to both the TouchControl
            // that got tapped and to primaryTouch.

            // Press.
            state->isTap = true;
            InputState.Change(control, *state, eventPtr: eventPtr);

            // Release.
            state->isTap = false;
            if (writeRelease)
                InputState.Change(control, *state, eventPtr: eventPtr);
        }

        internal static float s_TapTime;
        internal static float s_TapDelayTime;
        internal static float s_TapRadiusSquared;
    }
}
