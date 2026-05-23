
using UnityEngine;                                                      // Unity core types
using OculusSampleFramework;                                            // TargetAppear lives here (OVR sample)

// <summary>                                                            // XML doc start
// Post-process blur that engages only when gaze is on the current target. // High-level behavior
// It drives blur and fovea size from diopter mismatch if varifocal focus is provided. // Varifocal-aware
// It also supports a distance-based fallback bias if varifocal focus is not provided. // Fallback behavior
// New in this version: Near Distance Boost increases blur at closer target distances. // New feature
// Minimal update: Disengage now fades out smoothly over disengageFadeSeconds. // New feature
// Minimal update: Removed early-exit threshold that caused texture switching pop. // New feature
// </summary>                                                           // XML doc end
[DisallowMultipleComponent]                                             // Prevent adding this script twice
[RequireComponent(typeof(Camera))]                                      // Requires a Camera to run OnRenderImage
public class GazeDrivenDepthOfFieldPPv2 : MonoBehaviour                  // Main MonoBehaviour
{
    // ------------------------------------------------------------------ // Section divider
    // References                                                         // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("References")]                                               // Inspector header
    [Tooltip("Your working gaze ray + hit script.")]                     // Inspector tooltip
    public GazeFixationDepthRaycast gazeSource;                          // Provides gaze ray, hit, fixation depth

    [Tooltip("Your working experiment controller (TargetAppear).")]      // Inspector tooltip
    public TargetAppear targetAppear;                                    // Provides trial state, target transform, etc.

    [Header("Shader")]                                                   // Inspector header
    [Tooltip("Assign: Hidden/GazeDepthAwareFoveatedBlur")]               // Inspector tooltip
    public Shader blurShader;                                            // Post-effect shader reference

    // ------------------------------------------------------------------ // Section divider
    // On-target Detection                                                // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("Engagement Trigger")]                                       // Inspector header
    [Tooltip("If true, gaze must hit the CURRENT target collider. If false, the soft sustain radius can also acquire blur.")] // Tooltip
    public bool requireHitTargetCollider = true;                         // Strict mode toggle

    [Tooltip("If gaze ray passes within this radius (m) of target center, blur sustains to reduce flicker.")] // Tooltip
    [Range(0.01f, 1.0f)]                                                 // Clamp in inspector
    public float sustainRadiusMeters = 0.45f;                            // Sustain radius in meters

    [Tooltip("Allowed depth error (m) between gaze hit/projection and target distance.")] // Tooltip
    [Range(0.01f, 2.0f)]                                                 // Clamp in inspector
    public float targetDepthToleranceMeters = 0.50f;                     // Depth tolerance in meters

    // ------------------------------------------------------------------ // Section divider
    // Engagement Smoothing (Attack and Decay)                             // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("Engagement Smoothing")]                                     // Inspector header
    [Tooltip("Time (s) to ramp blur on. Higher = softer start.")]        // Tooltip
    [Range(0.1f, 2.0f)]                                                  // Clamp in inspector
    public float smoothTimeAttack = 0.70f;                               // Attack smoothing

    [Tooltip("Time (s) to fade blur out. Higher = slower release.")]     // Tooltip
    [Range(0.1f, 3.0f)]                                                  // Clamp in inspector
    public float smoothTimeDecay = 2.50f;                                // Decay smoothing

    [Tooltip("Microsaccade protection: hold blur for this long before starting to fade.")] // Tooltip
    [Range(0f, 1.0f)]                                                    // Clamp in inspector
    public float disengageGraceSeconds = 0.45f;                          // Grace time

    [Tooltip("After grace ends, fade from blur to clear over this time (s). Larger = slower clearing, less pop.")] // Tooltip
    [Range(0.10f, 5.0f)]                                                 // Clamp in inspector
    public float disengageFadeSeconds = 1.50f;                           // NEW: fade duration after grace

    [Tooltip("If true, blur can only engage when REAL eye gaze is used this frame.")] // Tooltip
    public bool requireEyeGazeForEngagement = true;                      // Eye gaze validity gate

    [Tooltip("If true, focus distance uses the trial target distance (not gaze depth).")] // Tooltip
    public bool snapFocusToTargetDistance = true;                        // Use target distance for focus ref

    // ------------------------------------------------------------------ // Section divider
    // Base Foveation Defaults                                             // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("Foveation (Degrees) - Reset Defaults")]                     // Inspector header
    [Range(0.1f, 30f)]                                                   // Clamp in inspector
    public float foveaInnerDeg = 9.0f;                                   // Inner fovea default

    [Range(0.5f, 60f)]                                                   // Clamp in inspector
    public float foveaOuterDeg = 14.0f;                                  // Outer fovea default

    // ------------------------------------------------------------------ // Section divider
    // Base Blur Appearance Defaults                                       // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("Blur Appearance - Reset Defaults")]                         // Inspector header
    [Range(0f, 1f)]                                                      // Clamp in inspector
    public float basePeripheryBlur = 0.35f;                              // Baseline periphery blur

    [Range(0f, 2f)]                                                      // Clamp in inspector
    public float depthBlurWeight = 0.85f;                                // How much blur follows depth

    [Range(0.05f, 2.0f)]                                                 // Clamp in inspector
    public float defocusDioptersAtMaxBlur = 0.65f;                       // Defocus scale for shader

    [Range(0f, 3f)]                                                      // Clamp in inspector
    public float blurStrength = 1.10f;                                   // Master blur strength

    // ------------------------------------------------------------------ // Section divider
    // Dynamic Tuning (Varifocal aware)                                    // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("Varifocal Input")]                                          // Inspector header
    [Tooltip("Optional: current optical focus distance (m). If <= 0, mismatch uses fallback bias.")] // Tooltip
    public float varifocalOpticalFocusMeters = -1f;                      // Set from varifocal controller each frame

    [Tooltip("If true, drive dynamic response by diopter mismatch when varifocalOpticalFocusMeters > 0.")] // Tooltip
    public bool driveByVarifocalMismatch = true;                         // Enable varifocal mismatch driving

    [Tooltip("Mismatch (diopters) that maps to full mismatch response. Lower = ramps sooner.")] // Tooltip
    [Range(0.1f, 3f)]                                                    // Clamp in inspector
    public float maxMismatchDiopters = 0.50f;                            // Normalization for mismatch in diopters

    [Tooltip("If varifocal focus is not provided, apply distance-based mismatch bias. 0 = none, 1 = old dist01 behavior.")] // Tooltip
    [Range(0f, 1f)]                                                      // Clamp in inspector
    public float fallbackDistanceMismatchBias = 0.40f;                   // Partial far bias if no varifocal input

    [Header("Dynamic Distance References")]                              // Inspector header
    [Tooltip("Distance considered very near (m).")]                      // Tooltip
    public float nearDistanceRef = 0.40f;                                // Near distance reference

    [Tooltip("Distance considered very far (m).")]                       // Tooltip
    public float farDistanceRef = 8.00f;                                 // Far distance reference

    [Header("Dynamic Fovea Window (Degrees)")]                           // Inspector header
    [Tooltip("Inner deg when near (bigger reduces tunnel at close).")]   // Tooltip
    public float innerDegNear = 14f;                                     // Inner near degrees

    [Tooltip("Outer deg when near.")]                                    // Tooltip
    public float outerDegNear = 26f;                                     // Outer near degrees

    [Tooltip("Inner deg when far.")]                                     // Tooltip
    public float innerDegFar = 6.5f;                                     // Inner far degrees

    [Tooltip("Outer deg when far.")]                                     // Tooltip
    public float outerDegFar = 13.5f;                                    // Outer far degrees

    [Tooltip("Minimum gap between outer and inner (deg). Prevents harsh ring transition.")] // Tooltip
    [Range(0.5f, 20f)]                                                   // Clamp in inspector
    public float minFoveaBandDeg = 5f;                                   // Minimum band width

    [Header("Dynamic Blur Multipliers (Comfort First)")]                 // Inspector header
    [Tooltip("Strength multiplier when matched (low mismatch). Lower reduces tunnel.")] // Tooltip
    public float strengthMultWhenMatched = 0.55f;                        // Blur strength multiplier when matched

    [Tooltip("Strength multiplier when mismatched (high mismatch). Higher adds blur cues.")] // Tooltip
    public float strengthMultWhenMismatched = 1.65f;                     // Blur strength multiplier when mismatched

    [Tooltip("Base blur multiplier when matched.")]                      // Tooltip
    public float baseBlurMultMatched = 0.75f;                            // Base blur multiplier when matched

    [Tooltip("Base blur multiplier when mismatched.")]                   // Tooltip
    public float baseBlurMultMismatched = 1.25f;                         // Base blur multiplier when mismatched

    // ------------------------------------------------------------------ // Section divider
    // Near Distance Boost (Your request)                                  // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("Near Distance Boost (Make close targets blur more)")]       // Inspector header
    [Tooltip("If true, increase blur for closer target distances while keeping far behavior mostly unchanged.")] // Tooltip
    public bool enableNearDistanceBoost = true;                          // Master toggle for near boost

    [Tooltip("Extra multiplier applied to strength when very near (after other logic). 1 = no change.")] // Tooltip
    [Range(1.0f, 3.0f)]                                                  // Clamp in inspector
    public float nearStrengthBoostAtMinDistance = 1.35f;                 // Strength boost at near

    [Tooltip("Extra multiplier applied to base blur when very near (after other logic). 1 = no change.")] // Tooltip
    [Range(1.0f, 3.0f)]                                                  // Clamp in inspector
    public float nearBaseBlurBoostAtMinDistance = 1.25f;                 // Base blur boost at near

    [Tooltip("Curve power for near boost. Higher makes the boost concentrate more at very near.")] // Tooltip
    [Range(0.25f, 4.0f)]                                                 // Clamp in inspector
    public float nearBoostPower = 1.6f;                                  // Near boost curve exponent

    [Tooltip("If true, near boost applies mostly when clarity is high (matched). This preserves your far look.")] // Tooltip
    public bool nearBoostPreferMatched = true;                           // Prefer applying boost when matched

    // ------------------------------------------------------------------ // Section divider
    // Dynamic Visual Param Smoothing                                      // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("Dynamic Visual Param Smoothing")]                           // Inspector header
    [Tooltip("Smoothing time (s) for inner/outer/strength changes. Separate from engage smoothing.")] // Tooltip
    [Range(0.05f, 1.5f)]                                                 // Clamp in inspector
    public float visualParamSmoothTime = 0.20f;                          // Smoothing time for visual params

    // ------------------------------------------------------------------ // Section divider
    // Quality and Debug                                                   // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("Blur Quality")]                                             // Inspector header
    [Range(1, 10)]                                                       // Clamp in inspector
    public int maxMip = 4;                                               // Max mip level used by shader

    [Range(1, 4)]                                                        // Clamp in inspector
    public int downsampleBlurTexture = 2;                                // Downsample factor for blur RT

    [Tooltip("Use multi-tap shader blur instead of sampling generated mip levels.")]
    public bool useDirectBlurFallback = false;

    [Range(1f, 32f)]
    public float directBlurRadiusPixels = 9f;

    [Header("Red Dot Marker")]                                           // Inspector header
    public bool drawRedDotWhenOnTarget = true;                           // Draw dot when strictly on target

    [Range(0.001f, 0.02f)]                                               // Clamp in inspector
    public float dotRadiusUV = 0.004f;                                   // Dot radius in UV

    [Header("Debug")]                                                    // Inspector header
    public bool debugLog = false;                                        // Print debug info periodically

    // ------------------------------------------------------------------ // Section divider
    // Focus Mode                                                          // Section label
    // ------------------------------------------------------------------ // Section divider

    public enum FocusMode { Off, Monochrome, Chromatic }                  // Selectable rendering mode

    [Header("Focus Mode")]                                                // Inspector header
    [Tooltip("Off = pass-through. Monochrome = existing blur. Chromatic = LCA simulation.")]
    public FocusMode focusMode = FocusMode.Monochrome;                    // Default preserves current behaviour

    [Tooltip("Blur strength multiplier applied only in Monochrome mode. Increase to make the uniform blur more visible.")]
    [Range(0f, 4f)]
    public float monochromeBlurStrength = 1.0f;

    [Tooltip("Overall blend strength multiplier applied only in Chromatic mode. Controls how strongly the chromatic blur is blended over the sharp image (independent of per-channel colour fringing).")]
    [Range(0f, 4f)]
    public float chromaticOverallStrength = 1.0f;

    // ------------------------------------------------------------------ // Section divider
    // Chromatic Aberration (Thibos LCA Model)                            // Section label
    // ------------------------------------------------------------------ // Section divider

    [Header("Chromatic Aberration (Thibos LCA)")]                        // Inspector header
    [Tooltip("Dioptric offset added to defocus for the RED channel (Thibos: -0.4 D). Negative = red focuses behind retina.")]
    [SerializeField] float chromaticOffsetR = -0.4f;

    [Tooltip("Dioptric offset added to defocus for the GREEN channel (reference wavelength ~550 nm, 0 D).")]
    [SerializeField] float chromaticOffsetG = 0.0f;

    [Tooltip("Dioptric offset added to defocus for the BLUE channel (Thibos: +1.0 D). Positive = blue focuses in front of retina.")]
    [SerializeField] float chromaticOffsetB = 1.0f;

    [Tooltip("Scales |effective_defocus_diopters| into a mip level. Higher = more colour blur per diopter.")]
    [Range(0f, 4f)]
    [SerializeField] float chromaticBlurStrength = 1.0f;

    [Tooltip("Chromatic blur weight at the foveal centre (0 = no chroma at centre, 1 = full). Blends to 1.0 in the periphery.")]
    [Range(0f, 1f)]
    [SerializeField] float chromaticFovealWeight = 0.5f;

    [Tooltip("Upper mip clamp for per-channel chromatic samples.")]
    [Range(1, 12)]
    [SerializeField] int maxChromaticMip = 6;

    // ------------------------------------------------------------------ // Section divider
    // Internal State                                                      // Section label
    // ------------------------------------------------------------------ // Section divider

    private Camera _cam;                                                 // Cached camera
    private Material _mat;                                               // Material instance using blurShader
    private RenderTexture _blurMipRT;                                    // RT that stores mip chain for blur

    private static readonly float[] _strengthSteps = { 0.5f, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 3.5f, 4.0f };
    private int _strengthStepIndex = 1;
    private bool _leftGripWasDown = false;
    private bool _rightGripWasDown = false;

    private float _currentEngageLevel = 0f;                              // Smoothed engagement 0..1
    private float _engageVelocity = 0f;                                  // SmoothDamp velocity for engagement
    private float _graceTimer = 0f;                                      // Grace timer for disengage (now also drives fade stage)
    private int _lastTargetStamp = -1;                                   // Used to detect new trial

    private float _rtInnerDeg;                                           // Runtime inner degrees (smoothed)
    private float _rtOuterDeg;                                           // Runtime outer degrees (smoothed)
    private float _rtStrengthMult;                                       // Runtime strength multiplier (smoothed)
    private float _rtBaseBlurMult;                                       // Runtime base multiplier (smoothed)

    private float _velInnerDeg;                                          // SmoothDamp velocity for inner degrees
    private float _velOuterDeg;                                          // SmoothDamp velocity for outer degrees
    private float _velStrengthMult;                                      // SmoothDamp velocity for strength multiplier
    private float _velBaseBlurMult;                                      // SmoothDamp velocity for base multiplier

    // ------------------------------------------------------------------ // Section divider
    // Unity Lifecycle                                                     // Section label
    // ------------------------------------------------------------------ // Section divider

    private void Update()
    {
        float lg = OVRInput.Get(OVRInput.RawAxis1D.LHandTrigger);
        float rg = OVRInput.Get(OVRInput.RawAxis1D.RHandTrigger);

        bool leftGripDown  = lg > 0.5f;
        bool rightGripDown = rg > 0.5f;

        if (Time.frameCount % 60 == 0)
            Debug.Log("[DOFDriver] Grip L:" + lg.ToString("F2") + " R:" + rg.ToString("F2") + " mode:" + focusMode);

        if (leftGripDown && !_leftGripWasDown)
        {
            focusMode = (FocusMode)(((int)focusMode + 1) % 3);
            Debug.Log("[DOFDriver] Focus mode → " + focusMode);
        }
        _leftGripWasDown = leftGripDown;

        if (rightGripDown && !_rightGripWasDown)
        {
            _strengthStepIndex = (_strengthStepIndex + 1) % _strengthSteps.Length;
            float val = _strengthSteps[_strengthStepIndex];

            if (focusMode == FocusMode.Monochrome)
                monochromeBlurStrength = val;
            else if (focusMode == FocusMode.Chromatic)
                chromaticOverallStrength = val;

            Debug.Log("[DOFDriver] Blur strength → " + val + " (mode:" + focusMode + ")");
        }
        _rightGripWasDown = rightGripDown;
    }

    private void OnEnable()                                              // Unity enable callback
    {
        _cam = GetComponent<Camera>();                                   // Cache camera
        _cam.depthTextureMode |= DepthTextureMode.Depth;                 // Ensure depth texture is available
        CreateOrUpdateMaterial();                                        // Create material if needed
        ResetEngagementState();                                          // Reset smoothing state
    }

    private void OnDisable()                                             // Unity disable callback
    {
        ReleaseBlurRT();                                                 // Release RT resources
        DestroyMaterialSafe();                                           // Destroy material safely
    }

    private void OnValidate()                                            // Unity editor validation callback
    {
        if (_cam == null) _cam = GetComponent<Camera>();                 // Re-cache camera in editor
        if (_cam != null) _cam.depthTextureMode |= DepthTextureMode.Depth; // Keep depth enabled
        CreateOrUpdateMaterial();                                        // Ensure material uses assigned shader
    }

    // ------------------------------------------------------------------ // Section divider
    // Core Rendering Loop                                                 // Section label
    // ------------------------------------------------------------------ // Section divider

    private void OnRenderImage(RenderTexture src, RenderTexture dst)     // Image effect callback
    {
        if (src == null || dst == null) return;                          // Safety check

        CreateOrUpdateMaterial();                                        // Ensure material exists

        if (focusMode == FocusMode.Off)                                   // Off mode: no blur regardless of trial state
        {
            Graphics.Blit(src, dst);                                     // Pass-through
            return;                                                      // Exit
        }

        if (_mat == null || gazeSource == null || targetAppear == null)  // Required dependencies missing
        {
            Graphics.Blit(src, dst);                                     // Pass-through
            return;                                                      // Exit
        }

        bool inShowTarget =                                              // Determine if effect should run
            (targetAppear.state == TargetAppear.ExpState.EXP_SHOW_TARGET) && // Must be in show-target state
            targetAppear.IsTargetVisibleForGaze &&                        // Target must be visible
            (targetAppear.CurrentTargetTransform != null);                // Must have current target transform

        if (!inShowTarget)                                               // If not in correct state
        {
            ResetEngagementState();                                      // Reset so blur does not leak
            Graphics.Blit(src, dst);                                     // Pass-through
            return;                                                      // Exit
        }

        int stamp = targetAppear.TargetChangeStamp;                      // Target change stamp

        if (stamp != _lastTargetStamp)                                   // Detect new trial
        {
            _lastTargetStamp = stamp;                                    // Store stamp

            // Minimal anti-pop: force the gate to "off" immediately, but do NOT hard reset engage. // Comment
            // With the new fade logic, set timer to a fully faded state (negative fade window).     // Comment
            _graceTimer = -Mathf.Max(0.01f, disengageFadeSeconds);        // NEW: ensures targetLevel starts at 0
        }

        Transform targetT = targetAppear.CurrentTargetTransform;         // Current target transform
        float targetDist = targetAppear.CurrentTargetDistanceMeters;     // Current target distance in meters
        float dt = Time.deltaTime;                                       // Delta time for smoothing

        float focusMetersUsed = Mathf.Max(0.2f, gazeSource.FixationDepthMeters); // Start with fixation depth
        if (snapFocusToTargetDistance)                                   // If using target distance as focus ref
            focusMetersUsed = Mathf.Max(0.2f, targetDist);               // Clamp to avoid too small

        bool eyesValid = true;                                           // Default allow
        if (requireEyeGazeForEngagement)                                 // If gating engagement by eye gaze
        {
            eyesValid = gazeSource.UsedEyeGazeThisFrame &&               // Must be using eye gaze
                        gazeSource.HasValidEyeGazeThisFrame;             // Must be valid gaze
        }

        bool isLookingAtTarget =                                         // Compute on-target condition
            eyesValid && ComputeGazeOnTarget(targetT, targetDist);       // True if gaze is on target

        // ------------------------------                                  // Divider
        // NEW: smoother disengage targetLevel                              // Comment
        // Old behavior: targetLevel jumps 1 or 0 when grace ends           // Comment
        // New behavior: hold at 1 during grace, then linearly ramp to 0    // Comment
        // over disengageFadeSeconds.                                       // Comment
        // ------------------------------                                  // Divider

        float fadeWindow = Mathf.Max(0.01f, disengageFadeSeconds);       // NEW: safe fade window

        if (isLookingAtTarget)                                           // If looking at target
        {
            _graceTimer = disengageGraceSeconds;                         // Reset grace timer (hold stage)
        }
        else                                                             // If not looking at target
        {
            _graceTimer -= dt;                                           // Count down (enters fade stage when <= 0)
            _graceTimer = Mathf.Max(_graceTimer, -fadeWindow);           // NEW: clamp so it stops at fully faded
        }

        float targetLevel;                                               // NEW: continuous target level

        if (_graceTimer > 0f)                                            // If still in grace hold
        {
            targetLevel = 1.0f;                                          // Keep fully engaged during grace
        }
        else                                                             // If grace ended, we are in fade stage
        {
            // When _graceTimer = 0, level = 1. When _graceTimer = -fadeWindow, level = 0. // Comment
            targetLevel = Mathf.Clamp01((_graceTimer + fadeWindow) / fadeWindow); // NEW: smooth ramp down
        }

        float targetSmoothTime =                                         // Choose smoothing time
            (targetLevel >= _currentEngageLevel) ? smoothTimeAttack : smoothTimeDecay; // Attack if rising, decay if falling

        _currentEngageLevel = Mathf.SmoothDamp(                          // Smooth engagement level
            _currentEngageLevel,                                         // Current
            targetLevel,                                                 // NEW: continuous target (not hard 0/1)
            ref _engageVelocity,                                         // Velocity ref
            targetSmoothTime,                                            // Smooth time
            Mathf.Infinity,                                              // No max speed
            dt                                                          // Delta time
        );

        UpdateDynamicVisualParams(focusMetersUsed, dt);                  // Update runtime degrees and multipliers

        Vector2 gazeUV = ComputeGazeUV(focusMetersUsed, Camera.MonoOrStereoscopicEye.Mono); // Compute mono gaze UV
        Vector2 leftGazeUV = gazeUV;
        Vector2 rightGazeUV = gazeUV;

        if (_cam.stereoEnabled)
        {
            leftGazeUV = ComputeGazeUV(focusMetersUsed, Camera.MonoOrStereoscopicEye.Left);
            rightGazeUV = ComputeGazeUV(focusMetersUsed, Camera.MonoOrStereoscopicEye.Right);
        }

        SetShaderGlobals(leftGazeUV, rightGazeUV, focusMetersUsed);       // Push shader uniforms

        if (focusMode == FocusMode.Chromatic)                            // Enable chromatic keyword for this material
            _mat.EnableKeyword("CHROMABLUR_ON");
        else
            _mat.DisableKeyword("CHROMABLUR_ON");

        _mat.SetFloat("_Engage01", Mathf.Clamp01(_currentEngageLevel));  // Provide engage level to shader

        bool showDot = drawRedDotWhenOnTarget && isLookingAtTarget;      // Dot only when strictly on target
        _mat.SetFloat("_DebugGazeDot", showDot ? 1f : 0f);               // Toggle dot in shader

        // ------------------------------                                  // Divider
        // NEW: removed early-exit threshold to avoid popping               // Comment
        // Old behavior switched _BlurTex from mip RT to src below 0.001,   // Comment
        // causing a visible discontinuity. Now we always use the same path. // Comment
        // ------------------------------                                  // Divider

        EnsureBlurRT(src);                                               // Allocate or resize blur RT

        Graphics.Blit(src, _blurMipRT);                                  // Copy src into blur RT
        _blurMipRT.GenerateMips();                                       // Generate mip chain

        _mat.SetTexture("_BlurTex", _blurMipRT);                         // Send mip chain to shader
        Graphics.Blit(src, dst, _mat, 0);                                // Run combine shader pass

        if (debugLog && Time.frameCount % 30 == 0)                       // Every 30 frames
        {
            Debug.Log(                                                   // Print useful values
                "[FoveatedBlur] " +                                      // Prefix
                $"OnTarget:{isLookingAtTarget} " +                       // On-target state
                $"| TargetLevel:{targetLevel:F3} " +                     // NEW: continuous target level
                $"| Engage:{_currentEngageLevel:F3} " +                  // Engagement
                $"| GraceTimer:{_graceTimer:F3} " +                      // NEW: grace/fade timer
                $"| FocusM:{focusMetersUsed:F2} " +                      // Focus meters
                $"| VFoptM:{varifocalOpticalFocusMeters:F2} " +          // Optical focus meters
                $"| Inner:{_rtInnerDeg:F1} Outer:{_rtOuterDeg:F1} " +    // Runtime fovea degrees
                $"| StrMult:{_rtStrengthMult:F2} BaseMult:{_rtBaseBlurMult:F2}" // Runtime multipliers
            );
        }
    }

    // ------------------------------------------------------------------ // Section divider
    // Dynamic Visual Params                                               // Section label
    // ------------------------------------------------------------------ // Section divider

    private void UpdateDynamicVisualParams(float focusMetersUsed, float dt) // Update fovea degrees and blur multipliers
    {
        float dist01 = Mathf.InverseLerp(nearDistanceRef, farDistanceRef, focusMetersUsed); // 0 near, 1 far
        dist01 = Mathf.Clamp01(dist01);                                   // Safety clamp

        float innerTarget = Mathf.Lerp(innerDegNear, innerDegFar, dist01); // Distance-based inner deg
        float outerTarget = Mathf.Lerp(outerDegNear, outerDegFar, dist01); // Distance-based outer deg

        float mismatch01 = 0f;                                            // 0 matched, 1 mismatched
        bool hasVarifocal = varifocalOpticalFocusMeters > 0.01f;          // True if varifocal distance is provided

        if (driveByVarifocalMismatch && hasVarifocal)                     // If using true diopter mismatch
        {
            float gazeD = 1f / Mathf.Max(0.01f, focusMetersUsed);         // Focus in diopters
            float optD  = 1f / Mathf.Max(0.01f, varifocalOpticalFocusMeters); // Optical focus in diopters
            float mismatchD = Mathf.Abs(gazeD - optD);                    // Absolute mismatch in diopters
            mismatch01 = Mathf.InverseLerp(0f, maxMismatchDiopters, mismatchD); // Normalize mismatch to 0..1
        }
        else                                                              // If varifocal not available
        {
            mismatch01 = dist01 * fallbackDistanceMismatchBias;           // Soft far bias without forcing full mismatch
        }

        mismatch01 = Mathf.Clamp01(mismatch01);                           // Clamp mismatch
        float clarity01 = 1f - mismatch01;                                // 1 matched (clear), 0 mismatched (blur)

        float widen = Mathf.Lerp(0.85f, 1.20f, clarity01);                // Matched widens, mismatched narrows
        innerTarget *= widen;                                             // Apply widen to inner
        outerTarget *= widen;                                             // Apply widen to outer

        innerTarget = Mathf.Clamp(innerTarget, 0.5f, 30f);                // Clamp inner degrees
        outerTarget = Mathf.Max(outerTarget, innerTarget + minFoveaBandDeg); // Ensure outer >= inner + band

        float strengthMultTarget =                                        // Base mapping from clarity to strength multiplier
            Mathf.Lerp(strengthMultWhenMismatched, strengthMultWhenMatched, clarity01); // Clear reduces strength, mismatch increases

        float baseBlurMultTarget =                                        // Base mapping from clarity to base blur multiplier
            Mathf.Lerp(baseBlurMultMismatched, baseBlurMultMatched, clarity01); // Clear reduces base, mismatch increases

        if (enableNearDistanceBoost)                                      // If near boost is enabled
        {
            float near01 = 1f - dist01;                                   // 1 near, 0 far
            near01 = Mathf.Clamp01(near01);                               // Clamp near01
            float nearCurve = Mathf.Pow(near01, nearBoostPower);          // Shape the near boost curve

            if (nearBoostPreferMatched)                                   // If we want to preserve far look
            {
                float matchedWeight = Mathf.SmoothStep(0f, 1f, clarity01); // 0 when mismatched, 1 when matched
                nearCurve *= matchedWeight;                               // Apply near boost mostly when matched
            }

            float nearStrengthBoost = Mathf.Lerp(1f, nearStrengthBoostAtMinDistance, nearCurve); // Strength boost factor
            float nearBaseBoost = Mathf.Lerp(1f, nearBaseBlurBoostAtMinDistance, nearCurve);     // Base boost factor

            strengthMultTarget *= nearStrengthBoost;                      // Increase blur strength more at near
            baseBlurMultTarget *= nearBaseBoost;                          // Increase base blur more at near
        }

        _rtInnerDeg = Mathf.SmoothDamp(                                   // Smooth inner degrees
            _rtInnerDeg,                                                 // Current inner
            innerTarget,                                                 // Target inner
            ref _velInnerDeg,                                            // Velocity ref
            visualParamSmoothTime,                                       // Smooth time
            Mathf.Infinity,                                              // No max speed
            dt                                                          // Delta time
        );

        _rtOuterDeg = Mathf.SmoothDamp(                                   // Smooth outer degrees
            _rtOuterDeg,                                                 // Current outer
            outerTarget,                                                 // Target outer
            ref _velOuterDeg,                                            // Velocity ref
            visualParamSmoothTime,                                       // Smooth time
            Mathf.Infinity,                                              // No max speed
            dt                                                          // Delta time
        );

        _rtStrengthMult = Mathf.SmoothDamp(                               // Smooth strength multiplier
            _rtStrengthMult,                                             // Current
            strengthMultTarget,                                          // Target
            ref _velStrengthMult,                                        // Velocity ref
            visualParamSmoothTime,                                       // Smooth time
            Mathf.Infinity,                                              // No max speed
            dt                                                          // Delta time
        );

        _rtBaseBlurMult = Mathf.SmoothDamp(                               // Smooth base blur multiplier
            _rtBaseBlurMult,                                             // Current
            baseBlurMultTarget,                                          // Target
            ref _velBaseBlurMult,                                        // Velocity ref
            visualParamSmoothTime,                                       // Smooth time
            Mathf.Infinity,                                              // No max speed
            dt                                                          // Delta time
        );
    }

    // ------------------------------------------------------------------ // Section divider
    // Compute On Target                                                   // Section label
    // ------------------------------------------------------------------ // Section divider

    private bool ComputeGazeOnTarget(Transform targetT, float targetDist) // Returns true if gaze is on the target
    {
        if (targetT == null) return false;                                // No target means false

        bool strictHit = false;                                           // Strict collider hit result
        bool sustainHit = false;                                          // Sustain zone result

        if (gazeSource.HasHit && gazeSource.HitCollider != null)          // If gaze ray hit something
        {
            Collider hitCol = gazeSource.HitCollider;                     // Collider that was hit
            bool hitIsTarget =                                            // Check if hit belongs to target
                (hitCol.transform == targetT) || hitCol.transform.IsChildOf(targetT); // Allow child colliders

            float distErr = Mathf.Abs(gazeSource.HitDistanceMeters - targetDist); // Compare hit depth vs target depth
            bool depthOk = distErr <= targetDepthToleranceMeters;         // Depth within tolerance

            if (hitIsTarget && depthOk) strictHit = true;                 // Strict hit success
        }

        if (sustainRadiusMeters > 0f)                                     // If sustain is enabled
        {
            Ray r = gazeSource.GazeRayWorld;                              // World gaze ray
            Vector3 toTarget = targetT.position - r.origin;               // Vector from ray origin to target center
            float t = Vector3.Dot(toTarget, r.direction);                 // Projection along ray direction

            if (t > 0f)                                                   // Only if target is in front
            {
                Vector3 closestPoint = r.origin + r.direction * t;        // Closest point on ray
                float distFromCenter = Vector3.Distance(closestPoint, targetT.position); // Lateral distance to center

                if (distFromCenter <= sustainRadiusMeters)                // Within sustain radius
                {
                    float depthErr = Mathf.Abs(t - targetDist);           // Depth error along ray
                    if (depthErr <= targetDepthToleranceMeters)           // Check depth tolerance
                        sustainHit = true;                                // Sustain success
                }
            }
        }

        if (requireHitTargetCollider)                                     // If strict required
            return strictHit;                                             // Require the current target collider
        else                                                              // If strict not required
            return strictHit || sustainHit;                               // Soft acquisition is allowed
    }

    // ------------------------------------------------------------------ // Section divider
    // Reset                                                               // Section label
    // ------------------------------------------------------------------ // Section divider

    private void ResetEngagementState()                                   // Reset engagement and runtime params
    {
        _currentEngageLevel = 0f;                                         // Reset engage level
        _engageVelocity = 0f;                                             // Reset engage velocity

        float fadeWindow = Mathf.Max(0.01f, disengageFadeSeconds);        // NEW: safe fade window
        _graceTimer = -fadeWindow;                                        // NEW: start fully off for new ramp logic

        _rtInnerDeg = foveaInnerDeg;                                      // Init runtime inner deg
        _rtOuterDeg = foveaOuterDeg;                                      // Init runtime outer deg
        _rtStrengthMult = 1f;                                             // Init strength mult
        _rtBaseBlurMult = 1f;                                             // Init base blur mult

        _velInnerDeg = 0f;                                                // Reset inner velocity
        _velOuterDeg = 0f;                                                // Reset outer velocity
        _velStrengthMult = 0f;                                            // Reset strength velocity
        _velBaseBlurMult = 0f;                                            // Reset base velocity
    }

    // ------------------------------------------------------------------ // Section divider
    // Shader Setup                                                        // Section label
    // ------------------------------------------------------------------ // Section divider

    private void SetShaderGlobals(Vector2 leftGazeUV, Vector2 rightGazeUV, float focusDist) // Push uniforms to shader
    {
        float tanHalfFovY = Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad); // tan(fovY/2)
        float tanHalfFovX = tanHalfFovY * _cam.aspect;                    // tan(fovX/2)

        _mat.SetVector("_GazeUV", new Vector4(leftGazeUV.x, leftGazeUV.y, rightGazeUV.x, rightGazeUV.y)); // Per-eye gaze UV
        _mat.SetFloat("_FocusMeters", Mathf.Max(0.01f, focusDist));       // Focus distance meters

        float inner = Mathf.Max(0.01f, _rtInnerDeg);                      // Clamp inner
        float outer = Mathf.Max(inner + 0.01f, _rtOuterDeg);              // Ensure outer > inner

        _mat.SetFloat("_FoveaInnerDeg", inner);                           // Inner deg uniform
        _mat.SetFloat("_FoveaOuterDeg", outer);                           // Outer deg uniform

        _mat.SetFloat("_TanHalfFovX", tanHalfFovX);                       // FOV helper X
        _mat.SetFloat("_TanHalfFovY", tanHalfFovY);                       // FOV helper Y

        _mat.SetFloat("_MaxMip", Mathf.Max(1, maxMip));                   // Max mip uniform

        float baseBlur = Mathf.Clamp01(basePeripheryBlur * _rtBaseBlurMult); // Base periphery blur scaled
        float depthW = Mathf.Max(0f, depthBlurWeight * _rtBaseBlurMult);  // Depth weight scaled

        _mat.SetFloat("_BasePeripheryBlur", baseBlur);                    // Base blur uniform
        _mat.SetFloat("_DepthBlurWeight", depthW);                        // Depth weight uniform
        _mat.SetFloat("_DefocusAtMaxBlurDiopters", Mathf.Max(0.01f, defocusDioptersAtMaxBlur)); // Defocus uniform

        float modeMult = focusMode == FocusMode.Monochrome ? Mathf.Max(0f, monochromeBlurStrength)
                       : focusMode == FocusMode.Chromatic  ? Mathf.Max(0f, chromaticOverallStrength)
                       : 1f;
        _mat.SetFloat("_BlurStrength", Mathf.Max(0f, blurStrength * _rtStrengthMult * modeMult)); // Strength uniform

        _mat.SetFloat("_DotRadiusUV", Mathf.Max(0.0001f, dotRadiusUV));   // Dot radius uniform
        _mat.SetFloat("_UseDirectBlur", useDirectBlurFallback ? 1f : 0f);
        _mat.SetFloat("_DirectBlurRadiusPixels", Mathf.Max(1f, directBlurRadiusPixels));

        // Chromatic aberration uniforms (always pushed; ignored by shader when CHROMABLUR_ON is off)
        _mat.SetFloat("_ChromaticOffsetR", chromaticOffsetR);
        _mat.SetFloat("_ChromaticOffsetG", chromaticOffsetG);
        _mat.SetFloat("_ChromaticOffsetB", chromaticOffsetB);
        _mat.SetFloat("_ChromaticBlurStrength", Mathf.Max(0f, chromaticBlurStrength));
        _mat.SetFloat("_MaxChromaticMip", Mathf.Max(1, maxChromaticMip));
        _mat.SetFloat("_ChromaticFovealWeight", Mathf.Clamp01(chromaticFovealWeight));
    }

    private void CreateOrUpdateMaterial()                                 // Ensure material exists and uses blurShader
    {
        if (blurShader == null) return;                                   // No shader assigned
        if (_mat == null || _mat.shader != blurShader)                    // If missing or wrong shader
        {
            DestroyMaterialSafe();                                        // Destroy old material safely
            _mat = new Material(blurShader);                              // Create new material
            _mat.hideFlags = HideFlags.HideAndDontSave;                   // Prevent saving as asset
        }
    }

    private void DestroyMaterialSafe()                                    // Destroy material in editor or play mode safely
    {
        if (_mat == null) return;                                         // Nothing to destroy
        if (Application.isPlaying) Destroy(_mat);                         // Use Destroy in play mode
        else DestroyImmediate(_mat);                                      // Use DestroyImmediate in editor
        _mat = null;                                                      // Clear reference
    }

    // ------------------------------------------------------------------ // Section divider
    // Compute gaze UV                                                     // Section label
    // ------------------------------------------------------------------ // Section divider

    private Vector2 ComputeGazeUV(float focusMetersUsed, Camera.MonoOrStereoscopicEye eye) // Convert fixation point to viewport UV
    {
        Ray gazeRay = gazeSource.GazeRayWorld;                            // Get gaze ray
        Vector3 fixationWS;                                               // World-space fixation point

        if (gazeSource.HasHit)                                            // If raycast hit something
            fixationWS = gazeSource.HitPointWorld;                        // Use hit point
        else                                                              // If no hit
            fixationWS = gazeRay.origin + gazeRay.direction * focusMetersUsed; // Use point along ray

        Vector3 camPos = _cam.transform.position;                         // Camera position
        Vector3 camFwd = _cam.transform.forward;                          // Camera forward

        if (Vector3.Dot(fixationWS - camPos, camFwd) < 0f)                // If fixation behind camera
            fixationWS = camPos + camFwd * focusMetersUsed;               // Force fixation in front

        Vector3 vp = _cam.WorldToViewportPoint(fixationWS, eye);          // Convert to viewport coordinates

        float gx = (!float.IsNaN(vp.x) && !float.IsInfinity(vp.x)) ? Mathf.Clamp01(vp.x) : 0.5f; // Safe x
        float gy = (!float.IsNaN(vp.y) && !float.IsInfinity(vp.y)) ? Mathf.Clamp01(vp.y) : 0.5f; // Safe y

        return new Vector2(gx, gy);                                       // Return UV
    }

    // ------------------------------------------------------------------ // Section divider
    // Blur RT allocation                                                  // Section label
    // ------------------------------------------------------------------ // Section divider

    private void EnsureBlurRT(RenderTexture src)                          // Ensure mip RT exists and matches size
    {
        int ds = Mathf.Max(1, downsampleBlurTexture);                     // Clamp downsample
        int w = Mathf.Max(1, src.width / ds);                             // Compute width
        int h = Mathf.Max(1, src.height / ds);                            // Compute height

        bool needNew = (_blurMipRT == null) || (_blurMipRT.width != w) || (_blurMipRT.height != h); // Need recreate?
        if (!needNew) return;                                             // Already correct

        ReleaseBlurRT();                                                  // Release old RT first

        var desc = new RenderTextureDescriptor(w, h, src.format, 0);      // RT descriptor
        desc.useMipMap = true;                                            // Enable mipmaps
        desc.autoGenerateMips = false;                                    // We generate mips manually
        desc.mipCount = Mathf.Clamp(maxMip + 1, 2, 12);                   // Safe mip count

        _blurMipRT = new RenderTexture(desc);                             // Create RT
        _blurMipRT.filterMode = FilterMode.Trilinear;                     // Smooth mip sampling
        _blurMipRT.wrapMode = TextureWrapMode.Clamp;                      // Clamp edges
        _blurMipRT.Create();                                              // Allocate GPU resource
    }

    private void ReleaseBlurRT()                                          // Release RT resources
    {
        if (_blurMipRT == null) return;                                   // Nothing to release
        _blurMipRT.Release();                                             // Release GPU memory
        _blurMipRT = null;                                                // Clear reference
    }
}
