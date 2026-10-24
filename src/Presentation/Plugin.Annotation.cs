using Ink_Canvas.Plugins;

namespace ICC.CE.ENBX;

public sealed partial class Plugin
{
    private PresentationSourceDescriptor? _annotationDescriptor;
    private PresentationAnnotationState? _annotationState;
    private string _annotationSessionId = "";

    private void ConfigureWebAnnotation(PresentationSourceDescriptor descriptor)
    {
        _annotationDescriptor = descriptor;
        descriptor.SupportsExternalAnnotation = true;
        descriptor.AnnotationStateChanged = state =>
        {
            _annotationState = state;
            SendAnnotationState();
        };
        descriptor.AnnotationCommand = command =>
        {
            if (_annotationSessionId.Length == 0) return;
            _webView?.PostAnnotation(new
            {
                type = "enbx:annotation-command", sessionId = _annotationSessionId,
                commandId = Guid.NewGuid().ToString("N"), command
            });
        };
    }

    private void SendAnnotationState()
    {
        var state = _annotationState;
        if (state == null || _annotationSessionId.Length == 0) return;
        _webView?.PostAnnotation(new
        {
            type = "enbx:annotation-state", sessionId = _annotationSessionId,
            tool = state.Tool,
            brush = new {
                color = state.Color, width = state.Width, height = state.Height,
                opacity = state.Opacity, pressure = state.Pressure,
                kind = state.BrushKind, tip = state.Tip, inkStyle = state.InkStyle,
                velocityMix = state.VelocityMix, minDistanceScale = state.MinDistanceScale,
                highlighterOverlap = state.HighlighterOverlap, fadeEnabled = state.FadeEnabled,
                fadeDelay = state.FadeDelay, fadeSpeed = state.FadeSpeed,
                smoothing = state.Smoothing, smoothingQuality = state.SmoothingQuality
            },
            eraser = new { width = state.EraserWidth, shape = state.EraserShape }, frozen = state.Frozen
        });
    }

    private void OnAnnotationMessage(EnbxWebMessage message)
    {
        if (message.Type == "enbx:annotation-ready")
        {
            if (message.ProtocolVersion != 1 || message.SessionId.Length == 0) return;
            _annotationSessionId = message.SessionId;
            SendAnnotationState();
            return;
        }
        if (message.SessionId != _annotationSessionId) return;
        if (message.Type == "enbx:annotation-status" && _annotationDescriptor != null)
        {
            _annotationDescriptor.AnnotationStatus = new PresentationAnnotationStatus
            {
                CanUndo = message.CanUndo, CanRedo = message.CanRedo,
                Tool = message.State?.Tool ?? "play",
                Frozen = message.State?.Frozen == true
            };
        }
        if (message.Type == "enbx:annotation-result" && !message.Success)
            _host?.Log("Web annotation command rejected: " + message.Error);
    }
}

public sealed partial class EnbxWebViewLayer
{
    internal void PostAnnotation(object message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Post(message)));
            return;
        }
        Post(message);
    }
}
