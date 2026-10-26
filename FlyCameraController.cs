using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using HxCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace CellEditor;

public sealed class FlyCameraController
{
    const double DefaultFlySpeed = 220;

    readonly Viewport3DX viewport;
    readonly Func<HxCamera?> camera;
    readonly Action onCameraChanged;
    readonly HashSet<Key> keysDown = new();
    bool flyMode;
    bool looking;
    Point lastMouse;
    double flySpeed = DefaultFlySpeed;
    bool orbitMoveEnabled = true;
    bool orbitRotationEnabled = true;

    public FlyCameraController(Viewport3DX viewport, Func<HxCamera?> camera, Action onCameraChanged)
    {
        this.viewport = viewport;
        this.camera = camera;
        this.onCameraChanged = onCameraChanged;
        viewport.PreviewKeyDown += OnPreviewKeyDown;
        viewport.PreviewKeyUp += OnPreviewKeyUp;
        viewport.PreviewMouseRightButtonDown += OnRightDown;
        viewport.PreviewMouseRightButtonUp += OnRightUp;
        viewport.PreviewMouseMove += OnMouseMove;
        viewport.LostKeyboardFocus += (_, _) => keysDown.Clear();
        CompositionTarget.Rendering += OnRendering;
    }

    public bool FlyMode
    {
        get => flyMode;
        set
        {
            if (flyMode == value)
            {
                return;
            }

            flyMode = value;
            keysDown.Clear();
            looking = false;
            if (flyMode)
            {
                orbitMoveEnabled = viewport.IsMoveEnabled;
                orbitRotationEnabled = viewport.IsRotationEnabled;
                viewport.IsMoveEnabled = false;
                viewport.IsRotationEnabled = false;
            }
            else
            {
                viewport.IsMoveEnabled = orbitMoveEnabled;
                viewport.IsRotationEnabled = orbitRotationEnabled;
            }
        }
    }

    public double FlySpeed
    {
        get => flySpeed;
        set => flySpeed = Math.Max(1, value);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!flyMode || !IsFlyKey(e.Key))
        {
            return;
        }

        keysDown.Add(e.Key);
        e.Handled = true;
    }

    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!flyMode || !IsFlyKey(e.Key))
        {
            return;
        }

        keysDown.Remove(e.Key);
        e.Handled = true;
    }

    static bool IsFlyKey(Key key)
    {
        return key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.LeftShift or Key.RightShift;
    }

    void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!flyMode)
        {
            return;
        }

        looking = true;
        lastMouse = e.GetPosition(viewport);
        viewport.CaptureMouse();
        e.Handled = true;
    }

    void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!flyMode || !looking)
        {
            return;
        }

        looking = false;
        if (viewport.IsMouseCaptured)
        {
            viewport.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!flyMode || !looking || camera() is not HxCamera cam)
        {
            return;
        }

        var point = e.GetPosition(viewport);
        var dx = point.X - lastMouse.X;
        var dy = point.Y - lastMouse.Y;
        lastMouse = point;
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            return;
        }

        const double degreesPerPixel = 0.22;
        var look = cam.LookDirection;
        look.Normalize();
        var up = cam.UpDirection;
        up.Normalize();
        var right = Vector3D.CrossProduct(look, up);
        right.Normalize();

        var pitch = Matrix3D.Identity;
        pitch.Rotate(new Quaternion(right, -dy * degreesPerPixel));
        look = pitch.Transform(look);
        up = pitch.Transform(up);

        var yaw = Matrix3D.Identity;
        yaw.Rotate(new Quaternion(new Vector3D(0, 1, 0), -dx * degreesPerPixel));
        look = yaw.Transform(look);
        up = yaw.Transform(up);

        if (look.LengthSquared > 1e-12)
        {
            look.Normalize();
            up.Normalize();
            cam.LookDirection = look;
            cam.UpDirection = up;
            onCameraChanged();
        }

        e.Handled = true;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        if (!flyMode || keysDown.Count == 0 || camera() is not HxCamera cam)
        {
            return;
        }

        var look = cam.LookDirection;
        if (look.LengthSquared < 1e-12)
        {
            return;
        }

        look.Normalize();
        var up = cam.UpDirection;
        up.Normalize();
        var right = Vector3D.CrossProduct(look, up);
        right.Normalize();

        var move = new Vector3D();
        if (keysDown.Contains(Key.W))
        {
            move += look;
        }

        if (keysDown.Contains(Key.S))
        {
            move -= look;
        }

        if (keysDown.Contains(Key.D))
        {
            move += right;
        }

        if (keysDown.Contains(Key.A))
        {
            move -= right;
        }

        if (keysDown.Contains(Key.E))
        {
            move += up;
        }

        if (keysDown.Contains(Key.Q))
        {
            move -= up;
        }

        if (move.LengthSquared < 1e-12)
        {
            return;
        }

        move.Normalize();
        var speed = flySpeed;
        if (keysDown.Contains(Key.LeftShift) || keysDown.Contains(Key.RightShift))
        {
            speed *= 3;
        }

        var delta = 1.0 / 60.0;
        cam.Position += move * (speed * delta);
        onCameraChanged();
    }
}
