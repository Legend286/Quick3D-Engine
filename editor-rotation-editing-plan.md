Core design rule

The transform owns only a normalized quaternion. Pitch/yaw/roll exist solely as editor presentation state.

TransformComponent
└── localRotation : Quaternion   // authoritative

RotationEditorState
├── displayedEulerDegrees        // editor hint/cache
├── textBuffers[3]
├── lastObservedQuaternion
├── transformRevision
└── interaction state

The critical rule is:

Never perform quaternion → Euler → quaternion every frame.

That round trip creates discontinuities, branch changes, ±180° snapping, and instability around singularities.

Euler conversion should happen only:

When an entity is selected.
When its quaternion changes externally.
When a gizmo modifies it.
When undo/redo restores it.
When the user enters Euler values and a new quaternion must be constructed.
1. Establish one explicit rotation convention

Pick one convention and make it an engine-wide contract.

I would recommend:

Pitch: rotation around +X
Yaw: rotation around +Y
Roll: rotation around +Z
Display order: Pitch, Yaw, Roll
Composition convention: YXZ

Assuming active rotations, column vectors and your usual quaternion multiplication semantics:

q = qYaw * qPitch * qRoll;

Do not trust the descriptive wording alone. Add basis-vector tests proving what the multiplication actually does in Q3DE.

Centralise this in one API:

Quaternion QuaternionFromEditorEulerDegrees(
    double pitch,
    double yaw,
    double roll);

Vector3 EditorEulerDegreesFromQuaternionNearest(
    Quaternion rotation,
    Vector3 referenceEuler);

Avoid having separate implementations scattered through the inspector, gizmo and serializer.

2. Quaternion-authoritative transform storage

The runtime transform should contain:

typedef struct TransformComponent
{
    double3 position;
    quatf rotation;
    float3 scale;
    uint64_t revision;
} TransformComponent;

Whenever rotation is assigned:

quatf transform_sanitise_rotation(quatf candidate, quatf previous)
{
    if (!quat_is_finite(candidate))
        return previous;

    float lengthSquared = quat_dot(candidate, candidate);

    if (lengthSquared < 1e-12f)
        return previous;

    candidate *= rsqrt(lengthSquared);

    // q and -q are identical rotations. Keep the representation continuous.
    if (quat_dot(candidate, previous) < 0.0f)
        candidate = -candidate;

    return candidate;
}

The revision should increment whenever the authoritative quaternion changes.

Never store Euler angles in the runtime transform.

3. Add an editor-side rotation presentation cache

Create something resembling:

public sealed class RotationEditorState
{
    public Quaternion LastObservedQuaternion;
    public Vector3d DisplayEulerDegrees;

    public readonly string[] Text =
    {
        "0",
        "0",
        "0"
    };

    public ulong ObservedTransformRevision;

    public bool IsEditing;
    public int ActiveAxis = -1;

    public Quaternion InteractionStartQuaternion;
    public Vector3d InteractionStartEuler;

    public bool Initialised;
}

Store this per inspected entity, not merely as one global inspector value.

A small cache keyed by entity ID is sufficient:

Dictionary<EntityId, RotationEditorState> _rotationStates;

This means an entity that the user entered as:

Pitch: 100°
Yaw:   25°
Roll:  10°

can continue displaying that representation rather than suddenly becoming an equivalent but surprising representation such as:

Pitch: 80°
Yaw:   205°
Roll:  190°
4. Initial quaternion-to-Euler conversion

A quaternion generally has multiple valid Euler representations.

For a non-singular YXZ rotation, calculate:

The canonical Euler solution.
The equivalent alternate solution.
Unwrap both solutions around the previous displayed value.
Select the candidate closest to the previous displayed value.

Conceptually:

Vector3d ToEulerNearest(Quaternion q, Vector3d reference)
{
    Matrix3x3d m = MatrixFromQuaternion(q);

    Vector3d canonical = ExtractCanonicalYXZ(m);
    Vector3d alternate = EquivalentYXZSolution(canonical);

    canonical = UnwrapNear(canonical, reference);
    alternate = UnwrapNear(alternate, reference);

    return DistanceSquared(canonical, reference)
         <= DistanceSquared(alternate, reference)
        ? canonical
        : alternate;
}

For each angle:

double UnwrapNear(double angle, double reference)
{
    return angle + 360.0 * Math.Round((reference - angle) / 360.0);
}

This gives continuous values such as:

178°, 179°, 180°, 181°, 182°

rather than:

178°, 179°, -180°, -179°, -178°
Equivalent solution

For a three-distinct-axis Euler convention, the alternate representation follows the general pattern:

outerA += 180°
middle  = 180° - middle
outerB += 180°

The exact component arrangement must match your YXZ convention and multiplication order.

Build this once and verify it by reconstructing the quaternion:

Quaternion q0 = QuaternionFromEuler(candidate0);
Quaternion q1 = QuaternionFromEuler(candidate1);

AssertRotationEquivalent(q, q0);
AssertRotationEquivalent(q, q1);
5. Explicit singularity handling

Euler angles cannot be made mathematically non-singular. At pitch ±90° in YXZ, yaw and roll represent overlapping degrees of freedom.

The goal is therefore not to pretend the singularity does not exist. The goal is to ensure that:

The quaternion remains completely valid.
No NaNs appear.
The inspector does not randomly jump.
Existing displayed values remain as stable as possible.
User-entered values are preserved.

Use a singularity branch:

double sinPitch = Clamp(-matrix.M12, -1.0, 1.0);
double pitch = Math.Asin(sinPitch);
double cosPitch = Math.Cos(pitch);

if (Math.Abs(cosPitch) > SingularityEpsilon)
{
    // Ordinary extraction of yaw and roll.
}
else
{
    // One degree of freedom has become ambiguous.
    // Preserve one outer angle from the editor reference,
    // then solve the other from the combined matrix rotation.
}

Suggested editor policy:

Preserve the previous roll.
Solve yaw from the remaining combined heading.
If there is no previous editor reference, choose roll = 0.

For the recommended YXZ convention:

At pitch +90°, the matrix contains a combined yaw - roll.
At pitch -90°, it contains a combined yaw + roll.

So the singular path can preserve the previous roll and calculate the matching yaw.

This provides continuity when a gizmo or external system moves through 90°.

Use double for editor-side matrix extraction and angle calculations. The actual stored quaternion can remain float.

A reasonable starting epsilon is:

const double SingularityEpsilon = 1e-7;

Test the threshold rather than assuming that exact value is ideal.

Do not clamp pitch to ±89.9°

That common workaround prevents valid rotations and still does not solve the underlying representation problem.

Only clamp values passed into asin to [-1, +1] to correct floating-point drift.

6. Text-entry state machine

Each text field should permit temporary incomplete text:

-
.
-.
1e
1e-

Do not immediately replace those with zero.

While typing
Update the text buffer.
Attempt to parse it.
If parseable and finite, update the cached Euler component.
Build a quaternion from all three cached Euler values.
Apply the quaternion as a preview.
Do not convert the resulting quaternion back to Euler.
void OnEulerTextChanged(
    Entity entity,
    RotationEditorState state,
    int axis,
    string text)
{
    state.Text[axis] = text;

    if (!TryParseFiniteDouble(text, out double value))
        return;

    state.DisplayEulerDegrees[axis] = value;

    Quaternion candidate = QuaternionFromEditorEulerDegrees(
        state.DisplayEulerDegrees.X,
        state.DisplayEulerDegrees.Y,
        state.DisplayEulerDegrees.Z);

    entity.Transform.SetLocalRotation(candidate);

    state.LastObservedQuaternion = entity.Transform.LocalRotation;
    state.ObservedTransformRevision = entity.Transform.Revision;
}
On Enter or focus loss
If valid: commit the interaction.
If invalid: revert that field to its last valid value.
Format the displayed number without changing its rotational branch.
On Escape

Restore:

state.InteractionStartQuaternion
state.InteractionStartEuler
Numeric dragging

Dragging should modify the cached Euler value directly:

displayEuler[axis] = interactionStartEuler[axis] + mouseDelta * sensitivity;

Then build a fresh quaternion from the complete cached triplet.

Do not repeatedly compose tiny quaternion deltas from each mouse event for an absolute Euler field. That makes results depend on frame rate and input event count.

7. Preserve the user’s exact representation

While an entity remains in the editor cache, keep values such as:

-450°
720°
1080.5°

They are legitimate editor values even though they describe rotations equivalent to smaller angles.

Do not automatically reduce everything into [0, 360) or [-180, 180).

You may provide an explicit context-menu operation:

Normalize displayed angles

That operation could unwrap each field near zero without changing the quaternion.

This should be user-triggered, not automatic.

8. Synchronising external quaternion changes

The transform may change because of:

Rotation gizmo manipulation.
Undo/redo.
Script execution.
Animation preview.
Physics.
Network or collaborative editing.
Parent/world-space manipulation.

At inspector update:

if (!state.IsEditing &&
    transform.Revision != state.ObservedTransformRevision)
{
    Quaternion q = transform.LocalRotation;

    state.DisplayEulerDegrees =
        EditorEulerDegreesFromQuaternionNearest(
            q,
            state.DisplayEulerDegrees);

    RefreshTextBuffers(state);

    state.LastObservedQuaternion = q;
    state.ObservedTransformRevision = transform.Revision;
}

While the user is actively typing, do not overwrite their text buffers.

You should define a conflict policy for external modifications during editing. The simplest sensible policy is:

The active text interaction owns rotation until committed or cancelled.
External updates can increment the revision, but do not replace the active buffers.
Committing the text fields applies the inspector orientation as the latest edit.

For animation or physics-driven objects, consider making the fields read-only unless the user explicitly enters an override/edit mode.

9. Gizmo integration

The gizmo should manipulate quaternions or orthonormal matrices directly.

gizmo result
    ↓
world quaternion
    ↓
convert to local quaternion if required
    ↓
TransformComponent.rotation
    ↓
nearest continuous Euler representation
    ↓
inspector cache

Never make the gizmo go through the inspector Euler values.

For world-space gizmos:

localRotation =
    Inverse(parentWorldRotation) * desiredWorldRotation;

If parent transforms can contain non-uniform scale or shear, do not extract rotation by blindly normalising matrix columns. Use a proper orthogonal/polar decomposition, or constrain the transform hierarchy so shear cannot enter ordinary transforms.

10. Undo and redo

Treat a complete text edit or drag as one transaction.

On field activation:

state.InteractionStartQuaternion = transform.LocalRotation;
state.InteractionStartEuler = state.DisplayEulerDegrees;
undo.BeginTransaction("Rotate Entity");

During editing:

undo.UpdatePreview(...);

On Enter/focus loss:

undo.CommitTransaction(
    beforeQuaternion,
    afterQuaternion);

On Escape:

undo.CancelTransaction();

The undo record should store the quaternion, not Euler values.

After undo/redo, regenerate the displayed Euler using the closest representation to the current cache.

11. Multi-selection behaviour

Multi-selection needs special treatment because each object may have a different Euler branch.

When values differ, display:

Pitch: —
Yaw:   —
Roll:  —

When the user enters one component, process each entity independently:

foreach (Entity entity in selection)
{
    RotationEditorState state = GetRotationState(entity);

    Vector3d euler = ToEulerNearest(
        entity.Transform.LocalRotation,
        state.DisplayEulerDegrees);

    euler[editedAxis] = enteredValue;

    entity.Transform.LocalRotation =
        QuaternionFromEditorEulerDegrees(euler);

    state.DisplayEulerDegrees = euler;
}

Do not take one selected object's yaw and roll and apply them to every other selected object.

For drag edits on mixed values, delta editing is generally friendlier:

newAxisValue = individualStartingAxisValue + dragDelta
12. Serialization

Scene/runtime serialization:

{
  "rotation": {
    "x": 0.0,
    "y": 0.3826834,
    "z": 0.0,
    "w": 0.9238795
  }
}

Do not serialize Euler as the authoritative orientation.

You may optionally serialize an editor-only hint:

{
  "editorRotationHint": {
    "pitch": 0.0,
    "yaw": 405.0,
    "roll": 0.0
  }
}

On load, use the hint only if reconstructing it produces a rotation equivalent to the stored quaternion within tolerance. Otherwise discard it.

That preserves user-authored values such as 405° without allowing stale metadata to alter the transform.

13. Suggested code layout

For Q3DE’s native/runtime and C# editor split:

src/
├── core/
│   └── math/
│       ├── quaternion.h
│       ├── quaternion.c
│       ├── rotation_convention.h
│       └── rotation_convention.c
│
├── scene/
│   ├── transform_component.h
│   └── transform_component.c
│
└── editor/
    ├── Math/
    │   ├── EditorRotation.cs
    │   └── RotationConvention.cs
    │
    ├── Inspectors/
    │   └── TransformInspector.cs
    │
    ├── State/
    │   └── RotationEditorState.cs
    │
    └── Undo/
        └── TransformRotationCommand.cs

Prefer one authoritative conversion implementation. Either:

Expose native conversion helpers to C#, or
Keep editor decomposition managed-only but validate it against native quaternion construction tests.

Do not silently maintain two unrelated Euler conventions.

14. Test suite
Basic round trips

Generate random normalised quaternions:

q
→ nearest Euler
→ reconstructed q

Assert rotational equivalence using:

Math.Abs(Quaternion.Dot(q0, q1)) > 1.0 - epsilon;

The absolute value matters because q and -q are equivalent.

Singularity sweeps

Test pitch values around:

89.0°
89.9°
89.99°
89.999°
90.0°
90.001°
90.01°
90.1°
91.0°

Repeat around -90°, with non-zero yaw and roll.

Assertions:

No NaNs or infinities.
Reconstructed quaternion remains equivalent.
Display changes continuously.
Roll remains near the reference at the exact singularity.
Wrap sweeps

Test:

178° → 182°
358° → 362°
-182° → -178°
719° → 721°

The UI must not jump by 360°.

User entry

Test:

Pitch = 100
Yaw   = 25
Roll  = 10

After applying the quaternion, the inspector must continue showing those values rather than immediately selecting another equivalent branch.

Quaternion sign

Feed the editor alternating values of:

q
-q
q
-q

The displayed Euler must remain unchanged.

Invalid values

Reject without corrupting state:

NaN
Infinity
1e9999
empty committed field
degenerate zero quaternion
Undo

One continuous field drag should produce one undo step, not hundreds.

Final behavioural contract

Once implemented, the system should guarantee:

The runtime never depends on Euler orientation.
All actual rotations remain quaternion-based.
Entering pitch/yaw/roll remains intuitive.
Crossing ±180° does not cause visible wrapping.
Crossing pitch ±90° does not corrupt or snap the transform.
Exact singularities preserve the nearest plausible editor representation.
Gizmos, undo and scripts can update rotation without resetting the inspector branch.
No clamping prevents valid orientations.
No NaNs or zero-length quaternions reach the transform.

The unavoidable limitation is that at an Euler singularity, yaw and roll cannot be independent mathematical coordinates. The editor can preserve continuity and user intent, but it cannot make three Euler channels represent three independent degrees of freedom at pitch ±90°. The authoritative quaternion ensures that this ambiguity never damages the actual orientation.