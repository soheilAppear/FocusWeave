using UnityEngine;
using OculusSampleFramework;

/// <summary>
/// Single world-space red eye-gaze marker for the current target.
/// This is the only red dot path; screen-space debug dots are disabled in the blur script.
/// </summary>
public class CurrentTargetOnlyWorldDot : MonoBehaviour
{
    [Header("References")]
    public GazeFixationDepthRaycast gaze;
    public TargetAppear targetAppear;
    public Transform dotTransform;

    [Header("Visibility")]
    public bool requireEyeGaze = true;
    [Tooltip("Show dot using head-direction fallback when eye tracking is unavailable.")]
    public bool showWithHeadFallback = true;
    public bool showForAnyValidEyeGaze = true;
    public bool hideWhenNotOnTarget = false;
    public bool placeAtTargetDepthWhenNoTargetHit = true;

    [Header("Target Gate")]
    [Range(0.01f, 1.5f)]
    public float softTargetRadiusMeters = 0.55f;

    [Header("Dot Shape")]
    [Range(0.005f, 0.20f)]
    public float minDiameterMeters = 0.045f;

    [Range(0.1f, 5.0f)]
    public float angularSizeDeg = 0.85f;

    [Range(0f, 0.10f)]
    public float surfaceOffsetMeters = 0.025f;

    public bool forceRedMaterial = true;

    private Renderer[] _renderers;
    private Collider[] _colliders;
    private Material _redMaterial;
    private Camera _camera;
    private Transform _preparedDotTransform;
    private bool _preparedForceRedMaterial;
    private bool _dotPrepared;

    private void Reset()
    {
        dotTransform = transform;
    }

    private void Awake()
    {
        PrepareDot();
    }

    private void OnEnable()
    {
        PrepareDot();
        SetDotVisible(false);
    }

    private void OnDisable()
    {
        SetDotVisible(false);

        if (_redMaterial != null)
        {
            if (Application.isPlaying) Destroy(_redMaterial);
            else DestroyImmediate(_redMaterial);
            _redMaterial = null;
        }
    }

    private void LateUpdate()
    {
        AutoResolveReferencesIfMissing();
        PrepareDotIfNeeded();

        if (dotTransform == null || gaze == null)
        {
            SetDotVisible(false);
            return;
        }

        bool allowTarget =
            targetAppear != null &&
            targetAppear.state == TargetAppear.ExpState.EXP_SHOW_TARGET &&
            targetAppear.IsTargetVisibleForGaze &&
            targetAppear.CurrentTargetTransform != null;

        Ray gazeRay = gaze.GazeRayWorld;
        bool hasAnyGazeRay = gazeRay.direction.sqrMagnitude > 1e-6f;
        bool eyeOk =
            !requireEyeGaze ||
            (gaze.UsedEyeGazeThisFrame && gaze.HasValidEyeGazeThisFrame) ||
            (showWithHeadFallback && hasAnyGazeRay);

        if (!eyeOk || !hasAnyGazeRay)
        {
            SetDotVisible(false);
            return;
        }

        Vector3 rayDir = gazeRay.direction.normalized;
        Transform targetT = allowTarget ? targetAppear.CurrentTargetTransform : null;

        bool hitCurrent =
            allowTarget &&
            gaze.HasHit &&
            gaze.HitCollider != null &&
            targetAppear.IsColliderFromCurrentTarget(gaze.HitCollider);

        float targetRayDistance = allowTarget
            ? Vector3.Dot(targetT.position - gazeRay.origin, rayDir)
            : 0f;

        bool canProjectToTargetDepth = placeAtTargetDepthWhenNoTargetHit && targetRayDistance > 0f;
        bool nearTargetRay = false;

        if (targetRayDistance > 0f)
        {
            Vector3 closestPoint = gazeRay.origin + rayDir * targetRayDistance;
            nearTargetRay = Vector3.Distance(closestPoint, targetT.position) <= softTargetRadiusMeters;
        }

        if (!allowTarget && !showForAnyValidEyeGaze)
        {
            SetDotVisible(false);
            return;
        }

        if (allowTarget && hideWhenNotOnTarget && !hitCurrent && !nearTargetRay)
        {
            SetDotVisible(false);
            return;
        }

        Vector3 dotPosition;
        bool positionOnSurface = false;

        if (hitCurrent)
        {
            dotPosition = gaze.HitPointWorld;
            positionOnSurface = true;
        }
        else if (!hideWhenNotOnTarget && gaze.HasHit)
        {
            dotPosition = gaze.HitPointWorld;
            positionOnSurface = true;
        }
        else if (canProjectToTargetDepth)
        {
            dotPosition = gazeRay.origin + rayDir * targetRayDistance;
        }
        else if (gaze.HasHit)
        {
            dotPosition = gaze.HitPointWorld;
            positionOnSurface = true;
        }
        else
        {
            dotPosition = gaze.FixationPointWorld;
        }

        if (positionOnSurface)
            dotPosition -= rayDir * surfaceOffsetMeters;

        dotTransform.position = dotPosition;
        dotTransform.localScale = Vector3.one * ComputeWorldDiameter(dotPosition);

        SetDotVisible(true);
    }

    private void AutoResolveReferencesIfMissing()
    {
        if (gaze == null)
            gaze = FindAnyObjectByType<GazeFixationDepthRaycast>();

        if (targetAppear == null)
            targetAppear = FindAnyObjectByType<TargetAppear>();

        if (_camera == null)
            _camera = Camera.main;
    }

    private void PrepareDot()
    {
        if (dotTransform == null)
            dotTransform = transform;

        if (dotTransform == null)
            return;

        _renderers = dotTransform.GetComponentsInChildren<Renderer>(true);
        _colliders = dotTransform.GetComponentsInChildren<Collider>(true);

        int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
        if (ignoreRaycastLayer >= 0)
            dotTransform.gameObject.layer = ignoreRaycastLayer;

        for (int i = 0; i < _colliders.Length; i++)
            _colliders[i].enabled = false;

        if (forceRedMaterial)
            ApplyRedMaterial();

        _preparedDotTransform = dotTransform;
        _preparedForceRedMaterial = forceRedMaterial;
        _dotPrepared = true;
    }

    private void PrepareDotIfNeeded()
    {
        if (_dotPrepared &&
            dotTransform == _preparedDotTransform &&
            forceRedMaterial == _preparedForceRedMaterial)
            return;

        PrepareDot();
    }

    private void ApplyRedMaterial()
    {
        if (_renderers == null || _renderers.Length == 0)
            return;

        if (_redMaterial == null)
        {
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Standard");

            if (shader == null)
                return;

            _redMaterial = new Material(shader);
            _redMaterial.hideFlags = HideFlags.HideAndDontSave;
            _redMaterial.color = Color.red;

            if (_redMaterial.HasProperty("_Color"))
                _redMaterial.SetColor("_Color", Color.red);

            _redMaterial.renderQueue = 5000;
        }

        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].sharedMaterial = _redMaterial;
    }

    private float ComputeWorldDiameter(Vector3 dotPosition)
    {
        Camera cam = _camera != null ? _camera : Camera.main;
        float viewDistance = cam != null
            ? Vector3.Distance(cam.transform.position, dotPosition)
            : 1f;

        float angularDiameter = 2f * viewDistance * Mathf.Tan(angularSizeDeg * 0.5f * Mathf.Deg2Rad);
        return Mathf.Max(minDiameterMeters, angularDiameter);
    }

    private void SetDotVisible(bool visible)
    {
        if (dotTransform == null)
            return;

        if (visible && !dotTransform.gameObject.activeSelf)
            dotTransform.gameObject.SetActive(true);

        if (!dotTransform.gameObject.activeSelf)
            return;

        if (_renderers == null)
            _renderers = dotTransform.GetComponentsInChildren<Renderer>(true);

        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].enabled = visible;
    }
}
