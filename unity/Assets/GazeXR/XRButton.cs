using UnityEngine;
using UnityEngine.XR;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// "Pressed this frame" for a controller button (in the headset) or a keyboard key (in the Editor).
/// Call Down() once per Update.
/// </summary>
public class XRButton
{
    private readonly XRNode node;
    private readonly InputFeatureUsage<bool> usage;
    private readonly string key;
    private bool previous;

    public XRButton(XRNode node, InputFeatureUsage<bool> usage, string key)
    {
        this.node = node;
        this.usage = usage;
        this.key = key;
    }

    public bool Down()
    {
        bool now = false;
        UnityEngine.XR.InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid && device.TryGetFeatureValue(usage, out bool value)) now = value;
        bool down = now && !previous;
        previous = now;
        return down || KeyDown(key);
    }

    /// <summary>Short vibration on the controller, as feedback for a solo user.</summary>
    public void Buzz(float amplitude = 0.5f, float seconds = 0.08f)
    {
        UnityEngine.XR.InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid) device.SendHapticImpulse(0, amplitude, seconds);
    }

    private static bool KeyDown(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return false;
        var control = keyboard.FindKeyOnCurrentKeyboardLayout(key);
        return control != null && control.wasPressedThisFrame;
#else
        return Input.GetKeyDown(key.ToLowerInvariant());
#endif
    }
}
