using UnityEngine;
using UnityEngine.InputSystem;


[AddComponentMenu("Grassland/Touch Controls Canvas")]
[DisallowMultipleComponent]
public class TouchControlsCanvas : MonoBehaviour
{
    public enum Visibility
    {
        Auto,       // handheld, or a touchscreen with no mouse
        AlwaysOn,   // for iterating in the editor and the Device Simulator
        AlwaysOff,
    }

    public Visibility visibility = Visibility.Auto;

    [Tooltip("The subtree holding the Canvas and EventSystem. Author it INACTIVE so no on-screen " +
             "control initialises where the controls stay hidden.")]
    public GameObject controls;

    private bool? _shown;

    private void Awake()
    {
        if (controls == null)
            Debug.LogError("[TouchControlsCanvas] 'controls' is not assigned, so the touch controls " +
                           "can never be shown.", this);
    }

    // Polled rather than checked once: the Device Simulator installs its Application/SystemInfo
    // shims after scene Awake has already run, and devices can be connected or removed at runtime.
    private void Update()
    {
        if (controls == null) return;

        bool show = ShouldShow();
        if (_shown == show) return;

        _shown = show;
        controls.SetActive(show);
    }

    private bool ShouldShow()
    {
        switch (visibility)
        {
            case Visibility.AlwaysOn: return true;
            case Visibility.AlwaysOff: return false;
            default:
                return Application.isMobilePlatform
                    || SystemInfo.deviceType == DeviceType.Handheld
                    || (Touchscreen.current != null && Mouse.current == null);
        }
    }
}
