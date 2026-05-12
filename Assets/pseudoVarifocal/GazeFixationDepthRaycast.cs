

/// before changes in 1/17/2026
// GazeFixationDepthRaycast.cs
// Updated to provide clean gaze origin/direction outputs for motion classification and to manage marker visibility.

using System;                     // StringComparison
using System.Collections.Generic; // HashSet
using UnityEngine;                // Unity

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;        // Android runtime permission API
#endif

public class GazeFixationDepthRaycast : MonoBehaviour
{
    // ----------------------------
    // Modes
    // ----------------------------

    public enum GazeMode
    {
        EyeOnly = 0,                   // Only eyes, no fallback
        EyePreferred_HeadFallback = 1, // Eyes when valid, else head ray
        HeadOnly = 2                   // Only head
    }

    [Header("Mode")]
    public GazeMode gazeMode = GazeMode.EyePreferred_HeadFallback; // Default mode

    // ----------------------------
    // Transforms
    // ----------------------------

    [Header("Transforms")]
    [Tooltip("Assign OVRCameraRig/TrackingSpace. If you assign a camera/eye anchor by mistake, we auto-fix.")]
    public Transform trackingSpace; // Must be TrackingSpace for correct conversion

    [Tooltip("Used for head fallback ray. If null, Camera.main is used.")]
    public Camera headFallbackCamera; // Head ray source

    // ----------------------------
    // Optional marker
    // ----------------------------

    [Header("Optional Marker")]
    public Transform worldFixationMarker;   // Debug marker
    public bool autoCreateWorldFixationMarker = false; // Create a red marker if none is assigned
    public bool markerRequiresEyeGaze = true; // Only show marker for real eye gaze
    public bool markerOnlyOnSurfaceHit = true; // Only show when hit
    public bool hideMarkerWhenNoHit = true;    // Hide marker when not hit
    public float markerDiameterMeters = 0.06f; // Minimum marker size
    public float markerAngularSizeDeg = 1.25f; // Keeps marker visible at distance
    public float markerSurfaceOffsetMeters = 0.025f; // Avoid z-fighting on hit surfaces

    // ----------------------------
    // Raycast
    // ----------------------------

    [Header("Raycast")]
    public LayerMask raycastLayers = ~0;         // Layers to test
    public float raycastMaxDistance = 25f;       // Max ray length
    public float fallbackDistanceMeters = 2f;    // Fallback depth when no hit
    public float minHitDistanceMeters = 0.02f;   // Ignore too-near hits
    public bool hitTriggers = false;             // Include triggers?
    public bool syncTransformsBeforeRaycast = false; // Physics.SyncTransforms?

    // ----------------------------
    // Eye filtering
    // ----------------------------

    [Header("Eye Filtering")]
    [Range(0f, 1f)]
    public float confidenceThreshold = 0.0f; // Minimal confidence to accept

    // ----------------------------
    // Ignore filters
    // ----------------------------

    [Header("Ignore Filters")]
    [Tooltip("Assign OVRCameraRig root so you do not hit your own rig/hands.")]
    public Transform ignoreRoot; // Rig root
    public bool ignoreMarkerColliders = true; // Ignore marker colliders

    // ----------------------------
    // Depth smoothing (raw depth output, not your fixation gating)
    // ----------------------------

    [Header("Depth Smoothing (Raw)")]
    public float smoothingHalfLifeSeconds = 0.02f; // EMA half-life
    public float maxDepthChangePerSecond = 200f;   // Rate clamp

    // ----------------------------
    // Performance
    // ----------------------------

    [Header("Performance")]
    [Range(8, 256)]
    public int raycastHitBufferSize = 64; // NonAlloc buffer size

    // ----------------------------
    // Debug
    // ----------------------------

    [Header("Debug")]
    public bool drawDebugRay = false;     // Draw ray in Scene view
    public bool debugLog = true;          // Periodic logs
    public bool showOnScreenDebug = true;  // OnGUI HUD
    public bool showWorldDebugStatus = true; // XR-visible status label
    public Vector3 worldDebugLocalOffset = new Vector3(-0.55f, 0.35f, 1.25f); // Camera-local status position
    public float worldDebugCharacterSize = 0.035f; // Text size in headset

    // ----------------------------
    // Permission and startup
    // ----------------------------

    [Header("Permission + Startup")]
    public bool requestEyeTrackingPermissionOnEnable = true; // Request permission at runtime
    public bool autoStartEyeTracking = true;                 // Start eye tracking automatically

    // ----------------------------
    // Public outputs
    // ----------------------------

    public Ray GazeRayWorld => _gazeRayWorld;                   // World ray
    public Vector3 GazeOriginWorld => _gazeRayWorld.origin;     // Ray origin
    public Vector3 GazeDirectionWorld => _gazeRayWorld.direction; // Ray direction (normalized)

    public bool HasHit => _hasHit;                    // Hit something?
    public Collider HitCollider => _hitCollider;      // Hit collider
    public Vector3 HitPointWorld => _hitPointWorld;   // Hit point
    public Vector3 HitNormalWorld => _hitNormalWorld; // Hit normal
    public float HitDistanceMeters => _hitDistance;   // Hit distance
    public float FixationDepthMeters => _fixationDepthSmoothed; // Smoothed raw depth
    public Vector3 FixationPointWorld => _fixationPointWorld;   // Hit point or fallback point

    public bool UsedEyeGazeThisFrame => _usedEyeGazeThisFrame;         // Used eyes, not head fallback
    public bool HasValidEyeGazeThisFrame => _hasValidEyeGazeThisFrame; // Eye ray valid this frame

    public bool EyeTrackingStarted => _eyeTrackingStarted; // Started OK
    public bool PermissionGranted => _permissionGranted;   // Permission state
    public bool EyeTrackingSupported => _eyeTrackingSupported; // Runtime/device support
    public bool EyeTrackingRuntimeEnabled => _eyeTrackingRuntimeEnabled; // Runtime enabled flag
    public bool OvrPluginInitialized => _ovrPluginInitialized; // Oculus runtime initialized

    public bool LeftValidThisFrame => _leftValid;  // Left accepted
    public bool RightValidThisFrame => _rightValid;// Right accepted
    public float LeftConfidence => _leftConf;      // Left confidence
    public float RightConfidence => _rightConf;    // Right confidence

    // ----------------------------
    // Internal state
    // ----------------------------

    private OVRPlugin.EyeGazesState _eyeGazesState; // Eye data container
    private Ray _gazeRayWorld;                      // Final ray

    private bool _hasHit;              // Hit flag
    private Collider _hitCollider;     // Hit collider
    private Vector3 _hitPointWorld;    // Hit point
    private Vector3 _hitNormalWorld;   // Hit normal
    private float _hitDistance;        // Hit distance

    private float _fixationDepthSmoothed = 2f; // Smoothed depth
    private Vector3 _fixationPointWorld;       // Fixation point

    private bool _leftValid, _rightValid; // Filtered validity
    private float _leftConf, _rightConf;  // Confidence values

    private bool _usedEyeGazeThisFrame = false;   // Used eye ray?
    private bool _hasValidEyeGazeThisFrame = false; // Eye valid?

    private readonly HashSet<Collider> _ignoreColliders = new HashSet<Collider>(); // Ignore set
    private RaycastHit[] _hitsNonAlloc; // Raycast buffer
    private GameObject _autoWorldMarkerGO; // Runtime marker if none assigned
    private Material _autoWorldMarkerMaterial; // Runtime red marker material
    private GameObject _worldDebugStatusGO;
    private TextMesh _worldDebugText;

    private bool _permissionGranted = false;         // Permission status
    private bool _eyeTrackingStarted = false;        // Started?
    private bool _ovrPluginInitialized = false;
    private bool _eyeTrackingSupported = false;
    private bool _eyeTrackingRuntimeEnabled = false;
    private string _lastEyeTrackingStatus = "Not started";

#if UNITY_ANDROID && !UNITY_EDITOR
    private bool _permissionRequestInFlight = false; // Request in flight (Android only)
    private const string ANDROID_EYE_TRACKING_PERMISSION = "com.oculus.permission.EYE_TRACKING";
#endif

    private void OnEnable()
    {
        EnsureHitBuffer();                // Ensure buffer
        AutoResolveReferencesIfMissing(); // Resolve references
        RebuildIgnoreColliderCache();     // Rebuild ignore cache

        if (requestEyeTrackingPermissionOnEnable)
            TryRequestEyeTrackingPermission(); // Request permission

        if (autoStartEyeTracking)
            TryStartEyeTrackingIfPossible();   // Try start
    }

    private void OnDisable()
    {
        // Stop eye tracking to avoid stale states
        try { OVRPlugin.StopEyeTracking(); } catch { }
        _eyeTrackingStarted = false; // Reset
        DestroyAutoWorldMarkerSafe(); // Clean runtime marker
        DestroyWorldDebugStatusSafe(); // Clean runtime status label

#if UNITY_ANDROID && !UNITY_EDITOR
        OVRPermissionsRequester.PermissionGranted -= OnOvrPermissionGranted;
#endif
    }

    private void Start()
    {
        AutoResolveReferencesIfMissing(); // Resolve again
        RebuildIgnoreColliderCache();     // Rebuild ignore cache
    }

    private void OnValidate()
    {
        EnsureHitBuffer(); // Keep buffer sized
    }

    private void LateUpdate()
    {
        EnsureHitBuffer();                // Ensure buffer
        AutoResolveReferencesIfMissing(); // Keep references correct

        if (autoStartEyeTracking)
            TryStartEyeTrackingIfPossible(); // Keep trying until it starts

        Ray rayWorld = default; // Final ray for this frame

        _usedEyeGazeThisFrame = false;        // Reset flag
        _hasValidEyeGazeThisFrame = false;    // Reset flag
        _leftValid = _rightValid = false;      // Reset per-eye validity
        _leftConf = _rightConf = 0f;           // Reset per-eye confidence

        if (gazeMode == GazeMode.HeadOnly)
        {
            rayWorld = BuildHeadFallbackRay(); // Head ray
        }
        else
        {
            bool eyeOk = false; // Eye ray valid?

            if (_eyeTrackingStarted)
                eyeOk = TryBuildEyeGazeRayWorld(out rayWorld); // Try eyes

            _hasValidEyeGazeThisFrame = eyeOk; // Publish validity
            _usedEyeGazeThisFrame = eyeOk;     // Used eyes only if valid

            if (!eyeOk)
            {
                if (gazeMode == GazeMode.EyeOnly)
                {
                    _gazeRayWorld = default; // Clear ray
                    ClearHitState();         // Clear hit state
                    UpdateWorldMarker(false);// Update marker
                    UpdateWorldDebugStatus(); // Keep XR status visible
                    return;                  // No fallback outputs
                }

                rayWorld = BuildHeadFallbackRay(); // Head fallback
                _usedEyeGazeThisFrame = false;     // Mark as head fallback
            }
        }

        _gazeRayWorld = rayWorld; // Store final ray

        if (drawDebugRay)
            Debug.DrawRay(_gazeRayWorld.origin, _gazeRayWorld.direction * 5f, Color.green); // Visualize ray

        bool hit = TryRaycastFilteredNonAlloc(_gazeRayWorld, out RaycastHit hitInfo); // Raycast

        _hasHit = hit; // Store hit flag

        if (hit)
        {
            _hitCollider = hitInfo.collider; // Store collider
            _hitPointWorld = hitInfo.point;  // Store point
            _hitNormalWorld = hitInfo.normal;// Store normal
            _hitDistance = hitInfo.distance; // Store distance
        }
        else
        {
            _hitCollider = null;             // Clear collider
            _hitPointWorld = Vector3.zero;   // Clear point
            _hitNormalWorld = Vector3.zero;  // Clear normal
            _hitDistance = 0f;               // Clear distance
        }

        float depthRaw = hit ? hitInfo.distance : fallbackDistanceMeters; // Depth choice

        float dt = Mathf.Max(Time.deltaTime, 1e-6f); // Safe dt
        float halfLife = Mathf.Max(smoothingHalfLifeSeconds, 1e-6f); // Safe half-life

        float alpha = 1f - Mathf.Exp(-0.69314718056f * dt / halfLife); // EMA alpha

        float depthEma = Mathf.Lerp(_fixationDepthSmoothed, depthRaw, alpha); // EMA update

        float maxDelta = Mathf.Max(0f, maxDepthChangePerSecond) * dt; // Rate clamp
        _fixationDepthSmoothed = Mathf.MoveTowards(_fixationDepthSmoothed, depthEma, maxDelta); // Apply clamp

        _fixationPointWorld = hit
            ? hitInfo.point
            : (_gazeRayWorld.origin + _gazeRayWorld.direction * _fixationDepthSmoothed); // Fixation point

        UpdateWorldMarker(hit); // Update marker

        if (debugLog && Time.frameCount % 60 == 0)
        {
            Debug.Log(
                "[GazeFixationDepthRaycast] started=" + _eyeTrackingStarted +
                " perm=" + _permissionGranted +
                " usedEye=" + _usedEyeGazeThisFrame +
                " eyeValid=" + _hasValidEyeGazeThisFrame +
                " L(valid=" + _leftValid + " conf=" + _leftConf.ToString("F2") + ")" +
                " R(valid=" + _rightValid + " conf=" + _rightConf.ToString("F2") + ")" +
                " mode=" + gazeMode +
                " trackingSpace=" + (trackingSpace ? trackingSpace.name : "null")
            );
        }

        UpdateWorldDebugStatus();
    }

    private bool TryBuildEyeGazeRayWorld(out Ray rayWorld)
    {
        rayWorld = default; // Init

        bool ok; // API status
        try
        {
            ok = OVRPlugin.GetEyeGazesState(OVRPlugin.Step.Render, -1, ref _eyeGazesState); // Fetch gaze
        }
        catch
        {
            return false; // API failed
        }

        if (!ok)
        {
            _lastEyeTrackingStatus = "GetEyeGazesState returned false";
            return false; // No data
        }

        var left = _eyeGazesState.EyeGazes[(int)OVRPlugin.Eye.Left];  // Left eye
        var right = _eyeGazesState.EyeGazes[(int)OVRPlugin.Eye.Right];// Right eye

        _leftConf = left.Confidence;  // Cache confidence
        _rightConf = right.Confidence;// Cache confidence

        bool leftOk = left.IsValid && left.Confidence >= confidenceThreshold;   // Filter left
        bool rightOk = right.IsValid && right.Confidence >= confidenceThreshold;// Filter right

        _leftValid = leftOk;   // Publish left valid
        _rightValid = rightOk; // Publish right valid

        if (!leftOk && !rightOk)
        {
            _lastEyeTrackingStatus =
                "No valid eye. L valid=" + left.IsValid + " conf=" + _leftConf.ToString("F2") +
                " R valid=" + right.IsValid + " conf=" + _rightConf.ToString("F2");
            return false; // No usable eye data
        }

        Vector3 originTS; // Origin in tracking space
        Vector3 dirTS;    // Direction in tracking space

        if (leftOk && rightOk)
        {
            var lp = left.Pose.ToOVRPose();  // Left pose
            var rp = right.Pose.ToOVRPose(); // Right pose

            originTS = (lp.position + rp.position) * 0.5f; // Midpoint origin

            Vector3 lDir = (lp.orientation * Vector3.forward).normalized; // Left forward
            Vector3 rDir = (rp.orientation * Vector3.forward).normalized; // Right forward

            dirTS = (lDir + rDir).normalized; // Average direction
        }
        else if (leftOk)
        {
            var lp = left.Pose.ToOVRPose(); // Left pose
            originTS = lp.position;         // Origin
            dirTS = (lp.orientation * Vector3.forward).normalized; // Direction
        }
        else
        {
            var rp = right.Pose.ToOVRPose(); // Right pose
            originTS = rp.position;          // Origin
            dirTS = (rp.orientation * Vector3.forward).normalized; // Direction
        }

        Transform ts = ResolveTrackingSpaceTransform(); // Resolve TrackingSpace
        if (ts == null)
            return false; // Cannot convert to world

        Vector3 originWS = ts.TransformPoint(originTS);          // To world
        Vector3 dirWS = ts.TransformDirection(dirTS).normalized; // To world

        rayWorld = new Ray(originWS, dirWS); // Build ray
        _lastEyeTrackingStatus = "Eye gaze valid";
        return true; // Success
    }

    private Transform ResolveTrackingSpaceTransform()
    {
        if (trackingSpace != null)
        {
            if (trackingSpace.name.IndexOf("OVRCameraRig", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Transform childTS = trackingSpace.Find("TrackingSpace"); // Find child
                if (childTS != null) trackingSpace = childTS;           // Fix
            }

            if (trackingSpace.GetComponent<Camera>() != null ||
                trackingSpace.name.IndexOf("CenterEye", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trackingSpace.name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Transform t = trackingSpace; // Start from assigned transform
                for (int i = 0; i < 25 && t != null; i++)
                {
                    if (t.name.IndexOf("TrackingSpace", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        trackingSpace = t; // Fix
                        break;
                    }
                    t = t.parent; // Climb
                }
            }

            return trackingSpace; // Return fixed
        }

        if (Camera.main != null)
        {
            Transform t = Camera.main.transform; // Start from camera
            for (int i = 0; i < 25 && t != null; i++)
            {
                if (t.name.IndexOf("TrackingSpace", StringComparison.OrdinalIgnoreCase) >= 0)
                    return t; // Found TrackingSpace
                t = t.parent; // Climb
            }
        }

        return null; // Not found
    }

    private Ray BuildHeadFallbackRay()
    {
        Camera cam = headFallbackCamera != null ? headFallbackCamera : Camera.main; // Choose camera
        if (cam == null) return new Ray(transform.position, transform.forward);     // Fallback
        return new Ray(cam.transform.position, cam.transform.forward);              // Head ray
    }

    private void TryRequestEyeTrackingPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _permissionGranted = OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.EyeTracking);

        if (_permissionGranted || _permissionRequestInFlight)
            return;

        _permissionRequestInFlight = true; // Mark in flight
        _lastEyeTrackingStatus = "Requesting eye tracking permission";

        OVRPermissionsRequester.PermissionGranted -= OnOvrPermissionGranted;
        OVRPermissionsRequester.PermissionGranted += OnOvrPermissionGranted;

        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += perm =>
        {
            if (perm != ANDROID_EYE_TRACKING_PERMISSION)
                return;

            _permissionGranted = true;
            _permissionRequestInFlight = false;
            _lastEyeTrackingStatus = "Eye tracking permission granted";
            TryStartEyeTrackingIfPossible();
        };
        callbacks.PermissionDenied += perm =>
        {
            if (perm != ANDROID_EYE_TRACKING_PERMISSION)
                return;

            _permissionGranted = false;
            _permissionRequestInFlight = false;
            _lastEyeTrackingStatus = "Eye tracking permission denied";
        };
        callbacks.PermissionDeniedAndDontAskAgain += perm =>
        {
            if (perm != ANDROID_EYE_TRACKING_PERMISSION)
                return;

            _permissionGranted = false;
            _permissionRequestInFlight = false;
            _lastEyeTrackingStatus = "Eye tracking permission denied and do-not-ask-again";
        };

        Permission.RequestUserPermission(ANDROID_EYE_TRACKING_PERMISSION, callbacks);
#else
        _permissionGranted = true; // Editor fallback
#endif
    }

    private void TryStartEyeTrackingIfPossible()
    {
        if (_eyeTrackingStarted) return;        // Already started
        if (gazeMode == GazeMode.HeadOnly) return; // No need

        _ovrPluginInitialized = OVRPlugin.initialized;
        if (!_ovrPluginInitialized)
        {
            _eyeTrackingSupported = false;
            _eyeTrackingRuntimeEnabled = false;
            _lastEyeTrackingStatus = "OVRPlugin not initialized. Check XR loader/startup.";
            return;
        }

        _eyeTrackingSupported = OVRPlugin.eyeTrackingSupported;
        _eyeTrackingRuntimeEnabled = OVRPlugin.eyeTrackingEnabled;

        // SDK v57+: OVRManager.eyeTrackingEnabled starts the subsystem automatically.
        // If it is already enabled, accept that and skip the manual StartEyeTracking call.
        if (_eyeTrackingRuntimeEnabled)
        {
            _eyeTrackingStarted = true;
            _lastEyeTrackingStatus = "Eye gaze valid";
            return;
        }

        if (!_eyeTrackingSupported)
        {
            _lastEyeTrackingStatus = "Eye tracking not supported by device/runtime";
            return;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        _permissionGranted = OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.EyeTracking);
        if (requestEyeTrackingPermissionOnEnable && !_permissionGranted)
        {
            TryRequestEyeTrackingPermission();
            return; // Wait for permission
        }
#endif

        bool started = false; // Start status
        try { started = OVRPlugin.StartEyeTracking(); } catch (Exception e) { _lastEyeTrackingStatus = "Start exception: " + e.Message; } // Try start

        // Re-check: SDK v57+ may have enabled it asynchronously or via OVRManager.
        if (!started) started = OVRPlugin.eyeTrackingEnabled;

        if (!started)
        {
            _eyeTrackingRuntimeEnabled = OVRPlugin.eyeTrackingEnabled;
            _lastEyeTrackingStatus =
                "StartEyeTracking failed. supported=" + _eyeTrackingSupported +
                " enabled=" + _eyeTrackingRuntimeEnabled +
                " permission=" + _permissionGranted;
            return;
        }

        _eyeTrackingStarted = true; // Mark started
        _eyeTrackingRuntimeEnabled = OVRPlugin.eyeTrackingEnabled;
        _lastEyeTrackingStatus = "Started";
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void OnOvrPermissionGranted(string permissionId)
    {
        if (permissionId != OVRPermissionsRequester.GetPermissionId(OVRPermissionsRequester.Permission.EyeTracking))
            return;

        _permissionGranted = true;
        _permissionRequestInFlight = false;
        TryStartEyeTrackingIfPossible();
    }
#endif

    private void EnsureHitBuffer()
    {
        int n = Mathf.Clamp(raycastHitBufferSize, 8, 256); // Clamp size
        if (_hitsNonAlloc == null || _hitsNonAlloc.Length != n)
            _hitsNonAlloc = new RaycastHit[n]; // Allocate
    }

    private bool TryRaycastFilteredNonAlloc(Ray ray, out RaycastHit bestHit)
    {
        bestHit = default; // Init

        if (syncTransformsBeforeRaycast)
            Physics.SyncTransforms(); // Sync if requested

        QueryTriggerInteraction qti = hitTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore; // Trigger policy

        int hitCount = Physics.RaycastNonAlloc(ray, _hitsNonAlloc, raycastMaxDistance, raycastLayers, qti); // NonAlloc raycast
        if (hitCount <= 0)
            return false; // No hits

        float bestDist = float.PositiveInfinity; // Best distance
        bool found = false;                      // Found flag

        int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast"); // Ignore Raycast layer

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit h = _hitsNonAlloc[i]; // Candidate hit

            if (h.collider == null) continue;            // Skip null
            if (h.distance < minHitDistanceMeters) continue; // Skip too near
            if (_ignoreColliders.Contains(h.collider)) continue; // Skip ignored colliders
            if (ignoreRaycastLayer >= 0 && h.collider.gameObject.layer == ignoreRaycastLayer) continue; // Skip layer

            if (h.distance < bestDist)
            {
                bestDist = h.distance; // Update best
                bestHit = h;           // Store
                found = true;          // Mark found
            }
        }

        return found; // Return result
    }

    public void RebuildIgnoreColliderCache()
    {
        _ignoreColliders.Clear(); // Clear

        if (ignoreRoot != null)
        {
            foreach (var c in ignoreRoot.GetComponentsInChildren<Collider>(true))
                _ignoreColliders.Add(c); // Add rig colliders
        }

        if (ignoreMarkerColliders && worldFixationMarker != null)
        {
            foreach (var c in worldFixationMarker.GetComponentsInChildren<Collider>(true))
                _ignoreColliders.Add(c); // Add marker colliders
        }
    }

    private void UpdateWorldMarker(bool hasSurfaceHit)
    {
        if (worldFixationMarker == null && autoCreateWorldFixationMarker)
            CreateAutoWorldFixationMarker(); // Ensure visible eye debug marker

        if (worldFixationMarker == null) return; // No marker

        if (markerRequiresEyeGaze && !_usedEyeGazeThisFrame)
        {
            worldFixationMarker.gameObject.SetActive(false); // Hide stale eye marker
            return;
        }

        if (markerOnlyOnSurfaceHit && !hasSurfaceHit)
        {
            if (hideMarkerWhenNoHit)
                worldFixationMarker.gameObject.SetActive(false); // Hide marker
            return; // Stop
        }

        if (!worldFixationMarker.gameObject.activeSelf)
            worldFixationMarker.gameObject.SetActive(true); // Ensure visible

        Vector3 markerPos = _fixationPointWorld;
        if (hasSurfaceHit && _gazeRayWorld.direction.sqrMagnitude > 1e-6f)
            markerPos -= _gazeRayWorld.direction.normalized * markerSurfaceOffsetMeters; // Avoid surface fighting

        worldFixationMarker.position = markerPos; // Place marker

        Camera cam = headFallbackCamera != null ? headFallbackCamera : Camera.main;
        float viewDistance = cam != null
            ? Vector3.Distance(cam.transform.position, markerPos)
            : Mathf.Max(0.2f, fallbackDistanceMeters);

        float angularDiameter = 2f * viewDistance * Mathf.Tan(markerAngularSizeDeg * 0.5f * Mathf.Deg2Rad);
        float diameter = Mathf.Max(markerDiameterMeters, angularDiameter);
        worldFixationMarker.localScale = Vector3.one * diameter; // Keep readable in headset
    }

    private void CreateAutoWorldFixationMarker()
    {
        if (_autoWorldMarkerGO != null)
            return; // Already created

        _autoWorldMarkerGO = GameObject.CreatePrimitive(PrimitiveType.Sphere); // Visible red gaze point
        _autoWorldMarkerGO.name = "Runtime Eye Gaze Fixation Marker";

        int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
        if (ignoreRaycastLayer >= 0)
            _autoWorldMarkerGO.layer = ignoreRaycastLayer;

        Collider c = _autoWorldMarkerGO.GetComponent<Collider>();
        if (c != null)
            Destroy(c);

        Renderer r = _autoWorldMarkerGO.GetComponent<Renderer>();

        Shader s = Shader.Find("Unlit/Color");
        if (s == null)
            s = Shader.Find("Sprites/Default");
        if (s == null)
            s = Shader.Find("Standard");

        if (s != null)
        {
            _autoWorldMarkerMaterial = new Material(s);
            _autoWorldMarkerMaterial.hideFlags = HideFlags.HideAndDontSave;
            _autoWorldMarkerMaterial.color = Color.red;
            if (_autoWorldMarkerMaterial.HasProperty("_Color"))
                _autoWorldMarkerMaterial.SetColor("_Color", Color.red);
            _autoWorldMarkerMaterial.renderQueue = 5000;

            if (r != null)
                r.sharedMaterial = _autoWorldMarkerMaterial;
        }

        worldFixationMarker = _autoWorldMarkerGO.transform;
        _autoWorldMarkerGO.SetActive(false);
    }

    private void DestroyAutoWorldMarkerSafe()
    {
        bool markerWasAuto =
            worldFixationMarker != null &&
            _autoWorldMarkerGO != null &&
            worldFixationMarker.gameObject == _autoWorldMarkerGO;

        if (_autoWorldMarkerGO != null)
        {
            if (Application.isPlaying) Destroy(_autoWorldMarkerGO);
            else DestroyImmediate(_autoWorldMarkerGO);
        }

        if (_autoWorldMarkerMaterial != null)
        {
            if (Application.isPlaying) Destroy(_autoWorldMarkerMaterial);
            else DestroyImmediate(_autoWorldMarkerMaterial);
        }

        if (markerWasAuto)
            worldFixationMarker = null;

        _autoWorldMarkerGO = null;
        _autoWorldMarkerMaterial = null;
    }

    private void ClearHitState()
    {
        _hasHit = false;                // Clear flag
        _hitCollider = null;            // Clear collider
        _hitPointWorld = Vector3.zero;  // Clear point
        _hitNormalWorld = Vector3.zero; // Clear normal
        _hitDistance = 0f;              // Clear distance
    }

    private void AutoResolveReferencesIfMissing()
    {
        if (headFallbackCamera == null)
            headFallbackCamera = Camera.main; // Default

        if (trackingSpace != null && trackingSpace.name.IndexOf("OVRCameraRig", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var child = trackingSpace.Find("TrackingSpace"); // Find child
            if (child != null) trackingSpace = child;        // Fix
        }

        if (trackingSpace == null && Camera.main != null)
        {
            Transform t = Camera.main.transform; // Start from camera
            for (int i = 0; i < 25 && t != null; i++)
            {
                if (t.name.IndexOf("TrackingSpace", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    trackingSpace = t; // Assign
                    break;
                }
                t = t.parent; // Climb
            }
        }

        if (ignoreRoot == null && Camera.main != null)
        {
            Transform t = Camera.main.transform; // Start from camera
            for (int i = 0; i < 25 && t != null; i++)
            {
                if (t.name.IndexOf("OVRCameraRig", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ignoreRoot = t; // Assign rig root
                    break;
                }
                t = t.parent; // Climb
            }
        }
    }

    private void UpdateWorldDebugStatus()
    {
        if (!showWorldDebugStatus)
        {
            DestroyWorldDebugStatusSafe();
            return;
        }

        Camera cam = headFallbackCamera != null ? headFallbackCamera : Camera.main;
        if (cam == null)
            return;

        if (_worldDebugStatusGO == null)
        {
            _worldDebugStatusGO = new GameObject("Runtime Eye Tracking Status");
            _worldDebugText = _worldDebugStatusGO.AddComponent<TextMesh>();
            _worldDebugText.anchor = TextAnchor.UpperLeft;
            _worldDebugText.alignment = TextAlignment.Left;
            _worldDebugText.fontSize = 64;
        }

        if (_worldDebugStatusGO.transform.parent != cam.transform)
            _worldDebugStatusGO.transform.SetParent(cam.transform, false);

        _worldDebugStatusGO.transform.localPosition = worldDebugLocalOffset;
        _worldDebugStatusGO.transform.localRotation = Quaternion.identity;
        _worldDebugStatusGO.transform.localScale = Vector3.one;

        if (_worldDebugText == null)
            _worldDebugText = _worldDebugStatusGO.GetComponent<TextMesh>();

        if (_worldDebugText == null)
            return;

        bool eyeOk = _usedEyeGazeThisFrame && _hasValidEyeGazeThisFrame;
        _worldDebugStatusGO.SetActive(!eyeOk);
        if (eyeOk) return;
        _worldDebugText.characterSize = Mathf.Max(0.005f, worldDebugCharacterSize);
        _worldDebugText.color = eyeOk ? Color.green : (_eyeTrackingStarted ? Color.yellow : Color.red);
        _worldDebugText.text =
            (eyeOk ? "EYE TRACKING OK" : "NO EYE TRACKING") + "\n" +
            "OVR init: " + _ovrPluginInitialized + "\n" +
            "Supported: " + _eyeTrackingSupported + "  Started: " + _eyeTrackingStarted + "\n" +
            "Permission: " + _permissionGranted + "\n" +
            "Used eye: " + _usedEyeGazeThisFrame + "  Valid: " + _hasValidEyeGazeThisFrame + "\n" +
            "L " + _leftValid + " " + _leftConf.ToString("F2") +
            "  R " + _rightValid + " " + _rightConf.ToString("F2") + "\n" +
            _lastEyeTrackingStatus;
    }

    private void DestroyWorldDebugStatusSafe()
    {
        if (_worldDebugStatusGO == null)
            return;

        if (Application.isPlaying) Destroy(_worldDebugStatusGO);
        else DestroyImmediate(_worldDebugStatusGO);

        _worldDebugStatusGO = null;
        _worldDebugText = null;
    }

    private void OnGUI()
    {
        if (!showOnScreenDebug) return; // Off

        string s =
            "GazeMode: " + gazeMode + "\n" +
            "OVRPluginInitialized: " + _ovrPluginInitialized + "\n" +
            "PermissionGranted: " + _permissionGranted + "\n" +
            "EyeTrackingSupported: " + _eyeTrackingSupported + "\n" +
            "RuntimeEyeTrackingEnabled: " + _eyeTrackingRuntimeEnabled + "\n" +
            "EyeTrackingStarted: " + _eyeTrackingStarted + "\n" +
            "UsedEyeGazeThisFrame: " + _usedEyeGazeThisFrame + "\n" +
            "HasValidEyeGazeThisFrame: " + _hasValidEyeGazeThisFrame + "\n" +
            "Left:  valid=" + _leftValid + " conf=" + _leftConf.ToString("F2") + "\n" +
            "Right: valid=" + _rightValid + " conf=" + _rightConf.ToString("F2") + "\n" +
            "Status: " + _lastEyeTrackingStatus + "\n" +
            "TrackingSpace: " + (trackingSpace ? trackingSpace.name : "null") + "\n" +
            "HasHit: " + _hasHit + "  HitCol: " + (_hitCollider ? _hitCollider.name : "null");

        GUI.Label(new Rect(10, 10, 1000, 340), s); // Draw HUD
    }
}

