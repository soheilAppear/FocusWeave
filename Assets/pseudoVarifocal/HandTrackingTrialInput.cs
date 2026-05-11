// HandTrackingTrialInput.cs
// Attach this to the same GameObject as TargetAppear (or anywhere), and assign references in Inspector.
//
// Requirements:
// - Oculus Integration / Meta XR: OVRHand components exist (LeftHand, RightHand).
// - Hand Tracking enabled in OVRManager / Project settings for Quest.
// - TargetAppear.enableControllerInput can be left true, because this script only activates when controllers are absent,
//   unless you force hand input.

using UnityEngine;
using OculusSampleFramework;

public class HandTrackingTrialInput : MonoBehaviour
{
    [Header("References")]
    public TargetAppear target;
    public OVRHand leftHand;
    public OVRHand rightHand;

    [Header("Activation")]
    [Tooltip("Auto = hands only when controllers are not connected. HandOnly = always hands. ControllerOnly = never hands.")]
    public InputMode inputMode = InputMode.Auto;

    public enum InputMode
    {
        Auto,
        HandOnly,
        ControllerOnly
    }

    [Header("Pinch Detection")]
    [Range(0.0f, 1.0f)]
    public float pinchStrengthThreshold = 0.75f;

    [Tooltip("How long the pinch must be held before firing the action (seconds).")]
    public float dwellSeconds = 0.12f;

    [Tooltip("Cooldown after an action fires (seconds).")]
    public float cooldownSeconds = 0.35f;

    [Header("Optional Safety")]
    [Tooltip("If true, requires hand tracking confidence to be High before accepting gestures.")]
    public bool requireHighConfidence = true;

    // Per-gesture state
    private float _rtIndexStart = -1f;
    private float _rtMiddleStart = -1f;

    private float _ltIndexStart = -1f;
    private float _ltMiddleStart = -1f;
    private float _ltRingStart = -1f;
    private float _ltPinkyStart = -1f;
    private float _ltRestoreStart = -1f;

    private float _cooldownUntil = 0f;

    private void Reset()
    {
        if (target == null) target = FindAnyObjectByType<TargetAppear>();
    }

    private void Update()
    {
        if (target == null)
            return;

        if (!ShouldUseHandsNow())
            return;

        if (Time.time < _cooldownUntil)
            return;

        bool rightUsable = IsHandUsable(rightHand);
        bool leftUsable = IsHandUsable(leftHand);

        // Right hand controls trial advance/back.
        if (rightUsable)
        {
            if (TryFireOnPinch(rightHand, OVRHand.HandFinger.Index, ref _rtIndexStart, OnAdvance)) return;
            if (TryFireOnPinch(rightHand, OVRHand.HandFinger.Middle, ref _rtMiddleStart, OnBack)) return;
        }
        else
        {
            _rtIndexStart = -1f;
            _rtMiddleStart = -1f;
        }

        if (!leftUsable)
        {
            ResetLeftGestureTimers();
            return;
        }

        // Check the chord before individual left-hand actions so it cannot be stolen by index/middle.
        if (TryFireRestoreViewChord()) return;

        // Left hand controls reset and view modes.
        if (TryFireOnPinch(leftHand, OVRHand.HandFinger.Index, ref _ltIndexStart, OnResetRig)) return;
        if (TryFireOnPinch(leftHand, OVRHand.HandFinger.Middle, ref _ltMiddleStart, OnForceBlank)) return;
        if (TryFireOnPinch(leftHand, OVRHand.HandFinger.Ring, ref _ltRingStart, OnForcePassthrough)) return;
        TryFireOnPinch(leftHand, OVRHand.HandFinger.Pinky, ref _ltPinkyStart, OnForceVirtual);
    }

    private bool ShouldUseHandsNow()
    {
        if (inputMode == InputMode.ControllerOnly) return false;
        if (inputMode == InputMode.HandOnly) return true;

        // Auto: use hands when Touch controllers are not connected
        bool lConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
        bool rConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
        bool anyController = lConnected || rConnected;

        return !anyController;
    }

    private bool IsStrongPinch(OVRHand hand, OVRHand.HandFinger finger)
    {
        if (hand == null) return false;
        if (!hand.IsTracked) return false;

        float strength = hand.GetFingerPinchStrength(finger);
        return strength >= pinchStrengthThreshold;
    }

    private bool IsHandUsable(OVRHand hand)
    {
        if (hand == null) return false;
        if (!hand.IsTracked) return false;
        if (requireHighConfidence && hand.HandConfidence != OVRHand.TrackingConfidence.High) return false;
        return true;
    }

    private bool TryFireRestoreViewChord()
    {
        bool leftIndex = IsStrongPinch(leftHand, OVRHand.HandFinger.Index);
        bool leftMiddle = IsStrongPinch(leftHand, OVRHand.HandFinger.Middle);

        if (!leftIndex || !leftMiddle)
        {
            _ltRestoreStart = -1f;
            return false;
        }

        _ltIndexStart = -1f;
        _ltMiddleStart = -1f;

        if (_ltRestoreStart < 0f)
            _ltRestoreStart = Time.time;

        if (Time.time - _ltRestoreStart < dwellSeconds)
            return true;

        target.RestoreViewForCurrentState();
        ArmCooldown();
        ResetLeftGestureTimers();
        return true;
    }

    private bool TryFireOnPinch(OVRHand hand, OVRHand.HandFinger finger, ref float pinchStartTime, System.Action action)
    {
        bool pinching = IsStrongPinch(hand, finger);

        if (!pinching)
        {
            pinchStartTime = -1f;
            return false;
        }

        if (pinchStartTime < 0f)
            pinchStartTime = Time.time;

        if (Time.time - pinchStartTime >= dwellSeconds)
        {
            action?.Invoke();
            ArmCooldown();
            pinchStartTime = -1f;
            return true;
        }

        return false;
    }

    private void ResetLeftGestureTimers()
    {
        _ltIndexStart = -1f;
        _ltMiddleStart = -1f;
        _ltRingStart = -1f;
        _ltPinkyStart = -1f;
        _ltRestoreStart = -1f;
    }

    private void ArmCooldown()
    {
        _cooldownUntil = Time.time + cooldownSeconds;
    }

    // Actions
    private void OnAdvance()
    {
        target.Advance();
    }

    private void OnBack()
    {
        target.Back();
    }

    private void OnResetRig()
    {
        target.ResetRigToPreferred();
    }

    private void OnForceBlank()
    {
        target.ForceView(TargetAppear.ViewMode.Blank);
    }

    private void OnForcePassthrough()
    {
        target.ForceView(TargetAppear.ViewMode.Passthrough);
    }

    private void OnForceVirtual()
    {
        target.ForceView(TargetAppear.ViewMode.VirtualOnly);
    }
}
