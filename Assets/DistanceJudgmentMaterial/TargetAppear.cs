// TargetAppear.cs
// Updated to: (1) use Rigidbody.velocity (portable), (2) expose public state helpers,
// (3) add visibility alias for other scripts, (4) optional controller input enable/disable.
//
// NOTE: This file remains in OculusSampleFramework namespace.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace OculusSampleFramework
{
    public class TargetAppear : MonoBehaviour
    {
        // ----------------------------
        // Scene references
        // ----------------------------

        [Header("Scene References")]
        [Tooltip("Root GameObject for your virtual environment (meshes and colliders). Do NOT disable this root.")]
        public GameObject LabEnvironment;

        [Tooltip("Camera/GameObject used for the blank screen state (for walking).")]
        public GameObject camBlank;

        [Tooltip("Camera/GameObject used for the normal viewing state.")]
        public GameObject NormalCam;

        [Tooltip("OVR Passthrough Layer component. If null, we try GetComponent<OVRPassthroughLayer>().")]
        public OVRPassthroughLayer passthroughLayer;

        // ----------------------------
        // Targets
        // ----------------------------

        [Header("Targets")]
        [Tooltip("Your target objects (we toggle their Renderer and Colliders on/off and move them).")]
        public GameObject[] BochaoTargets = new GameObject[5];

        // Public outputs for gaze-contingent logic
        public Transform CurrentTargetTransform { get; private set; }
        public float CurrentTargetDistanceMeters { get; private set; }
        public bool IsTargetVisibleForGaze { get; private set; }

        // Compatibility alias for older scripts that look for this property name
        public bool IsCurrentlyVisible => IsTargetVisibleForGaze;

        // Increments whenever we show a target for a trial
        public int TargetChangeStamp { get; private set; } = 0;

        // Convenience flags
        public bool IsInShowTargetState => state == ExpState.EXP_SHOW_TARGET;
        public bool HasCurrentTarget => CurrentTargetTransform != null;

        // ----------------------------
        // Player rig / reset
        // ----------------------------

        [Header("Player Rig / Reset")]
        public Transform BeginPoint;
        public Transform resetTransform;
        public GameObject player;
        public Camera playerHead;

        public bool autoFindRigReferences = true;
        public bool resetXZOnly = true;
        public bool resetOnStart = true;
        public bool resetEachTrial = true;

        // ----------------------------
        // Controller input (optional)
        // ----------------------------

        [Header("Controller Input (Optional)")]
        [Tooltip("If false, TargetAppear will not read controller buttons in Update(). Useful if hands will control everything.")]
        public bool enableControllerInput = true;

        [Tooltip("Speed (m/s) for right-joystick walk/strafe movement.")]
        public float playerMoveSpeed = 1.5f;

        [Header("Right Trigger Behavior")]
        public bool rightTriggerResetsToBeginPoint = true;
        public bool triggerResetForcesShowTargetView = true;
        public bool triggerResetReturnsToShowTargetState = false;

        // ----------------------------
        // View modes
        // ----------------------------

        public enum ViewMode
        {
            VirtualOnly,
            Passthrough,
            Blank
        }

        [Header("View Mode per Experiment State")]
        public ViewMode startMode = ViewMode.VirtualOnly;
        public ViewMode showTargetMode = ViewMode.VirtualOnly;
        public ViewMode walkMode = ViewMode.Blank;

        [Header("Environment Visibility Policy")]
        public bool keepEnvironmentCollidersEnabled = true;
        public bool showEnvironmentVisualsInPassthrough = false;
        public bool showEnvironmentVisualsInVirtual = true;
        public bool showEnvironmentVisualsInBlank = false;

        // ----------------------------
        // Trial design
        // ----------------------------

        [Header("Trial Design")]
        public float[] AllTrials = new float[17];
        public int[] isPractice = new int[17];
        public int[] generatedRandomTargetObjects = new int[17];
        public int currentTrial = 0;

        // ----------------------------
        // Focus mode per trial
        // ----------------------------

        [Header("Focus Mode (ChromaBlur)")]
        [Tooltip("Optional: DOF driver. If assigned, focusMode is set automatically at each trial start.")]
        public GazeDrivenDepthOfFieldPPv2 dofDriver;

        [Tooltip("One entry per trial (must match AllTrials length). Monochrome = existing blur; Chromatic = LCA simulation.")]
        public GazeDrivenDepthOfFieldPPv2.FocusMode[] trialFocusModes = new GazeDrivenDepthOfFieldPPv2.FocusMode[17];

        // ----------------------------
        // CSV logging
        // ----------------------------

        private string _csvPath;
        private float _trialStartTime;

        private static readonly float[] _strengthSteps = { 0.5f, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 3.5f, 4.0f };
        private int _strengthStepIndex = 1; // default 1.0f

        private bool _leftGripWasDown = false;
        private bool _rightGripWasDown = false;

        private readonly List<float> Pre_List = new List<float> { 3.5f, 4.5f };
        private readonly List<float> lst = new List<float> { 2f, 2f, 2f, 2.5f, 3f, 3f, 3f, 3.5f, 4f, 4f, 4f, 4.5f, 5f, 5f, 5f };
        private readonly List<int> Target_Objects_lst = new List<int> { 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4 };

        // ----------------------------
        // Experiment state machine
        // ----------------------------

        public enum ExpState
        {
            EXP_START = 0,
            EXP_SHOW_TARGET = 1,
            EXP_WALK = 2,
            EXP_RECORDED = 3,
            EXP_FINISHED = 4
        }

        [Header("Experiment State")]
        public ExpState state = ExpState.EXP_START;

        [Header("Target preview policy")]
        public bool hideTargetWhenWalking = true;

        [Header("Target Placement")]
        [Tooltip("World-space Y position for the target (0 = floor level).")]
        public float targetWorldY = 0f;

        [Header("Target Visuals")]
        [Tooltip("Apply bright emissive materials so targets are clearly visible in VR.")]
        public bool autoApplyTargetMaterials = true;
        [Tooltip("One distinct color per target slot. Bright HDR values produce a visible glow.")]
        public Color[] targetColors = new Color[]
        {
            new Color(1.0f, 0.92f, 0.02f),  // vivid yellow
            new Color(1.0f, 0.35f, 0.00f),  // deep orange
            new Color(0.0f, 0.85f, 1.00f),  // cyan
            new Color(0.1f, 1.00f, 0.30f),  // bright green
            new Color(1.0f, 0.10f, 0.90f),  // magenta
        };
        [Range(0f, 6f)]
        [Tooltip("HDR emission multiplier. Values above 1 produce a glow in VR.")]
        public float emissionIntensity = 3f;

        private Material[] _targetMaterials;

        // ----------------------------
        // Debug / utilities
        // ----------------------------

        [Header("Debug")]
        public bool debugLog = true;
        public bool enableDebugHotkeys = true;

        [Header("Target Collider")]
        public bool autoAddColliderToTargets = true;
        public float minAutoColliderRadius = 0.05f;

        private void Awake()
        {
            if (passthroughLayer == null)
                passthroughLayer = GetComponent<OVRPassthroughLayer>();

            if (LabEnvironment != null && transform.IsChildOf(LabEnvironment.transform))
            {
                Debug.LogWarning(
                    "[TargetAppear] This script is under LabEnvironment. " +
                    "We do not disable LabEnvironment root, so it can work, but it is cleaner to keep it outside."
                );
            }
        }

        private void Start()
        {
            if (autoFindRigReferences)
                AutoFindRigReferences();

            BuildTrialsAndTargets();
            ClearAllTargets();

            InitCsvLog();

            if (resetOnStart)
                ResetToReference(GetPreferredResetReference());

            ApplyViewMode(startMode);

            if (debugLog)
            {
                Debug.Log("[TargetAppear] Started. Use input (controller or hands) to begin.");
                LogButtonMap();
            }
        }

        // ----------------------------
        // PUBLIC API (for hand tracking or other input)
        // ----------------------------

        public void Advance()
        {
            AdvanceState();
        }

        public void Back()
        {
            BackState();
        }

        public void ResetRigToPreferred()
        {
            ResetToReference(GetPreferredResetReference());
        }

        public void ForceView(ViewMode mode)
        {
            ApplyViewMode(mode);
        }

        public void RestoreViewForCurrentState()
        {
            // Useful if you forced blank/passthrough and want to return to the state policy
            ApplyViewMode(GetViewModeForState(state));
        }

        public void ReshowCurrentTrialTargetIfAppropriate()
        {
            if (state == ExpState.EXP_SHOW_TARGET)
                ShowTargetForCurrentTrial();
        }

        // ----------------------------
        // Internals
        // ----------------------------

        private ViewMode GetViewModeForState(ExpState s)
        {
            switch (s)
            {
                case ExpState.EXP_START: return startMode;
                case ExpState.EXP_SHOW_TARGET: return showTargetMode;
                case ExpState.EXP_WALK: return walkMode;
                case ExpState.EXP_RECORDED: return showTargetMode;
                case ExpState.EXP_FINISHED: return showTargetMode;
                default: return showTargetMode;
            }
        }

        private void BuildTrialsAndTargets()
        {
            var preRand = new System.Random();
            var preShuffled = Pre_List.OrderBy(_ => preRand.Next()).ToArray();

            var rand = new System.Random();
            var shuffled = lst.OrderBy(_ => rand.Next()).ToArray();

            var targetRand = new System.Random();
            generatedRandomTargetObjects = Target_Objects_lst.OrderBy(_ => targetRand.Next()).ToArray();

            Array.Copy(preShuffled, 0, AllTrials, 0, preShuffled.Length);
            Array.Copy(shuffled, 0, AllTrials, preShuffled.Length, shuffled.Length);

            for (int i = 0; i < AllTrials.Length; i++)
                isPractice[i] = i < preShuffled.Length ? 1 : 0;
        }

        private void ApplyViewMode(ViewMode mode)
        {
            if (NormalCam != null)
                NormalCam.SetActive(mode != ViewMode.Blank);

            if (camBlank != null)
                camBlank.SetActive(mode == ViewMode.Blank);

            bool passthroughOn = (mode == ViewMode.Passthrough);
            if (passthroughLayer != null)
                passthroughLayer.enabled = passthroughOn;

            bool wantVisuals =
                (mode == ViewMode.Passthrough && showEnvironmentVisualsInPassthrough) ||
                (mode == ViewMode.VirtualOnly && showEnvironmentVisualsInVirtual) ||
                (mode == ViewMode.Blank && showEnvironmentVisualsInBlank);

            bool wantColliders = keepEnvironmentCollidersEnabled;

            SetEnvironmentRenderersVisible(wantVisuals);
            SetEnvironmentCollidersEnabled(wantColliders);

            if (debugLog)
            {
                Debug.Log(
                    "[TargetAppear] ApplyViewMode: " + mode +
                    " passthrough=" + (passthroughOn ? "on" : "off") +
                    " envVisuals=" + (wantVisuals ? "on" : "off") +
                    " envColliders=" + (wantColliders ? "on" : "off")
                );
            }
        }

        private void SetEnvironmentRenderersVisible(bool visible)
        {
            if (LabEnvironment == null) return;
            var renderers = LabEnvironment.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                renderers[i].enabled = visible;
        }

        private void SetEnvironmentCollidersEnabled(bool enabled)
        {
            if (LabEnvironment == null) return;
            var cols = LabEnvironment.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
                cols[i].enabled = enabled;
        }

        public bool IsColliderFromCurrentTarget(Collider col)
        {
            if (col == null) return false;
            if (CurrentTargetTransform == null) return false;

            Transform t = col.transform;
            return (t == CurrentTargetTransform) || t.IsChildOf(CurrentTargetTransform);
        }

        private void ClearAllTargets()
        {
            CurrentTargetTransform = null;
            CurrentTargetDistanceMeters = 0f;
            IsTargetVisibleForGaze = false;

            for (int i = 0; i < BochaoTargets.Length; i++)
            {
                GameObject t = BochaoTargets[i];
                if (t == null) continue;
                SetTargetVisualAndColliderState(t, visualsEnabled: false, collidersEnabled: false);
            }
        }

        private void ShowTargetForCurrentTrial()
        {
            ClearAllTargets();

            if (currentTrial < 0 || currentTrial >= AllTrials.Length)
                return;

            int safeTrialIndex = Mathf.Clamp(currentTrial, 0, generatedRandomTargetObjects.Length - 1);
            int targetIndex = generatedRandomTargetObjects[safeTrialIndex];

            if (targetIndex < 0 || targetIndex >= BochaoTargets.Length)
                return;

            GameObject target = BochaoTargets[targetIndex];
            if (target == null)
                return;

            float d = AllTrials[currentTrial];
            // Place the target d meters forward from BeginPoint so distances are
            // measured from the participant's starting position, not the rig origin.
            Transform origin = BeginPoint != null ? BeginPoint :
                               (resetTransform != null ? resetTransform : transform);
            Vector3 worldPos = origin.position + origin.forward * d;
            worldPos.y = targetWorldY;
            target.transform.position = worldPos;

            if (autoAddColliderToTargets)
                EnsureTargetHasCollider(target);

            if (autoApplyTargetMaterials)
                ApplyTargetMaterial(target, targetIndex);

            SetTargetVisualAndColliderState(target, visualsEnabled: true, collidersEnabled: true);

            CurrentTargetTransform = target.transform;
            CurrentTargetDistanceMeters = d;
            IsTargetVisibleForGaze = true;

            TargetChangeStamp++;

            if (dofDriver != null && currentTrial < trialFocusModes.Length)
                dofDriver.focusMode = trialFocusModes[currentTrial];

            _trialStartTime = Time.time;

            if (debugLog)
            {
                Debug.Log(
                    "[TargetAppear] ShowTarget trial=" + currentTrial +
                    " dist=" + d.ToString("F2") + "m targetIndex=" + targetIndex +
                    " practice=" + isPractice[currentTrial] +
                    " stamp=" + TargetChangeStamp
                );
            }
        }

        private void SetTargetVisualAndColliderState(GameObject target, bool visualsEnabled, bool collidersEnabled)
        {
            if (target == null) return;

            var rends = target.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
                rends[i].enabled = visualsEnabled;

            var cols = target.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
                cols[i].enabled = collidersEnabled;
        }

        private void ApplyTargetMaterial(GameObject target, int targetIndex)
        {
            if (target == null) return;

            if (_targetMaterials == null || _targetMaterials.Length != BochaoTargets.Length)
                _targetMaterials = new Material[BochaoTargets.Length];

            Color baseColor = (targetIndex >= 0 && targetIndex < targetColors.Length)
                ? targetColors[targetIndex]
                : Color.yellow;

            if (_targetMaterials[targetIndex] == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                if (shader == null) return;

                var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

                if (mat.HasProperty("_BaseColor"))   mat.SetColor("_BaseColor", baseColor);
                if (mat.HasProperty("_Color"))       mat.SetColor("_Color", baseColor);
                if (mat.HasProperty("_Smoothness"))  mat.SetFloat("_Smoothness", 0.55f);
                if (mat.HasProperty("_Metallic"))    mat.SetFloat("_Metallic", 0f);
                if (mat.HasProperty("_EmissionColor"))
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", baseColor * emissionIntensity);
                }

                _targetMaterials[targetIndex] = mat;
            }
            else
            {
                var mat = _targetMaterials[targetIndex];
                if (mat.HasProperty("_BaseColor"))     mat.SetColor("_BaseColor", baseColor);
                if (mat.HasProperty("_Color"))         mat.SetColor("_Color", baseColor);
                if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", baseColor * emissionIntensity);
            }

            foreach (var r in target.GetComponentsInChildren<Renderer>(true))
                r.sharedMaterial = _targetMaterials[targetIndex];
        }

        private void OnDestroy()
        {
            if (_targetMaterials == null) return;
            for (int i = 0; i < _targetMaterials.Length; i++)
            {
                if (_targetMaterials[i] == null) continue;
                if (Application.isPlaying) Destroy(_targetMaterials[i]);
                else DestroyImmediate(_targetMaterials[i]);
            }
            _targetMaterials = null;
        }

        private void EnsureTargetHasCollider(GameObject target)
        {
            if (target == null) return;

            if (target.GetComponentInChildren<Collider>(true) != null)
                return;

            var rend = target.GetComponentInChildren<Renderer>(true);
            if (rend != null)
            {
                Bounds b = rend.bounds;

                Vector3 localCenter = target.transform.InverseTransformPoint(b.center);
                float worldRadius = b.extents.magnitude;

                Vector3 s = target.transform.lossyScale;
                float maxScale = Mathf.Max(1e-6f, Mathf.Max(s.x, Mathf.Max(s.y, s.z)));
                float localRadius = Mathf.Max(minAutoColliderRadius, worldRadius / maxScale);

                var sc = target.AddComponent<SphereCollider>();
                sc.center = localCenter;
                sc.radius = localRadius;

                if (debugLog)
                    Debug.Log("[TargetAppear] Added SphereCollider to target: " + target.name + " radius(local)=" + localRadius.ToString("F3"));

                return;
            }

            var scFallback = target.AddComponent<SphereCollider>();
            scFallback.center = Vector3.zero;
            scFallback.radius = Mathf.Max(minAutoColliderRadius, 0.1f);

            if (debugLog)
                Debug.Log("[TargetAppear] Added fallback SphereCollider to target: " + target.name);
        }

        private Transform GetPreferredResetReference()
        {
            if (BeginPoint != null) return BeginPoint;
            return resetTransform;
        }

        public void ResetToReference(Transform reference)
        {
            if (reference == null)
            {
                Debug.LogWarning("[TargetAppear] ResetToReference failed: reference is null. Assign BeginPoint or resetTransform.");
                return;
            }

            if (playerHead == null && Camera.main != null)
                playerHead = Camera.main;

            if (player == null || playerHead == null)
            {
                Debug.LogWarning("[TargetAppear] ResetToReference failed: assign player (rig root) and playerHead (HMD camera).");
                return;
            }

            Transform playerRoot = player.transform;

            // Prefer OVR's CenterEyeAnchor for accurate physical head position on Quest
            Transform headT = playerHead.transform;
            OVRCameraRig ovrRig = player.GetComponent<OVRCameraRig>();
            if (ovrRig == null) ovrRig = player.GetComponentInChildren<OVRCameraRig>();
            if (ovrRig != null && ovrRig.centerEyeAnchor != null)
                headT = ovrRig.centerEyeAnchor;

            var cc = player.GetComponent<CharacterController>();
            var ovrpc = player.GetComponent<OVRPlayerController>();

            if (ovrpc != null) ovrpc.enabled = false;
            if (cc != null) cc.enabled = false;

            var rb = player.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;        // Unity portable
                rb.angularVelocity = Vector3.zero;
            }

            float targetYaw = reference.eulerAngles.y;
            float headYaw = headT.eulerAngles.y;
            float deltaYaw = Mathf.DeltaAngle(headYaw, targetYaw);

            playerRoot.RotateAround(headT.position, Vector3.up, deltaYaw);

            Vector3 headPosAfterRot = headT.position;
            Vector3 targetHeadPos = reference.position;

            // Always XZ-only in VR: Y is determined by the physical headset height, never overridden
            Vector3 translation = new Vector3(
                targetHeadPos.x - headPosAfterRot.x,
                0f,
                targetHeadPos.z - headPosAfterRot.z
            );

            playerRoot.position += translation;

            if (cc != null) cc.enabled = true;
            if (ovrpc != null) ovrpc.enabled = true;

            if (debugLog)
            {
                Debug.Log(
                    "[TargetAppear] ResetToReference done ref=" + reference.name +
                    " resetXZOnly=" + resetXZOnly +
                    " deltaYaw=" + deltaYaw.ToString("F2")
                );
            }
        }

        private void AutoFindRigReferences()
        {
            // If you want auto-find for playerHead too, enable this block.
            // Using conditional compilation avoids old Unity API warnings on newer versions.
#if UNITY_2023_1_OR_NEWER
            if (playerHead == null)
            {
                if (Camera.main != null)
                    playerHead = Camera.main;
                else
                {
                    var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);
                    for (int i = 0; i < cams.Length; i++)
                    {
                        if (cams[i] != null && cams[i].enabled)
                        {
                            playerHead = cams[i];
                            break;
                        }
                    }
                }
            }
#else
            if (playerHead == null && Camera.main != null)
                playerHead = Camera.main;
#endif

            if (player == null && playerHead != null)
            {
                Transform candidate = playerHead.transform;

                for (int i = 0; i < 10 && candidate != null; i++)
                {
                    if (candidate.name.IndexOf("OVRCameraRig", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        player = candidate.gameObject;
                        break;
                    }
                    candidate = candidate.parent;
                }

                if (player == null)
                {
                    Transform root = playerHead.transform;
                    while (root.parent != null)
                        root = root.parent;

                    if (LabEnvironment == null || root.gameObject != LabEnvironment)
                        player = root.gameObject;
                }
            }

            if (debugLog)
            {
                Debug.Log(
                    "[TargetAppear] AutoFindRigReferences player=" + (player ? player.name : "null") +
                    " playerHead=" + (playerHead ? playerHead.name : "null")
                );
            }
        }

        private void AdvanceState()
        {
            switch (state)
            {
                case ExpState.EXP_START:
                {
                    state = ExpState.EXP_SHOW_TARGET;
                    currentTrial = 0;

                    if (resetEachTrial)
                        ResetToReference(GetPreferredResetReference());

                    ApplyViewMode(showTargetMode);
                    ShowTargetForCurrentTrial();

                    if (debugLog)
                        Debug.Log("[TargetAppear] State EXP_SHOW_TARGET. Advance to start walking.");

                    break;
                }

                case ExpState.EXP_SHOW_TARGET:
                {
                    state = ExpState.EXP_WALK;

                    if (hideTargetWhenWalking)
                        ClearAllTargets();

                    ApplyViewMode(walkMode);

                    if (debugLog)
                        Debug.Log("[TargetAppear] State EXP_WALK. Advance when reached target.");

                    break;
                }

                case ExpState.EXP_WALK:
                {
                    state = ExpState.EXP_RECORDED;

                    LogTrialToCsv();

                    if (debugLog)
                        Debug.Log("[TargetAppear] State EXP_RECORDED. Record response, then advance for next trial.");

                    break;
                }

                case ExpState.EXP_RECORDED:
                {
                    if (currentTrial + 1 >= AllTrials.Length)
                    {
                        state = ExpState.EXP_FINISHED;

                        ClearAllTargets();
                        ApplyViewMode(showTargetMode);

                        if (debugLog)
                            Debug.Log("[TargetAppear] State EXP_FINISHED. Done.");
                    }
                    else
                    {
                        state = ExpState.EXP_SHOW_TARGET;
                        currentTrial++;

                        if (resetEachTrial)
                            ResetToReference(GetPreferredResetReference());

                        ApplyViewMode(showTargetMode);
                        ShowTargetForCurrentTrial();

                        if (debugLog)
                            Debug.Log("[TargetAppear] Next trial: EXP_SHOW_TARGET.");
                    }

                    break;
                }

                case ExpState.EXP_FINISHED:
                default:
                {
                    if (debugLog)
                        Debug.Log("[TargetAppear] Already finished.");
                    break;
                }
            }
        }

        private void BackState()
        {
            switch (state)
            {
                case ExpState.EXP_START:
                {
                    ClearAllTargets();
                    ApplyViewMode(startMode);

                    if (debugLog)
                        Debug.Log("[TargetAppear] BackState: still EXP_START.");

                    break;
                }

                case ExpState.EXP_SHOW_TARGET:
                {
                    if (currentTrial == 0)
                    {
                        state = ExpState.EXP_START;
                        ClearAllTargets();
                        ApplyViewMode(startMode);

                        if (debugLog)
                            Debug.Log("[TargetAppear] BackState: to EXP_START.");
                    }
                    else
                    {
                        state = ExpState.EXP_RECORDED;
                        currentTrial = Mathf.Max(0, currentTrial - 1);

                        ApplyViewMode(showTargetMode);
                        ShowTargetForCurrentTrial();

                        if (debugLog)
                            Debug.Log("[TargetAppear] BackState: to EXP_RECORDED (previous trial).");
                    }

                    break;
                }

                case ExpState.EXP_WALK:
                {
                    state = ExpState.EXP_SHOW_TARGET;

                    if (resetEachTrial)
                        ResetToReference(GetPreferredResetReference());

                    ApplyViewMode(showTargetMode);
                    ShowTargetForCurrentTrial();

                    if (debugLog)
                        Debug.Log("[TargetAppear] BackState: to EXP_SHOW_TARGET.");

                    break;
                }

                case ExpState.EXP_RECORDED:
                {
                    state = ExpState.EXP_WALK;

                    if (hideTargetWhenWalking)
                        ClearAllTargets();

                    ApplyViewMode(walkMode);

                    if (debugLog)
                        Debug.Log("[TargetAppear] BackState: to EXP_WALK.");

                    break;
                }

                case ExpState.EXP_FINISHED:
                default:
                {
                    state = ExpState.EXP_SHOW_TARGET;
                    currentTrial = Mathf.Clamp(AllTrials.Length - 1, 0, AllTrials.Length - 1);

                    if (resetEachTrial)
                        ResetToReference(GetPreferredResetReference());

                    ApplyViewMode(showTargetMode);
                    ShowTargetForCurrentTrial();

                    if (debugLog)
                        Debug.Log("[TargetAppear] BackState: from FINISHED to EXP_SHOW_TARGET (last trial).");

                    break;
                }
            }
        }

        private void Update()
        {
            if (enableControllerInput)
                PollControllerInput();

            // Grip buttons always active regardless of enableControllerInput
            PollGripButtons();

            if (!enableDebugHotkeys)
                return;

            if (enableControllerInput)
            {
                // Debug hotkeys on controllers
                if (OVRInput.GetDown(OVRInput.Button.Three)) // X
                    ApplyViewMode(ViewMode.VirtualOnly);

                if (OVRInput.GetDown(OVRInput.Button.Four))  // Y
                    ApplyViewMode(ViewMode.Passthrough);

                if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.Touch)) // Left trigger
                    ApplyViewMode(ViewMode.Blank);
            }
        }

        private void PollGripButtons()
        {
            float lg = OVRInput.Get(OVRInput.RawAxis1D.LHandTrigger);
            float rg = OVRInput.Get(OVRInput.RawAxis1D.RHandTrigger);

            if (Time.frameCount % 60 == 0)
                Debug.Log("[TargetAppear] Grip axes — L:" + lg.ToString("F2") + " R:" + rg.ToString("F2") +
                          " dofDriver=" + (dofDriver != null ? dofDriver.name : "NULL"));

            bool leftGripDown  = lg > 0.5f;
            bool rightGripDown = rg > 0.5f;

            if (leftGripDown && !_leftGripWasDown)
            {
                if (dofDriver == null)
                    Debug.LogWarning("[TargetAppear] Left grip pressed but dofDriver is not assigned.");
                else
                {
                    int next = ((int)dofDriver.focusMode + 1) % 3;
                    dofDriver.focusMode = (GazeDrivenDepthOfFieldPPv2.FocusMode)next;
                    Debug.Log("[TargetAppear] Focus mode → " + dofDriver.focusMode);
                }
            }
            _leftGripWasDown = leftGripDown;

            if (rightGripDown && !_rightGripWasDown)
            {
                if (dofDriver == null)
                    Debug.LogWarning("[TargetAppear] Right grip pressed but dofDriver is not assigned.");
                else
                {
                    _strengthStepIndex = (_strengthStepIndex + 1) % _strengthSteps.Length;
                    float val = _strengthSteps[_strengthStepIndex];

                    if (dofDriver.focusMode == GazeDrivenDepthOfFieldPPv2.FocusMode.Monochrome)
                        dofDriver.monochromeBlurStrength = val;
                    else if (dofDriver.focusMode == GazeDrivenDepthOfFieldPPv2.FocusMode.Chromatic)
                        dofDriver.chromaticOverallStrength = val;

                    Debug.Log("[TargetAppear] Blur strength → " + val + " (mode: " + dofDriver.focusMode + ")");
                }
            }
            _rightGripWasDown = rightGripDown;
        }

        private void PollControllerInput()
        {
            bool aPressed = OVRInput.GetDown(OVRInput.Button.One);
            bool bPressed = OVRInput.GetDown(OVRInput.Button.Two);

            if (aPressed) AdvanceState();
            if (bPressed) BackState();

            bool rightTriggerDown = OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger, OVRInput.Controller.Touch);

            if (rightTriggerResetsToBeginPoint && rightTriggerDown)
            {
                ResetToReference(GetPreferredResetReference());

                if (triggerResetForcesShowTargetView)
                    ApplyViewMode(showTargetMode);

                if (triggerResetReturnsToShowTargetState)
                {
                    state = ExpState.EXP_SHOW_TARGET;
                    ShowTargetForCurrentTrial();
                }

                if (debugLog)
                    Debug.Log("[TargetAppear] Right trigger reset (position + yaw).");
            }

            // Right thumbstick: walk forward/back and strafe, oriented to the HMD look direction
            if (player != null)
            {
                Vector2 moveAxis = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
                if (moveAxis.sqrMagnitude > 0.01f)
                {
                    Camera head = playerHead != null ? playerHead : Camera.main;
                    Vector3 fwd   = head != null
                        ? Vector3.ProjectOnPlane(head.transform.forward, Vector3.up).normalized
                        : player.transform.forward;
                    Vector3 right = head != null
                        ? Vector3.ProjectOnPlane(head.transform.right, Vector3.up).normalized
                        : player.transform.right;

                    Vector3 move = (fwd * moveAxis.y + right * moveAxis.x) * playerMoveSpeed * Time.deltaTime;

                    var cc = player.GetComponent<CharacterController>();
                    if (cc != null && cc.enabled)
                        cc.Move(move);
                    else
                        player.transform.position += move;
                }
            }
        }

        private void InitCsvLog()
        {
            _csvPath = Path.Combine(Application.persistentDataPath, "trial_log.csv");

            if (!File.Exists(_csvPath))
            {
                File.WriteAllText(_csvPath, "trial_id,target_distance,focus_mode,response_time_s\n");

                if (debugLog)
                    Debug.Log("[TargetAppear] Created trial log: " + _csvPath);
            }
            else if (debugLog)
            {
                Debug.Log("[TargetAppear] Appending to existing trial log: " + _csvPath);
            }
        }

        private void LogTrialToCsv()
        {
            if (string.IsNullOrEmpty(_csvPath)) return;

            float responseTime = Time.time - _trialStartTime;
            float dist = CurrentTargetDistanceMeters;
            string modeName = (dofDriver != null) ? dofDriver.focusMode.ToString() : "Unknown";

            string row = string.Format("{0},{1:F2},{2},{3:F3}\n",
                currentTrial, dist, modeName, responseTime);

            try
            {
                File.AppendAllText(_csvPath, row);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TargetAppear] CSV write failed: " + e.Message);
            }
        }

        private void LogButtonMap()
        {
            Debug.Log(
                "[TargetAppear] Button Map (controllers):\n" +
                "A (Button.One): Advance state\n" +
                "B (Button.Two): Back state\n" +
                "Right Index Trigger: Reset to BeginPoint/resetTransform\n" +
                "Optional debug:\n" +
                "X: Force VirtualOnly\n" +
                "Y: Force Passthrough\n" +
                "Left Index Trigger: Force Blank"
            );
        }
    }
}










///// before hand tracking 1/18/2026
// // TargetAppear.cs
// // Updated to: (1) use Rigidbody.velocity (portable), (2) expose simple state helpers
// // Note: This file remains in OculusSampleFramework namespace like your original.

// using System;                       // StringComparison, Array
// using System.Collections.Generic;   // List<T>
// using System.Linq;                  // OrderBy, FirstOrDefault
// using UnityEngine;                  // Unity core

// namespace OculusSampleFramework
// {
//     /// <summary>
//     /// TargetAppear
//     ///
//     /// Responsibilities: 
//     /// - Experiment state machine (A advances, B goes back)
//     /// - Shows exactly one target per trial at a specified distance
//     /// - Ensures ONLY current target is raycastable (colliders enabled)
//     /// - Controls view mode (VirtualOnly, Passthrough, Blank) without disabling LabEnvironment root
//     ///
//     /// Extra:
//     /// - Publishes a TargetChangeStamp that increments each time we show a new target
//     ///   so other scripts can reset cleanly when trial changes.
//     /// </summary>
//     public class TargetAppear : MonoBehaviour
//     {
//         // ----------------------------
//         // Scene references
//         // ----------------------------

//         [Header("Scene References")]
//         [Tooltip("Root GameObject for your virtual environment (meshes and colliders). Do NOT disable this root.")]
//         public GameObject LabEnvironment; // Environment root

//         [Tooltip("Camera/GameObject used for the blank screen state (for walking).")]
//         public GameObject camBlank;       // Blank camera object

//         [Tooltip("Camera/GameObject used for the normal viewing state.")]
//         public GameObject NormalCam;      // Normal camera object

//         [Tooltip("OVR Passthrough Layer component. If null, we try GetComponent<OVRPassthroughLayer>().")]
//         public OVRPassthroughLayer passthroughLayer; // Passthrough layer reference

//         // ----------------------------
//         // Targets
//         // ----------------------------

//         [Header("Targets")]
//         [Tooltip("Your target objects (we toggle their Renderer and Colliders on/off and move them).")]
//         public GameObject[] BochaoTargets = new GameObject[5]; // Target pool

//         // Public outputs for gaze-contingent logic
//         public Transform CurrentTargetTransform { get; private set; }       // Current target transform
//         public float CurrentTargetDistanceMeters { get; private set; }      // Current trial distance
//         public bool IsTargetVisibleForGaze { get; private set; }            // Whether scripts should consider target visible

//         // Increments whenever we show a target for a trial
//         public int TargetChangeStamp { get; private set; } = 0;             // Change stamp counter

//         // Convenience flags (useful in your other scripts)
//         public bool IsInShowTargetState => state == ExpState.EXP_SHOW_TARGET; // True only while viewing the target
//         public bool HasCurrentTarget => CurrentTargetTransform != null;       // True if a target exists

//         // ----------------------------
//         // Player rig / reset
//         // ----------------------------

//         [Header("Player Rig / Reset")]
//         public Transform BeginPoint;            // Preferred reset point
//         public Transform resetTransform;        // Alternate reset point
//         public GameObject player;               // Rig root object
//         public Camera playerHead;               // HMD camera

//         public bool autoFindRigReferences = true; // Auto resolve rig refs
//         public bool resetXZOnly = false;          // Keep Y unchanged when resetting
//         public bool resetOnStart = true;          // Reset on Start()
//         public bool resetEachTrial = true;        // Reset every trial

//         // ----------------------------
//         // Right trigger behavior
//         // ----------------------------

//         [Header("Right Trigger Behavior")]
//         public bool rightTriggerResetsToBeginPoint = true;       // Right trigger does reset
//         public bool triggerResetForcesShowTargetView = true;     // Force show-target view mode after reset
//         public bool triggerResetReturnsToShowTargetState = false; // Optionally force EXP_SHOW_TARGET state after reset

//         // ----------------------------
//         // View modes
//         // ----------------------------

//         public enum ViewMode
//         {
//             VirtualOnly,   // Virtual environment only
//             Passthrough,   // Passthrough on (optionally show environment visuals)
//             Blank          // Blank camera active (walking)
//         }

//         [Header("View Mode per Experiment State")]
//         public ViewMode startMode = ViewMode.VirtualOnly;      // Mode for EXP_START
//         public ViewMode showTargetMode = ViewMode.VirtualOnly; // Mode for EXP_SHOW_TARGET
//         public ViewMode walkMode = ViewMode.Blank;             // Mode for EXP_WALK

//         [Header("Environment Visibility Policy")]
//         public bool keepEnvironmentCollidersEnabled = true;      // Keep colliders enabled for raycasts even if visuals hidden
//         public bool showEnvironmentVisualsInPassthrough = false; // Show env visuals during passthrough
//         public bool showEnvironmentVisualsInVirtual = true;      // Show env visuals during virtual-only
//         public bool showEnvironmentVisualsInBlank = false;       // Show env visuals during blank

//         // ----------------------------
//         // Trial design
//         // ----------------------------

//         [Header("Trial Design")]
//         public float[] AllTrials = new float[17];                 // Distances per trial
//         public int[] isPractice = new int[17];                    // Practice flags
//         public int[] generatedRandomTargetObjects = new int[17];  // Target index per trial
//         public int currentTrial = 0;                              // Current trial index

//         // Trial sets
//         private readonly List<float> Pre_List = new List<float> { 3.5f, 4.5f }; // Pre trials
//         private readonly List<float> lst = new List<float> { 2f, 2f, 2f, 2.5f, 3f, 3f, 3f, 3.5f, 4f, 4f, 4f, 4.5f, 5f, 5f, 5f }; // Main trials
//         private readonly List<int> Target_Objects_lst = new List<int> { 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4 };     // Which target to show each trial

//         // ----------------------------
//         // Experiment state machine
//         // ----------------------------

//         public enum ExpState
//         {
//             EXP_START = 0,       // Initial
//             EXP_SHOW_TARGET = 1, // Show target and let them look
//             EXP_WALK = 2,        // Blank while they walk
//             EXP_RECORDED = 3,    // Response recorded step
//             EXP_FINISHED = 4     // Done
//         }

//         [Header("Experiment State")]
//         public ExpState state = ExpState.EXP_START; // Current state

//         [Header("Target preview policy")]
//         public bool hideTargetWhenWalking = true;   // Hide target on walk

//         // ----------------------------
//         // Debug / utilities
//         // ----------------------------

//         [Header("Debug")]
//         public bool debugLog = true;          // Enable debug logs
//         public bool enableDebugHotkeys = true;// X/Y/LeftTrigger debug view toggles

//         [Header("Target Collider")]
//         public bool autoAddColliderToTargets = true; // Add a collider if target has none
//         public float minAutoColliderRadius = 0.05f;  // Minimum collider radius

//         private void Awake()
//         {
//             // If passthroughLayer is not assigned, try to get it on this object
//             if (passthroughLayer == null)
//                 passthroughLayer = GetComponent<OVRPassthroughLayer>();

//             // Warn if script sits under LabEnvironment
//             if (LabEnvironment != null && transform.IsChildOf(LabEnvironment.transform))
//             {
//                 Debug.LogWarning(
//                     "[TargetAppear] This script is under LabEnvironment. " +
//                     "We do not disable LabEnvironment root, so it can work, but it is cleaner to keep it outside."
//                 );
//             }
//         }

//         private void Start()
//         {
//             // Auto-find references if desired
//             if (autoFindRigReferences)
//                 AutoFindRigReferences();

//             // Build randomized trial arrays
//             BuildTrialsAndTargets();

//             // Start with no visible targets
//             ClearAllTargets();

//             // Reset player if requested
//             if (resetOnStart)
//                 ResetToReference(GetPreferredResetReference());

//             // Apply initial view mode
//             ApplyViewMode(startMode);

//             // Log help
//             if (debugLog)
//             {
//                 Debug.Log("[TargetAppear] Started. Press A to begin.");
//                 LogButtonMap();
//             }
//         }

//         private void BuildTrialsAndTargets()
//         {
//             // Shuffle pre trials
//             var preRand = new System.Random();                 // RNG for pre trials
//             var preShuffled = Pre_List.OrderBy(_ => preRand.Next()).ToArray(); // Shuffle list

//             // Shuffle main trials
//             var rand = new System.Random();                    // RNG for main trials
//             var shuffled = lst.OrderBy(_ => rand.Next()).ToArray(); // Shuffle list

//             // Shuffle target mapping
//             var targetRand = new System.Random();              // RNG for target mapping
//             generatedRandomTargetObjects = Target_Objects_lst.OrderBy(_ => targetRand.Next()).ToArray(); // Shuffle mapping

//             // Copy into AllTrials
//             Array.Copy(preShuffled, 0, AllTrials, 0, preShuffled.Length); // Copy pre
//             Array.Copy(shuffled, 0, AllTrials, preShuffled.Length, shuffled.Length); // Copy main

//             // Mark practice trials
//             for (int i = 0; i < AllTrials.Length; i++)
//             {
//                 float d = AllTrials[i]; // Trial distance
//                 isPractice[i] =
//                     (Mathf.Approximately(d, 2.5f) || Mathf.Approximately(d, 3.5f) || Mathf.Approximately(d, 4.5f))
//                     ? 1 : 0; // Practice marker
//             }
//         }

//         private void ApplyViewMode(ViewMode mode)
//         {
//             // Enable/disable camera objects
//             if (NormalCam != null)
//                 NormalCam.SetActive(mode != ViewMode.Blank); // Normal camera off only during Blank

//             if (camBlank != null)
//                 camBlank.SetActive(mode == ViewMode.Blank);  // Blank camera on only during Blank

//             // Enable/disable passthrough
//             bool passthroughOn = (mode == ViewMode.Passthrough); // Determine passthrough state
//             if (passthroughLayer != null)
//                 passthroughLayer.enabled = passthroughOn;        // Apply

//             // Decide visuals
//             bool wantVisuals =
//                 (mode == ViewMode.Passthrough && showEnvironmentVisualsInPassthrough) ||
//                 (mode == ViewMode.VirtualOnly && showEnvironmentVisualsInVirtual) ||
//                 (mode == ViewMode.Blank && showEnvironmentVisualsInBlank); // Visual policy

//             // Decide colliders
//             bool wantColliders = keepEnvironmentCollidersEnabled; // Collider policy

//             // Apply environment visibility toggles
//             SetEnvironmentRenderersVisible(wantVisuals);          // Renderers
//             SetEnvironmentCollidersEnabled(wantColliders);        // Colliders

//             // Debug log
//             if (debugLog)
//             {
//                 Debug.Log(
//                     "[TargetAppear] ApplyViewMode: " + mode +
//                     " passthrough=" + (passthroughOn ? "on" : "off") +
//                     " envVisuals=" + (wantVisuals ? "on" : "off") +
//                     " envColliders=" + (wantColliders ? "on" : "off")
//                 );
//             }
//         }

//         private void SetEnvironmentRenderersVisible(bool visible)
//         {
//             if (LabEnvironment == null) return; // Guard

//             var renderers = LabEnvironment.GetComponentsInChildren<Renderer>(true); // Get all renderers
//             for (int i = 0; i < renderers.Length; i++)
//                 renderers[i].enabled = visible; // Toggle
//         }

//         private void SetEnvironmentCollidersEnabled(bool enabled)
//         {
//             if (LabEnvironment == null) return; // Guard

//             var cols = LabEnvironment.GetComponentsInChildren<Collider>(true); // Get all colliders
//             for (int i = 0; i < cols.Length; i++)
//                 cols[i].enabled = enabled; // Toggle
//         }

//         public bool IsColliderFromCurrentTarget(Collider col)
//         {
//             if (col == null) return false;              // Guard
//             if (CurrentTargetTransform == null) return false; // Guard

//             Transform t = col.transform;                // Collider transform
//             return (t == CurrentTargetTransform) || t.IsChildOf(CurrentTargetTransform); // Belongs to target?
//         }

//         private void ClearAllTargets()
//         {
//             // Reset public state
//             CurrentTargetTransform = null;      // Clear transform
//             CurrentTargetDistanceMeters = 0f;   // Clear distance
//             IsTargetVisibleForGaze = false;     // Mark invisible

//             // Disable all targets
//             for (int i = 0; i < BochaoTargets.Length; i++)
//             {
//                 GameObject t = BochaoTargets[i]; // Candidate target
//                 if (t == null) continue;         // Skip null
//                 SetTargetVisualAndColliderState(t, visualsEnabled: false, collidersEnabled: false); // Disable
//             }
//         }

//         private void ShowTargetForCurrentTrial()
//         {
//             ClearAllTargets(); // Clear previous target

//             if (currentTrial < 0 || currentTrial >= AllTrials.Length)
//                 return; // Out of range

//             int safeTrialIndex = Mathf.Clamp(currentTrial, 0, generatedRandomTargetObjects.Length - 1); // Safe index
//             int targetIndex = generatedRandomTargetObjects[safeTrialIndex]; // Target index for this trial

//             if (targetIndex < 0 || targetIndex >= BochaoTargets.Length)
//                 return; // Invalid target

//             GameObject target = BochaoTargets[targetIndex]; // Target object
//             if (target == null)
//                 return; // Null target

//             float d = AllTrials[currentTrial]; // Distance for this trial

//             target.transform.localPosition = new Vector3(0f, 0f, d); // Place target at +Z

//             if (autoAddColliderToTargets)
//                 EnsureTargetHasCollider(target); // Ensure target is raycastable

//             SetTargetVisualAndColliderState(target, visualsEnabled: true, collidersEnabled: true); // Enable only current target

//             CurrentTargetTransform = target.transform;   // Publish current target
//             CurrentTargetDistanceMeters = d;             // Publish distance
//             IsTargetVisibleForGaze = true;               // Allow gaze logic

//             TargetChangeStamp++;                         // Increment stamp for other scripts

//             if (debugLog)
//             {
//                 Debug.Log(
//                     "[TargetAppear] ShowTarget trial=" + currentTrial +
//                     " dist=" + d.ToString("F2") + "m targetIndex=" + targetIndex +
//                     " practice=" + isPractice[currentTrial] +
//                     " stamp=" + TargetChangeStamp
//                 );
//             }
//         }

//         private void SetTargetVisualAndColliderState(GameObject target, bool visualsEnabled, bool collidersEnabled)
//         {
//             if (target == null) return; // Guard

//             var rends = target.GetComponentsInChildren<Renderer>(true); // Renderers
//             for (int i = 0; i < rends.Length; i++)
//                 rends[i].enabled = visualsEnabled; // Toggle renderer

//             var cols = target.GetComponentsInChildren<Collider>(true); // Colliders
//             for (int i = 0; i < cols.Length; i++)
//                 cols[i].enabled = collidersEnabled; // Toggle collider
//         }

//         private void EnsureTargetHasCollider(GameObject target)
//         {
//             if (target == null) return; // Guard

//             if (target.GetComponentInChildren<Collider>(true) != null)
//                 return; // Already has a collider

//             var rend = target.GetComponentInChildren<Renderer>(true); // Renderer for bounds
//             if (rend != null)
//             {
//                 Bounds b = rend.bounds; // World bounds

//                 Vector3 localCenter = target.transform.InverseTransformPoint(b.center); // Convert center to local
//                 float worldRadius = b.extents.magnitude; // Approx radius in world

//                 Vector3 s = target.transform.lossyScale; // Scale
//                 float maxScale = Mathf.Max(1e-6f, Mathf.Max(s.x, Mathf.Max(s.y, s.z))); // Prevent divide by zero
//                 float localRadius = Mathf.Max(minAutoColliderRadius, worldRadius / maxScale); // Convert to local radius

//                 var sc = target.AddComponent<SphereCollider>(); // Add sphere
//                 sc.center = localCenter; // Set center
//                 sc.radius = localRadius; // Set radius

//                 if (debugLog)
//                     Debug.Log("[TargetAppear] Added SphereCollider to target: " + target.name + " radius(local)=" + localRadius.ToString("F3"));

//                 return; // Done
//             }

//             var scFallback = target.AddComponent<SphereCollider>(); // Fallback sphere
//             scFallback.center = Vector3.zero; // Default center
//             scFallback.radius = Mathf.Max(minAutoColliderRadius, 0.1f); // Default radius

//             if (debugLog)
//                 Debug.Log("[TargetAppear] Added fallback SphereCollider to target: " + target.name);
//         }

//         private Transform GetPreferredResetReference()
//         {
//             if (BeginPoint != null) return BeginPoint; // Prefer BeginPoint
//             return resetTransform;                      // Otherwise use resetTransform
//         }

//         public void ResetToReference(Transform reference)
//         {
//             if (reference == null)
//             {
//                 Debug.LogWarning("[TargetAppear] ResetToReference failed: reference is null. Assign BeginPoint or resetTransform.");
//                 return; // Must have a reset reference
//             }

//             if (playerHead == null && Camera.main != null)
//                 playerHead = Camera.main; // Try auto-assign head

//             if (player == null || playerHead == null)
//             {
//                 Debug.LogWarning("[TargetAppear] ResetToReference failed: assign player (rig root) and playerHead (HMD camera).");
//                 return; // Must have rig root and head
//             }

//             Transform playerRoot = player.transform;     // Rig root transform
//             Transform headT = playerHead.transform;      // Head transform

//             var cc = player.GetComponent<CharacterController>(); // CharacterController
//             var ovrpc = player.GetComponent<OVRPlayerController>(); // OVRPlayerController

//             if (ovrpc != null) ovrpc.enabled = false; // Disable movement
//             if (cc != null) cc.enabled = false;       // Disable physics controller

//             var rb = player.GetComponent<Rigidbody>(); // Optional rigidbody
//             if (rb != null)
//             {
//                 rb.linearVelocity = Vector3.zero;       // Stop linear motion (portable)
//                 rb.angularVelocity = Vector3.zero; // Stop angular motion
//             }

//             float targetYaw = reference.eulerAngles.y; // Desired yaw
//             float headYaw = headT.eulerAngles.y;       // Current head yaw
//             float deltaYaw = Mathf.DeltaAngle(headYaw, targetYaw); // Yaw correction

//             playerRoot.RotateAround(headT.position, Vector3.up, deltaYaw); // Rotate around head so head stays put

//             Vector3 headPosAfterRot = headT.position;  // Head after rotation
//             Vector3 targetHeadPos = reference.position;// Target head position

//             Vector3 translation; // Translation vector

//             if (resetXZOnly)
//             {
//                 translation = new Vector3(
//                     targetHeadPos.x - headPosAfterRot.x,
//                     0f,
//                     targetHeadPos.z - headPosAfterRot.z
//                 ); // Only shift XZ
//             }
//             else
//             {
//                 translation = targetHeadPos - headPosAfterRot; // Shift XYZ
//             }

//             playerRoot.position += translation; // Apply translation

//             if (cc != null) cc.enabled = true;       // Re-enable CC
//             if (ovrpc != null) ovrpc.enabled = true; // Re-enable OVRPC

//             if (debugLog)
//             {
//                 Debug.Log(
//                     "[TargetAppear] ResetToReference done ref=" + reference.name +
//                     " resetXZOnly=" + resetXZOnly +
//                     " deltaYaw=" + deltaYaw.ToString("F2")
//                 );
//             }
//         }

//         private void AutoFindRigReferences()
//         {
//             // if (playerHead == null)
//             // {
//             //     if (Camera.main != null)
//             //         playerHead = Camera.main; // Prefer Camera.main
//             //     else
//             //     {
//             //         Camera anyCam = FindObjectsOfType<Camera>(true).FirstOrDefault(c => c.enabled); // Any enabled camera
//             //         if (anyCam != null) playerHead = anyCam; // Assign
//             //     }
//             // }

//             if (player == null && playerHead != null)
//             {
//                 Transform candidate = playerHead.transform; // Start from head

//                 for (int i = 0; i < 10 && candidate != null; i++)
//                 {
//                     if (candidate.name.IndexOf("OVRCameraRig", StringComparison.OrdinalIgnoreCase) >= 0)
//                     {
//                         player = candidate.gameObject; // Found rig
//                         break;
//                     }
//                     candidate = candidate.parent; // Climb
//                 }

//                 if (player == null)
//                 {
//                     Transform root = playerHead.transform; // Start from head
//                     while (root.parent != null)
//                         root = root.parent; // Climb to scene root

//                     if (LabEnvironment == null || root.gameObject != LabEnvironment)
//                         player = root.gameObject; // Fallback root
//                 }
//             }

//             if (debugLog)
//             {
//                 Debug.Log(
//                     "[TargetAppear] AutoFindRigReferences player=" + (player ? player.name : "null") +
//                     " playerHead=" + (playerHead ? playerHead.name : "null")
//                 );
//             }
//         }

//         private void AdvanceState()
//         {
//             switch (state)
//             {
//                 case ExpState.EXP_START:
//                 {
//                     state = ExpState.EXP_SHOW_TARGET; // Go to show
//                     currentTrial = 0;                 // Start trial 0

//                     if (resetEachTrial)
//                         ResetToReference(GetPreferredResetReference()); // Reset rig

//                     ApplyViewMode(showTargetMode);  // Show mode
//                     ShowTargetForCurrentTrial();    // Show target

//                     if (debugLog)
//                         Debug.Log("[TargetAppear] State EXP_SHOW_TARGET. Press A to start walking.");

//                     break;
//                 }

//                 case ExpState.EXP_SHOW_TARGET:
//                 {
//                     state = ExpState.EXP_WALK; // Go to walk

//                     if (hideTargetWhenWalking)
//                         ClearAllTargets(); // Hide target

//                     ApplyViewMode(walkMode); // Blank mode

//                     if (debugLog)
//                         Debug.Log("[TargetAppear] State EXP_WALK. Press A when you reached the target.");

//                     break;
//                 }

//                 case ExpState.EXP_WALK:
//                 {
//                     state = ExpState.EXP_RECORDED; // Go to recorded

//                     if (debugLog)
//                         Debug.Log("[TargetAppear] State EXP_RECORDED. Record response now. Press A for next trial.");

//                     break;
//                 }

//                 case ExpState.EXP_RECORDED:
//                 {
//                     if (currentTrial + 1 >= AllTrials.Length)
//                     {
//                         state = ExpState.EXP_FINISHED; // Finish

//                         ClearAllTargets();             // Clear
//                         ApplyViewMode(showTargetMode); // Back to show mode

//                         if (debugLog)
//                             Debug.Log("[TargetAppear] State EXP_FINISHED. Done.");
//                     }
//                     else
//                     {
//                         state = ExpState.EXP_SHOW_TARGET; // Next trial show
//                         currentTrial++;                   // Advance trial

//                         if (resetEachTrial)
//                             ResetToReference(GetPreferredResetReference()); // Reset rig

//                         ApplyViewMode(showTargetMode);   // Show mode
//                         ShowTargetForCurrentTrial();     // Show target

//                         if (debugLog)
//                             Debug.Log("[TargetAppear] Next trial, back to EXP_SHOW_TARGET.");
//                     }

//                     break;
//                 }

//                 case ExpState.EXP_FINISHED:
//                 default:
//                 {
//                     if (debugLog)
//                         Debug.Log("[TargetAppear] Already finished.");
//                     break;
//                 }
//             }
//         }

//         private void BackState()
//         {
//             switch (state)
//             {
//                 case ExpState.EXP_START:
//                 {
//                     ClearAllTargets();     // Clear
//                     ApplyViewMode(startMode); // Start mode

//                     if (debugLog)
//                         Debug.Log("[TargetAppear] BackState: still EXP_START.");

//                     break;
//                 }

//                 case ExpState.EXP_SHOW_TARGET:
//                 {
//                     if (currentTrial == 0)
//                     {
//                         state = ExpState.EXP_START; // Back to start
//                         ClearAllTargets();          // Clear
//                         ApplyViewMode(startMode);   // Start mode

//                         if (debugLog)
//                             Debug.Log("[TargetAppear] BackState: to EXP_START.");
//                     }
//                     else
//                     {
//                         state = ExpState.EXP_RECORDED; // Back to recorded
//                         currentTrial = Mathf.Max(0, currentTrial - 1); // Previous trial

//                         ApplyViewMode(showTargetMode); // Show mode
//                         ShowTargetForCurrentTrial();   // Show previous target

//                         if (debugLog)
//                             Debug.Log("[TargetAppear] BackState: to EXP_RECORDED (previous trial).");
//                     }

//                     break;
//                 }

//                 case ExpState.EXP_WALK:
//                 {
//                     state = ExpState.EXP_SHOW_TARGET; // Back to show

//                     if (resetEachTrial)
//                         ResetToReference(GetPreferredResetReference()); // Reset

//                     ApplyViewMode(showTargetMode); // Show mode
//                     ShowTargetForCurrentTrial();   // Show target

//                     if (debugLog)
//                         Debug.Log("[TargetAppear] BackState: to EXP_SHOW_TARGET.");

//                     break;
//                 }

//                 case ExpState.EXP_RECORDED:
//                 {
//                     state = ExpState.EXP_WALK; // Back to walk

//                     if (hideTargetWhenWalking)
//                         ClearAllTargets(); // Hide target

//                     ApplyViewMode(walkMode); // Blank

//                     if (debugLog)
//                         Debug.Log("[TargetAppear] BackState: to EXP_WALK.");

//                     break;
//                 }

//                 case ExpState.EXP_FINISHED:
//                 default:
//                 {
//                     state = ExpState.EXP_SHOW_TARGET; // Back to show
//                     currentTrial = Mathf.Clamp(AllTrials.Length - 1, 0, AllTrials.Length - 1); // Last trial

//                     if (resetEachTrial)
//                         ResetToReference(GetPreferredResetReference()); // Reset

//                     ApplyViewMode(showTargetMode); // Show mode
//                     ShowTargetForCurrentTrial();   // Show last target

//                     if (debugLog)
//                         Debug.Log("[TargetAppear] BackState: from FINISHED to EXP_SHOW_TARGET (last trial).");

//                     break;
//                 }
//             }
//         }

//         private void Update()
//         {
//             bool aPressed = OVRInput.GetDown(OVRInput.Button.One); // A button
//             bool bPressed = OVRInput.GetDown(OVRInput.Button.Two); // B button

//             if (aPressed) AdvanceState(); // Advance
//             if (bPressed) BackState();    // Back

//             bool rightTriggerDown = OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger, OVRInput.Controller.Touch); // Right trigger

//             if (rightTriggerResetsToBeginPoint && rightTriggerDown)
//             {
//                 ResetToReference(GetPreferredResetReference()); // Reset rig

//                 if (triggerResetForcesShowTargetView)
//                     ApplyViewMode(showTargetMode); // Force show mode

//                 if (triggerResetReturnsToShowTargetState)
//                 {
//                     state = ExpState.EXP_SHOW_TARGET; // Force show state
//                     ShowTargetForCurrentTrial();       // Re-show current target
//                 }

//                 if (debugLog)
//                     Debug.Log("[TargetAppear] Right trigger reset (position + yaw).");
//             }

//             if (!enableDebugHotkeys)
//                 return; // Stop here if debug hotkeys are off

//             if (OVRInput.GetDown(OVRInput.Button.Three)) // X
//                 ApplyViewMode(ViewMode.VirtualOnly);     // Force virtual only

//             if (OVRInput.GetDown(OVRInput.Button.Four))  // Y
//                 ApplyViewMode(ViewMode.Passthrough);     // Force passthrough

//             if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.Touch)) // Left trigger
//                 ApplyViewMode(ViewMode.Blank);           // Force blank
//         }

//         private void LogButtonMap()
//         {
//             Debug.Log(
//                 "[TargetAppear] Button Map:\n" +
//                 "A (Button.One): Advance state\n" +
//                 "B (Button.Two): Back state\n" +
//                 "Right Index Trigger (SecondaryIndexTrigger): Reset to BeginPoint/resetTransform\n" +
//                 "Optional debug:\n" +
//                 "X (Button.Three): Force VirtualOnly\n" +
//                 "Y (Button.Four): Force Passthrough\n" +
//                 "Left Index Trigger (PrimaryIndexTrigger): Force Blank"
//             );
//         }
//     }
// }

