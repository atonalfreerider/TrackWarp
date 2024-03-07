namespace VarispeedDemo.SoundTouch;

class SoundTouchProfile(bool useTempo, bool useAntiAliasing)
{
    public bool UseTempo { get; set; } = useTempo;
    public bool UseAntiAliasing { get; set; } = useAntiAliasing;
    public bool UseQuickSeek { get; set; }
}