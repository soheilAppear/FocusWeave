# FocusWeave
FocusWeave is a Unity XR research prototype for gaze-driven, pseudo-varifocal rendering on the Meta Quest. It combines Meta Quest eye tracking, target-aware raycasting, and a depth-aware foveated blur shader to manipulate perceived focus based on where the participant is looking during a distance-judgment task.

The project is built around a simple experimental loop: show a target at a randomized distance, let the participant inspect it, transition to a walking/blank or passthrough state, and record the response before moving to the next trial. While the target is visible, FocusWeave engages a gaze-centered depth-of-field effect only when gaze is on the active target.

FocusWeave 2 adds a physically grounded **chromatic aberration mode** (Thibos LCA model) alongside the original monochrome blur, plus smoother disengage behavior, near-distance blur boost, and live runtime tuning via controller grips.

## Demo

<!-- Upload your new recording here and replace this line -->




## Highlights

- Gaze-driven fixation ray using Meta `OVRPlugin` eye-gaze data, with head-direction fallback modes.
- **Three rendering modes** switchable at runtime: `Off` (post-process bypassed — virtual scene renders normally), `Monochrome` (depth-aware foveated blur), and `Chromatic` (LCA simulation). Inspired by ChromaBlur (Cholewiak et al., SIGGRAPH 2017); this is a forward real-time post-process running at full frame rate, not their offline inverse-deconvolution pipeline.
- **Chromatic aberration simulation** using the Thibos reduced chromatic eye model: red, green, and blue channels receive independent per-channel dioptric offsets (R −0.4 D, G 0 D, B +1.0 D), each sampling the blur mip chain at the depth mismatch for that wavelength.
- Depth-aware foveated blur post-process that uses the camera depth texture and a gaze-centered focus window.
- Target-gated engagement so the blur effect follows only the current trial target.
- **Smooth disengage fade**: after the microsaccade grace window, blur fades out linearly over `disengageFadeSeconds` instead of snapping off, eliminating the texture-switching visual pop.
- **Near-distance blur boost**: separate strength and base-blur multipliers increase blur response for close targets while preserving the far-target look.
- Dynamic blur and fovea tuning from target distance and optional varifocal optical-focus mismatch (diopter-based).
- **Live runtime tuning**: left grip cycles through focus modes; right grip steps through a preset blur-strength ladder.
- Stereo-correct gaze UV: per-eye fixation UV is computed and selected in the shader via the stereo eye index.
- Controller and hand-tracking input paths for running the experiment in-headset.
- A reusable distance-judgment scene with lab assets, target prefabs, passthrough support, and reset points.

## Project Info

- Unity version: `6000.4.5f1`
- Main scene: `Assets/Scenes/SampleScene.unity`
- Target platform: Android / Meta Quest, configured with Quest Pro targeting enabled
- License: MIT

Key Unity packages:

- `com.meta.xr.sdk.all` `71.0.0`
- `com.unity.xr.oculus` `4.5.4`
- `com.unity.xr.management` `4.5.3`
- `com.unity.inputsystem` `1.19.0`
- `com.unity.postprocessing` `3.5.4`
- `com.unity.ai.inference` `2.6.1`

## Repository Layout

```text
Assets/
  Scenes/
    SampleScene.unity                         # Main experiment scene
  pseudoVarifocal/
    GazeFixationDepthRaycast.cs               # Eye/head gaze ray, hit point, depth, debug marker
    GazeDrivenDepthOfFieldPPv2.cs             # Camera post-process driver: mode switching, engagement, chromatic params
    GazeDepthAwareFoveatedBlur.shader         # Fullscreen depth-aware blur; CHROMABLUR_ON keyword enables LCA path
    CurrentTargetOnlyWorldDot.cs              # Optional world-space gaze marker
    HandTrackingTrialInput.cs                 # Pinch gestures for trial/view controls
  DistanceJudgmentMaterial/
    TargetAppear.cs                           # Trial state machine, target placement, view modes
    PlayerControllerRestView.cs               # Legacy/reset helper
    BochaoTargets.prefab                      # Target pool prefab
    New_OpenGL/, Maya/, Bochao_Targets/       # Lab (© Michigan Tech, Scott Kuhl), target, and texture assets
  pretrained_model/
    GazeFixationGate.onnx                     # Included ONNX model asset
Packages/
  manifest.json                               # Unity package dependencies
ProjectSettings/
  ProjectVersion.txt                          # Unity editor version
```

## Getting Started

1. Install Unity `6000.4.5f1`.
2. Clone this repository.
3. Open the repository root in Unity Hub.
4. Let Unity restore packages from `Packages/manifest.json`.
5. Open `Assets/Scenes/SampleScene.unity`.
6. Switch the build target to Android.
7. Confirm XR Plug-in Management is using the Oculus loader.
8. Build and run on a Meta Quest device.

For eye-tracked behavior, use hardware and runtime settings that support eye tracking. The project includes runtime permission handling in `GazeFixationDepthRaycast`; if eye tracking is unavailable, use `EyePreferred_HeadFallback` or `HeadOnly` while developing.

## Running the Experiment

`TargetAppear` controls the trial lifecycle:

1. `EXP_START`: ready state.
2. `EXP_SHOW_TARGET`: resets the rig if configured, selects a trial target, places it at the trial distance, and makes it visible.
3. `EXP_WALK`: hides the target if configured and applies the walking view mode.
4. `EXP_RECORDED`: response/recording pause.
5. `EXP_FINISHED`: all trials complete.

Trial distances are generated from two practice trials plus the main distance list in `TargetAppear.cs`. Targets are selected from the `BochaoTargets` array and are automatically given a collider if needed so gaze raycasts can hit them.

## Controls

Controller input is handled by `TargetAppear`:

| Input | Action |
| --- | --- |
| `A` | Advance experiment state |
| `B` | Go back one state |
| Right index trigger | Reset rig to `BeginPoint` / `resetTransform` |
| `X` | Force virtual-only view |
| `Y` | Force passthrough view |
| Left index trigger | Force blank view |

Runtime blur tuning (handled by `GazeDrivenDepthOfFieldPPv2`):

| Input | Action |
| --- | --- |
| Left grip (press) | Cycle focus mode: Off → Monochrome → Chromatic |
| Right grip (press) | Step blur strength through preset ladder (0.5 → 1.0 → 1.5 … 4.0) |

Hand input is handled by `HandTrackingTrialInput` when controllers are absent, or always when `inputMode` is set to `HandOnly`:

| Gesture | Action |
| --- | --- |
| Right index pinch | Advance experiment state |
| Right middle pinch | Go back one state |
| Left index pinch | Reset rig |
| Left middle pinch | Force blank view |
| Left ring pinch | Force passthrough view |
| Left pinky pinch | Force virtual-only view |
| Left index + middle pinch | Restore view for the current experiment state |

## How the Gaze-Driven Blur Works

`GazeFixationDepthRaycast` produces a world-space gaze ray each frame. It prefers eye tracking when available, can fall back to the headset forward ray, raycasts into the scene, and publishes:

- current gaze ray origin and direction
- whether real eye gaze was used this frame
- hit collider, hit point, hit normal, and hit distance
- smoothed fixation depth
- optional world-space debug marker and headset-visible eye-tracking status

`GazeDrivenDepthOfFieldPPv2` runs as a camera image effect. During `EXP_SHOW_TARGET`, it checks whether gaze is on the current target using collider ownership, depth tolerance, and an optional sustain radius. When engaged, it:

- computes per-eye stereo gaze UVs and selects the correct one in the shader
- builds a mipmapped blur render texture
- sends gaze, focus, fovea, blur, and depth parameters to `Hidden/GazeDepthAwareFoveatedBlur`
- drives a continuous engagement level: holds at 1.0 during the grace window, then fades linearly to 0 over `disengageFadeSeconds` after gaze leaves the target
- updates dynamic fovea degrees and blur multipliers based on target distance and diopter mismatch

### Focus Modes

| Mode | Behavior |
| --- | --- |
| `Off` | Post-process bypassed — virtual scene renders normally, no blur applied |
| `Monochrome` | Depth-aware foveated blur. Single-channel (not color-separated): blur ramps in with angular eccentricity from gaze center and with depth defocus from the focus plane. Controlled by `monochromeBlurStrength`. |
| `Chromatic` | Thibos LCA simulation. Red, green, and blue channels are each sampled from the mip chain at a diopter-driven mip level computed from their respective chromatic offsets. This produces wavelength-dependent blur that approximates the longitudinal chromatic aberration of the human eye. Foveal weight reduces chroma at the fixation center to preserve acuity. Controlled by `chromaticOverallStrength`. |

The shader reads `_CameraDepthTexture`, computes each pixel's diopter defocus from the focus plane, and applies more blur outside the foveal window or when depth differs from the focus plane. In Chromatic mode, the `CHROMABLUR_ON` keyword activates per-channel mip sampling instead of a single blurred value.

## Important Inspector References

On the camera with `GazeDrivenDepthOfFieldPPv2`:

- `gazeSource`: assign the object with `GazeFixationDepthRaycast`
- `targetAppear`: assign the object with `TargetAppear`
- `blurShader`: assign `Hidden/GazeDepthAwareFoveatedBlur`
- `focusMode`: start in `Monochrome` for the classic blur or `Chromatic` for LCA simulation

On `GazeFixationDepthRaycast`:

- `trackingSpace`: assign `OVRCameraRig/TrackingSpace`
- `headFallbackCamera`: assign the HMD camera, or leave it to resolve `Camera.main`
- `ignoreRoot`: assign the rig root so gaze raycasts ignore the user rig and hands
- `gazeMode`: use `EyePreferred_HeadFallback` for development and `EyeOnly` for strict eye-tracking runs

On `TargetAppear`:

- `LabEnvironment`: root of the virtual environment
- `camBlank`: blank/walking view object
- `NormalCam`: normal viewing camera object
- `passthroughLayer`: Meta passthrough layer
- `BochaoTargets`: target pool
- `BeginPoint` or `resetTransform`: participant reset reference
- `player` and `playerHead`: rig root and HMD camera

## Tuning Notes

Useful `GazeDrivenDepthOfFieldPPv2` controls:

- `focusMode`: `Off`, `Monochrome`, or `Chromatic`. Switchable at runtime with left grip.
- `monochromeBlurStrength`: multiplier on top of the computed blur in Monochrome mode.
- `chromaticOverallStrength`: blend strength of the chromatic blur over the sharp image.
- `chromaticOffsetR/G/B`: per-channel dioptric offsets (Thibos defaults: −0.4 / 0.0 / +1.0 D).
- `chromaticBlurStrength`: scales diopters → mip level for chromatic sampling.
- `chromaticFovealWeight`: how much chroma blur is suppressed at the fixation center (0 = none at center, 1 = full).
- `maxChromaticMip`: upper mip clamp for per-channel chromatic samples.
- `requireHitTargetCollider`: require gaze to hit the active target collider.
- `sustainRadiusMeters`: keeps blur alive near the target to reduce flicker.
- `targetDepthToleranceMeters`: controls how close the gaze hit/projection must be to the target depth.
- `smoothTimeAttack`, `smoothTimeDecay`, `disengageGraceSeconds`: shape blur engagement and the grace hold.
- `disengageFadeSeconds`: time to fade from full blur to clear after grace ends. Larger = slower clearing, less pop.
- `snapFocusToTargetDistance`: focuses the shader on the known trial distance instead of raw gaze depth.
- `varifocalOpticalFocusMeters`: optional external optical focus distance in meters.
- `enableNearDistanceBoost`: increases blur response for close targets.
- `nearStrengthBoostAtMinDistance`, `nearBaseBlurBoostAtMinDistance`, `nearBoostPower`: shape the near-distance boost curve.
- `nearBoostPreferMatched`: concentrate the near boost when optical focus matches gaze, preserving far-target appearance.
- `maxMip`, `downsampleBlurTexture`, `useDirectBlurFallback`: quality/performance controls.

## Troubleshooting

- No eye tracking: confirm the device supports eye tracking, the Oculus loader is active, eye tracking is enabled in Meta/Oculus settings, and runtime permission is granted.
- Gaze appears offset: make sure `trackingSpace` is the `OVRCameraRig/TrackingSpace` transform, not a camera or eye anchor.
- Blur never engages: confirm `TargetAppear` is in `EXP_SHOW_TARGET`, the current target has an enabled collider, and `gazeSource.UsedEyeGazeThisFrame` is true if `requireEyeGazeForEngagement` is enabled.
- Chromatic mode looks wrong: check that `blurShader` is assigned and the `CHROMABLUR_ON` keyword is being toggled (visible in Frame Debugger). Confirm `chromaticOffsetR/G/B` and `chromaticBlurStrength` are set to non-zero values.
- Everything raycasts against the player rig: assign `ignoreRoot` on `GazeFixationDepthRaycast` and call `RebuildIgnoreColliderCache` after changing rig colliders.
- Target disappears while walking: this is expected when `hideTargetWhenWalking` is enabled.
- Passthrough view is blank or wrong: check the `OVRPassthroughLayer` reference and the view-mode visibility flags on `TargetAppear`.

## Acknowledgments

The lab environment model used in the distance-judgment scene belongs to Michigan Technological University and was provided by Scott Kuhl. It is included here with permission for use within this research prototype.

## License

This project is released under the MIT License. See `LICENSE` for details.

The MIT license applies to project code authored for FocusWeave. Third-party assets — including the Michigan Tech lab model by Scott Kuhl — retain their original ownership and are used with permission; they are not covered by the MIT license of this repository.
