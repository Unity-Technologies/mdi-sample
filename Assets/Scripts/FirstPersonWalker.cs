using UnityEngine;
using UnityEngine.InputSystem;


[AddComponentMenu("Grassland/First Person Walker")]
[DisallowMultipleComponent]
public class FirstPersonWalker : MonoBehaviour
{
    [Header("Movement")]
    public float walkSpeed = 4.0f;
    public float sprintMultiplier = 2.5f;
    public float acceleration = 12.0f;

    [Header("Look")]
    [Tooltip("Degrees of rotation per pixel of mouse movement. Does not affect stick look.")]
    public float lookSensitivity = 0.4f;
    [Tooltip("Degrees per second at full deflection of a gamepad or on-screen look stick.")]
    public float stickLookSpeed = 140.0f;
    [Range(0.0f, 0.5f)]
    public float stickLookDeadzone = 0.1f;
    [Range(1.0f, 89.0f)]
    public float pitchLimit = 85.0f;
    public bool invertY = false;

    [Header("Ground")]
    public bool stickToGround = true;
    public float eyeHeight = 1.7f;
    public LayerMask groundMask = ~0;
    public float stepSmoothing = 12.0f;

    [Header("Cursor")]
    public bool lockCursor = true;

    private InputAction _move;       // Player/Move       Vector2, -1..1
    private InputAction _look;       // Player/Look       Vector2, per-frame pixel delta (Mouse only)
    private InputAction _lookStick;  // Player/LookStick  Vector2, -1..1 (gamepad + on-screen stick)
    private InputAction _sprint;     // Player/Sprint     Button

    private bool _cursorLockSupported;
    private float _yaw, _pitch;
    private Vector3 _velocity;
    private float _groundY;
    private bool _hasGround;

    private void OnEnable()
    {
        ResolveActions();

        _cursorLockSupported = lockCursor && !Application.isMobilePlatform && Mouse.current != null;

        Vector3 e = transform.eulerAngles;
        _yaw = e.y;
        _pitch = NormalizeAngle(e.x);
        _velocity = Vector3.zero;
        _hasGround = false;

        if (_cursorLockSupported) SetCursor(true);
        WarnIfDriven();
    }

    private void OnDisable()
    {
        SetCursor(false);

        _move = _look = _lookStick = _sprint = null;
    }

    private void ResolveActions()
    {
        InputActionAsset asset = InputSystem.actions;
        if (asset == null)
        {
            Debug.LogError("[FirstPersonWalker] No project-wide input actions are assigned " +
                           "(Project Settings > Input System Package). Expected " +
                           "Assets/Settings/InputSystem_Actions.inputactions.", this);
            return;
        }

        _move = asset.FindAction("Player/Move", throwIfNotFound: false);
        _look = asset.FindAction("Player/Look", throwIfNotFound: false);
        _lookStick = asset.FindAction("Player/LookStick", throwIfNotFound: false);
        _sprint = asset.FindAction("Player/Sprint", throwIfNotFound: false);

        _move?.Enable();
        _look?.Enable();
        _lookStick?.Enable();
        _sprint?.Enable();

        if (_lookStick == null)
            Debug.LogWarning("[FirstPersonWalker] Player/LookStick is missing, so gamepad and " +
                             "on-screen stick look will be dead. Add it to the actions asset as a " +
                             "Vector2 Value action bound to <Gamepad>/rightStick.", this);
    }

    private void WarnIfDriven()
    {
        foreach (var c in GetComponents<Behaviour>())
        {
            if (c == null || !c.enabled || c.GetType().Name != "CinemachineBrain") continue;
            Debug.LogWarning("[FirstPersonWalker] A CinemachineBrain on this camera overwrites the transformation. " +
                             "Disable or remove the CinemachineBrain.", this);
        }
    }

    private void Update()
    {
        HandleCursor();
        Look();
        Move();
    }

    private void HandleCursor()
    {
        if (!_cursorLockSupported) return;

        Keyboard kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            SetCursor(false);
            return;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame &&
            Cursor.lockState != CursorLockMode.Locked)
            SetCursor(true);
    }

    private static void SetCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    private void Look()
    {
        Vector2 degrees = Vector2.zero;

        if (_look != null && (!_cursorLockSupported || Cursor.lockState == CursorLockMode.Locked))
            degrees += _look.ReadValue<Vector2>() * lookSensitivity;

        if (_lookStick != null)
        {
            Vector2 stick = _lookStick.ReadValue<Vector2>();
            if (stick.sqrMagnitude > stickLookDeadzone * stickLookDeadzone)
                degrees += stick * (stickLookSpeed * Time.deltaTime);
        }

        if (degrees == Vector2.zero) return;

        _yaw += degrees.x;
        _pitch += degrees.y * (invertY ? 1.0f : -1.0f);
        _pitch = Mathf.Clamp(_pitch, -pitchLimit, pitchLimit);
        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0.0f);
    }

    private void Move()
    {
        float dt = Time.deltaTime;
        if (dt <= 0.0f) return;

        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(transform.up, Vector3.up).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        Vector2 stick = _move != null ? _move.ReadValue<Vector2>() : Vector2.zero;
        Vector3 wish = forward * stick.y + right * stick.x;
        if (wish.sqrMagnitude > 1.0f) wish.Normalize();   // no diagonal speed bonus

        bool sprinting = _sprint != null && _sprint.IsPressed();
        float speed = walkSpeed * (sprinting ? Mathf.Max(1.0f, sprintMultiplier) : 1.0f);

        Vector3 target = wish * speed;
        if (!stickToGround)
        {
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.eKey.isPressed) target += Vector3.up * speed;
                if (kb.qKey.isPressed) target -= Vector3.up * speed;
            }
        }

        _velocity = Vector3.Lerp(_velocity, target, 1.0f - Mathf.Exp(-Mathf.Max(0.01f, acceleration) * dt));
        Vector3 pos = transform.position + _velocity * dt;

        if (stickToGround)
        {
            Ray ray = new Ray(pos + Vector3.up * 50.0f, Vector3.down);
            if (Physics.Raycast(ray, out RaycastHit hit, 500.0f, groundMask, QueryTriggerInteraction.Ignore))
            {
                _groundY = hit.point.y;
                _hasGround = true;
            }
            if (_hasGround)
            {
                float targetY = _groundY + eyeHeight;
                pos.y = Mathf.Lerp(pos.y, targetY, 1.0f - Mathf.Exp(-Mathf.Max(0.01f, stepSmoothing) * dt));
            }
        }

        transform.position = pos;
    }

    private static float NormalizeAngle(float degrees)
    {
        degrees %= 360.0f;
        return degrees > 180.0f ? degrees - 360.0f : degrees;
    }
}
