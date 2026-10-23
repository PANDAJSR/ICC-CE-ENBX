using Ink_Canvas.Plugins;

namespace ICC.CE.ENBX;

/// <summary>可选 SDK 能力探测：旧宿主仍能播放，新宿主提供会话选择工具。</summary>
internal static class HostPlaybackSelectionBridge
{
    public static void Configure(PresentationSourceDescriptor descriptor, Action<bool> onChanged, Action<string> log)
    {
        var type = descriptor.GetType();
        var supported = type.GetProperty("SupportsElementSelection");
        var changed = type.GetProperty("ElementSelectionChanged");
        if (supported?.PropertyType != typeof(bool) || changed?.PropertyType != typeof(Action<bool>))
        {
            log("当前宿主尚不支持放映元素选择；请更新 ICC-CE 以使用选择工具。");
            return;
        }
        changed.SetValue(descriptor, onChanged);
        supported.SetValue(descriptor, true);
    }
}
